namespace WpfApp1.Models;

/// <summary>三个设备信息读取业务（WpfApp1诊断架构讨论记录.md §2）。</summary>
public enum DeviceVersionKind
{
    Bootloader,
    Firmware,
    Hardware
}

/// <summary>
/// 一笔诊断业务的时间配置。当前三个版本读取共用：
/// 首次 3D 延迟与节拍 20 ms、业务总截止 t0+5 s、3C 回显等待 1000 ms、N_Cr 1000 ms（§10）。
/// </summary>
public sealed record DiagnosticTiming
{
    public static DiagnosticTiming Default { get; } = new();

    public TimeSpan FirstResponsePollDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan ResponsePollPeriod { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan TotalDeadline { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan EchoWaitTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan ConsecutiveFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);
}

/// <summary>
/// 诊断请求描述：目标 NAD、完整 UDS 请求消息及预期响应规则（§12.3）。
/// 协调层不硬编码业务 DID；SF 分包由 TP 完成。
/// </summary>
public sealed record DiagnosticRequest
{
    public required byte Nad { get; init; }

    /// <summary>完整 UDS 请求消息（不含 NAD 与 PCI），当前为 3 字节的 0x22 服务。</summary>
    public required byte[] UdsRequest { get; init; }

    /// <summary>预期正响应前缀（如 62 F1 95）；长度不足或不匹配按业务响应不匹配结束（§15.1）。</summary>
    public required byte[] ExpectedResponsePrefix { get; init; }

    public DiagnosticTiming Timing { get; init; } = DiagnosticTiming.Default;
}

public enum DiagnosticFailureReason
{
    None,

    /// <summary>t0 后 1000 ms 内未收到有效 3C 回显（§7.3）。</summary>
    EchoTimeout,

    /// <summary>3C 串口写入失败（非断线）。</summary>
    RequestWriteFailed,

    /// <summary>完整响应的服务号/DID 与预期前缀不匹配（§15.1）。</summary>
    ResponseMismatch,

    /// <summary>t0 + 5 s 总截止（§10）。</summary>
    DeadlineExceeded,

    /// <summary>串口断开：当前业务失败并清空排队诊断（§11.5）。</summary>
    Disconnected,

    /// <summary>应用退出/服务释放。</summary>
    Shutdown
}

/// <summary>
/// 一笔诊断业务的最终结果（只发布一次，§11.4）。
/// 失败时可附带 TP 记录的重组放弃原因，供内部排查；与报文窗口显示分开（§7.4）。
/// </summary>
public sealed class DiagnosticResult
{
    private DiagnosticResult(bool success, byte[]? response, DiagnosticFailureReason reason, string? tpAbandonReason)
    {
        Success = success;
        Response = response;
        Reason = reason;
        TpAbandonReason = tpAbandonReason;
    }

    public bool Success { get; }

    /// <summary>完整 UDS 响应消息，不含 NAD、PCI、填充字节和 LIN 校验（§12.3）。</summary>
    public byte[]? Response { get; }

    public DiagnosticFailureReason Reason { get; }

    public string? TpAbandonReason { get; }

    public static DiagnosticResult Succeeded(byte[] response, string? tpAbandonReason = null) =>
        new(true, response, DiagnosticFailureReason.None, tpAbandonReason);

    public static DiagnosticResult Failed(DiagnosticFailureReason reason, string? tpAbandonReason = null) =>
        new(false, null, reason, tpAbandonReason);
}

public enum TpOutcome
{
    /// <summary>无新进展：继续等待、忽略输入或放弃后再次忽略（§14.3）。</summary>
    ContinueWaiting,

    /// <summary>消息收齐，附完整 UDS 消息；一笔消息只返回一次。</summary>
    MessageComplete,

    /// <summary>重组放弃，附首次放弃原因；后续输入不再重启本笔重组。</summary>
    ReassemblyAbandoned
}

/// <summary>TP 处理一帧或一次超时检查后的内部返回结果（§7.4，普通返回值，非事件）。</summary>
public sealed class TpReceiveResult
{
    private TpReceiveResult(TpOutcome outcome, byte[]? message, string? abandonReason)
    {
        Outcome = outcome;
        Message = message;
        AbandonReason = abandonReason;
    }

    public TpOutcome Outcome { get; }

    /// <summary>Outcome 为 MessageComplete 时的完整 UDS 消息。</summary>
    public byte[]? Message { get; }

    /// <summary>Outcome 为 ReassemblyAbandoned 时的放弃原因。</summary>
    public string? AbandonReason { get; }

    public static TpReceiveResult ContinueWaiting { get; } = new(TpOutcome.ContinueWaiting, null, null);

    public static TpReceiveResult Complete(byte[] message) =>
        new(TpOutcome.MessageComplete, message, null);

    public static TpReceiveResult Abandoned(string reason) =>
        new(TpOutcome.ReassemblyAbandoned, null, reason);
}

/// <summary>
/// 单帧写入回执：串口写入完成与否及软件观察到的完成单调时间（§12.3）。
/// 成功仅表示串口写完成，不等同 LIN 总线发送完成。
/// </summary>
public sealed class FrameWriteReceipt
{
    public FrameWriteReceipt(long completedTimestamp)
    {
        CompletedTimestamp = completedTimestamp;
    }

    /// <summary>Stopwatch.GetTimestamp() 体系的完成时刻，用作 t0 等计时锚点。</summary>
    public long CompletedTimestamp { get; }
}

/// <summary>
/// 统一接收入口输出的诊断候选（§13.5）：9 字节原始数据、8 字节 PDU 与经典校验结果。
/// 3C/3D 归属由当前业务上下文判定，入口不虚构诊断 PID。
/// </summary>
public sealed class DiagnosticPacket
{
    public DiagnosticPacket(byte[] raw, byte[] pdu, bool checksumValid, long receivedTimestamp)
    {
        Raw = raw;
        Pdu = pdu;
        ChecksumValid = checksumValid;
        ReceivedTimestamp = receivedTimestamp;
    }

    /// <summary>9 字节：8 字节 PDU + 1 字节经典校验。</summary>
    public byte[] Raw { get; }

    public byte[] Pdu { get; }

    public bool ChecksumValid { get; }

    public long ReceivedTimestamp { get; }
}
