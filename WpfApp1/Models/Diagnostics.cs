namespace WpfApp1.Models;

/// <summary>三个设备信息读取业务（架构/WpfApp1诊断架构讨论记录.md §2）。</summary>
public enum DeviceVersionKind
{
    Bootloader,
    Firmware,
    Hardware
}

/// <summary>
/// 一笔 SF 诊断业务的时间配置。当前三个版本读取共用：
/// 3D 首拍与节拍均按 <see cref="ResponsePollPeriod"/>、业务总截止 t0+5 s、
/// 3C 回显等待 1000 ms、N_Cr 1000 ms（§10）。
/// 多帧请求不使用本类型，改由 <see cref="DiagnosticMultiFrameOptions"/> 提供参数。
/// </summary>
public sealed record DiagnosticTiming
{
    public static DiagnosticTiming Default { get; } = new();

    /// <summary>保留字段：SF 路径当前不使用；多帧首次 3D 延迟见 <see cref="DiagnosticResponseTiming"/>。</summary>
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

    /// <summary>SF（1～6 字节）路径的原有时间配置；多帧请求完全忽略该属性（实施计划 §4.10）。</summary>
    public DiagnosticTiming Timing { get; init; } = DiagnosticTiming.Default;

    /// <summary>
    /// 多帧（长度 &gt; 6）请求必须显式提供的选项；SF 请求保持为 null。
    /// 长度 &gt; 6 且为 null 时入队前以 InvalidRequest 拒绝，不回退到 <see cref="Timing"/>。
    /// </summary>
    public DiagnosticMultiFrameOptions? MultiFrameOptions { get; init; }
}

public enum DiagnosticFailureReason
{
    None,

    /// <summary>t0 后 1000 ms 内未收到有效 3C 回显（§7.3）。</summary>
    EchoTimeout,

    /// <summary>3C 串口写入失败（非断线）。</summary>
    RequestWriteFailed,

    /// <summary>
    /// 完整响应的服务号/DID 与预期前缀不匹配（§15.1）；残缺负响应或原 SID 不符的 7F 也按此报告。
    /// </summary>
    ResponseMismatch,

    /// <summary>原 SF 路径 t0 + 5 s 总截止（§10）；多帧不复用该值隐藏失败阶段。</summary>
    DeadlineExceeded,

    /// <summary>串口断开：当前业务失败并清空排队诊断（§11.5）。</summary>
    Disconnected,

    /// <summary>应用退出/服务释放。</summary>
    Shutdown,

    /// <summary>单片写入等待超时，可能存在在途未知状态（实施计划 §4.9.2）。</summary>
    RequestWriteTimeout,

    /// <summary>发送阶段预算耗尽（实施计划 §4.3、§4.9.2）。</summary>
    SendDeadlineExceeded,

    /// <summary>不能满足最大相邻分片间隔（实施计划 §4.9.2）。</summary>
    InterFrameDeadlineExceeded,

    /// <summary>多帧请求的响应预算耗尽，包含固定回显隔离期（实施计划 §4.3）。</summary>
    ResponseDeadlineExceeded,

    /// <summary>已确认的归属规则被破坏，或遇到不能安全交付 TP 的模糊候选（实施计划 §4.5）。</summary>
    ProtocolAttributionError,

    /// <summary>调用者取消（实施计划 §4.11）。</summary>
    Cancelled,

    /// <summary>前序事务中止整批而清空队列，或处于失败收尾期；本请求未发送（实施计划 §4.4.1）。</summary>
    QueueAborted,

    /// <summary>跨事务门禁已关闭、诊断准入暂停；需显式复位或重启（实施计划 §4.4.2）。</summary>
    DiagnosticGateClosed,

    /// <summary>长度、必填多帧选项或参数组合非法，发送前拒绝（实施计划 §4.9.2）。</summary>
    InvalidRequest,

    /// <summary>
    /// 设备返回完整负响应 7F + 原 SID + NRC（架构/OTA升级流程.md §6.4）。
    /// 原始 UDS、原服务号与 NRC 由 <see cref="DiagnosticResult.NegativeResponse"/> 携带；
    /// 本值仅追加在末尾，不改变既有枚举编号。
    /// </summary>
    NegativeResponse
}

/// <summary>
/// 一条完整的 UDS 负响应 7F + 原服务号 + NRC（架构/OTA升级流程.md §6.4）：
/// 保留原请求 SID、NRC 与原始字节，未知 NRC 也保留数值，不丢数据、不虚构含义。
/// </summary>
public sealed class DiagnosticNegativeResponse
{
    public DiagnosticNegativeResponse(byte requestServiceId, byte nrc, byte[] rawUds)
    {
        RequestServiceId = requestServiceId;
        Nrc = nrc;
        RawUds = rawUds ?? throw new ArgumentNullException(nameof(rawUds));
    }

    /// <summary>被拒绝或待处理的原请求服务号（进入扩展会话时为 0x10）。</summary>
    public byte RequestServiceId { get; }

    /// <summary>负响应码 NRC（第三个字节），不是原请求服务号，也不等于 NAD。</summary>
    public byte Nrc { get; }

    /// <summary>完整 UDS 负响应原始字节（7F + 原 SID + NRC），不含 NAD、PCI、填充与 LIN 校验。</summary>
    public byte[] RawUds { get; }

    /// <summary>原始 UDS 的十六进制文本，例如 7F 10 22。</summary>
    public string RawUdsText => BitConverter.ToString(RawUds).Replace('-', ' ');

    /// <summary>NRC 数值与中文原因，例如 NRC=0x22，执行条件不满足。</summary>
    public string NrcText => DiagnosticNrcText.Describe(Nrc);

    /// <summary>日志用的一行说明：原始 UDS + NRC 数值与中文原因，例如 7F 10 22；NRC=0x22，执行条件不满足。</summary>
    public string Describe() => $"{RawUdsText}；{NrcText}";
}

/// <summary>
/// 收到有效 NRC 0x78（响应待定）并已续期响应窗口后的提示信息（架构/OTA升级流程.md §6.5）。
/// 只用于日志提示，不是失败结果，也不改变调用方等待：事务仍在同一次 <c>ExecuteAsync</c> 内继续等待最终响应。
/// </summary>
public sealed record DiagnosticResponsePendingNotice
{
    /// <summary>本次 OTA/诊断的目标 NAD（与 NRC 0x78 不是同一个概念）。</summary>
    public required byte Nad { get; init; }

    /// <summary>被待处理的原请求服务号，例如扩展会话的 0x10。</summary>
    public required byte RequestServiceId { get; init; }

    /// <summary>负响应码，恒为 0x78。</summary>
    public required byte Nrc { get; init; }

    /// <summary>本笔第几次收到有效 78（从 1 开始，供日志合并与排查）。</summary>
    public required int PendingCount { get; init; }

    /// <summary>本次刷新后的响应等待窗口长度（默认配置下为 5 s，取自本笔时间参数）。</summary>
    public required TimeSpan WaitWindow { get; init; }

    /// <summary>原始 UDS 的十六进制文本，例如 7F 10 78。</summary>
    public string RawUdsText => $"7F {RequestServiceId:X2} {Nrc:X2}";

    /// <summary>NRC 数值与中文原因，例如 NRC=0x78，请求已正确接收，最终响应待定。</summary>
    public string NrcText => DiagnosticNrcText.Describe(Nrc);
}

/// <summary>
/// NRC 的中文说明（架构/OTA升级流程.md §6.4 建议的统一映射）：
/// 供 OTA 升级日志与版本读取失败文案复用，未知 NRC 仍显示数值。
/// </summary>
public static class DiagnosticNrcText
{
    public static string Describe(byte nrc) => nrc switch
    {
        0x11 => "NRC=0x11，服务不支持",
        0x12 => "NRC=0x12，子功能不支持",
        0x13 => "NRC=0x13，消息长度错误或格式无效",
        0x21 => "NRC=0x21，设备忙，请求需稍后重试",
        0x22 => "NRC=0x22，执行条件不满足",
        0x24 => "NRC=0x24，请求顺序错误",
        0x31 => "NRC=0x31，请求参数超出范围",
        0x33 => "NRC=0x33，安全访问被拒绝",
        0x35 => "NRC=0x35，密钥无效",
        0x36 => "NRC=0x36，超过允许的尝试次数",
        0x37 => "NRC=0x37，要求的等待时间尚未结束",
        0x70 => "NRC=0x70，上传/下载请求不被接受",
        0x73 => "NRC=0x73，数据块序号错误",
        // 响应待定：由协调层续期响应窗口并继续接收最终响应（§6.5），不按失败退出。
        0x78 => "NRC=0x78，请求已正确接收，最终响应待定",
        0x7E => "NRC=0x7E，当前会话不支持该子功能",
        _ => $"NRC=0x{nrc:X2}，未知负响应"
    };
}

/// <summary>
/// 诊断失败原因的界面文案（§11.4）：供设备版本读取与 OTA 流程共用，避免同一枚举在多处各写一套文本。
/// </summary>
public static class DiagnosticFailureText
{
    public static string Describe(DiagnosticFailureReason reason) => reason switch
    {
        DiagnosticFailureReason.EchoTimeout => "未收到 3C 回显",
        DiagnosticFailureReason.RequestWriteFailed => "诊断请求发送失败",
        DiagnosticFailureReason.RequestWriteTimeout => "诊断请求写入超时",
        DiagnosticFailureReason.ResponseMismatch => "设备响应与预期不符",
        DiagnosticFailureReason.DeadlineExceeded => "等待设备响应超时",
        DiagnosticFailureReason.ResponseDeadlineExceeded => "等待设备响应超时",
        DiagnosticFailureReason.SendDeadlineExceeded => "诊断请求发送超时",
        DiagnosticFailureReason.InterFrameDeadlineExceeded => "诊断分片间隔超限",
        DiagnosticFailureReason.ProtocolAttributionError => "诊断响应归属异常",
        DiagnosticFailureReason.Cancelled => "诊断已取消",
        DiagnosticFailureReason.QueueAborted => "诊断已中止",
        DiagnosticFailureReason.DiagnosticGateClosed => "诊断已锁止，需复位或重启",
        DiagnosticFailureReason.InvalidRequest => "诊断请求参数非法",
        DiagnosticFailureReason.NegativeResponse => "设备返回负响应",
        DiagnosticFailureReason.Disconnected => "串口已断开",
        DiagnosticFailureReason.Shutdown => "诊断已终止",
        // 未知未来枚举保留兜底，但显示原因标识，避免掩盖已知值遗漏（实施计划 §4.9.2）。
        _ => $"读取失败（{reason}）"
    };
}

/// <summary>事务阶段，用于进度快照与 FailedPhase 断言；不使用自由文本阶段名（实施计划 §4.9.1）。</summary>
public enum DiagnosticPhase
{
    /// <summary>入队前校验；InvalidRequest、预取消、恢复门禁拒绝时使用。</summary>
    Validating,

    /// <summary>已入队尚未执行；排队取消、断线或被整批清空时使用。</summary>
    Queued,

    /// <summary>已出队，正在获取独占且尚未发送。</summary>
    EnteringDiagnostic,

    /// <summary>请求写入、逐片确认或帧间隔等待；包括 SF 请求写入阶段。</summary>
    SendingRequest,

    /// <summary>多帧发送完成后的固定回显隔离。</summary>
    EchoDrain,

    /// <summary>响应读取与重组；SF 并行等待有效回显和响应也归此阶段。</summary>
    WaitingResponse,

    /// <summary>本笔仍待完成时执行必要的失败恢复/跨事务隔离。</summary>
    Recovery,

    /// <summary>本笔仍待完成时处理收尾。</summary>
    Finalizing
}

/// <summary>
/// 一笔事务的不可变进度快照（实施计划 §4.9.1）。索引从 0 开始；
/// 计数关系：EchoConfirmedCount、SerialWriteCompletedCount 均不得超过 WriteAttemptCount。
/// </summary>
public sealed record DiagnosticProgress
{
    public static DiagnosticProgress Empty { get; } = new();

    /// <summary>校验/分包成功后预计 PDU 数；尚未确定时为 null。</summary>
    public int? TotalPduCount { get; init; }

    /// <summary>已调用发送出口的 PDU 数，含当前失败或未知完成状态的一片。</summary>
    public int WriteAttemptCount { get; init; }

    /// <summary>已取得成功写入回执的 PDU 数，不等于已上总线或设备已接收。</summary>
    public int SerialWriteCompletedCount { get; init; }

    /// <summary>已确认有效回显的 PDU 数，重复回显不累计。</summary>
    public int EchoConfirmedCount { get; init; }

    /// <summary>成功写入分片内的有效 UDS 字节之和，排除 NAD、PCI、填充和校验。</summary>
    public int UdsBytesWriteCompleted { get; init; }

    /// <summary>结束时当前操作分片索引；尚未开始或无当前分片时为 null。</summary>
    public int? CurrentPduIndex { get; init; }

    /// <summary>失败发生阶段；成功时为 null。终止原因首次胜出时快照，收尾不得覆盖。</summary>
    public DiagnosticPhase? FailedPhase { get; init; }

    /// <summary>最终结果确定时是否仍有未取得明确结果的写入，避免把未知误报为未发送。</summary>
    public bool InFlightWriteAtCompletion { get; init; }
}

/// <summary>
/// 一笔诊断业务的最终结果（只发布一次，§11.4）。
/// 失败时可附带 TP 记录的重组放弃原因与设备负响应原始数据，供内部排查与业务提示；
/// 与报文窗口显示分开（§7.4）。
/// </summary>
public sealed class DiagnosticResult
{
    private DiagnosticResult(
        bool success,
        byte[]? response,
        DiagnosticFailureReason reason,
        string? tpAbandonReason,
        DiagnosticProgress progress,
        DiagnosticNegativeResponse? negativeResponse)
    {
        Success = success;
        Response = response;
        Reason = reason;
        TpAbandonReason = tpAbandonReason;
        Progress = progress;
        NegativeResponse = negativeResponse;
    }

    public bool Success { get; }

    /// <summary>完整 UDS 响应消息，不含 NAD、PCI、填充字节和 LIN 校验（§12.3）。</summary>
    public byte[]? Response { get; }

    public DiagnosticFailureReason Reason { get; }

    /// <summary>设备负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    public string? TpAbandonReason { get; }

    /// <summary>最终进度快照（实施计划 §4.9.1）；结果发布后不再变化。</summary>
    public DiagnosticProgress Progress { get; }

    public static DiagnosticResult Succeeded(
        byte[] response,
        DiagnosticProgress? progress = null,
        string? tpAbandonReason = null) =>
        new(true, response, DiagnosticFailureReason.None, tpAbandonReason,
            progress ?? DiagnosticProgress.Empty, null);

    public static DiagnosticResult Failed(
        DiagnosticFailureReason reason,
        string? tpAbandonReason = null,
        DiagnosticProgress? progress = null,
        DiagnosticNegativeResponse? negativeResponse = null) =>
        new(false, null, reason, tpAbandonReason, progress ?? DiagnosticProgress.Empty, negativeResponse);
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

    /// <summary>与诊断模块同源 <see cref="TimeProvider"/> 的完成时刻，用作 t0 等计时锚点。</summary>
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
