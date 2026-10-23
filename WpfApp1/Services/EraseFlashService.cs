using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 一次擦除请求（第十步）的结果：成败、失败原因、设备负响应原始数据，
/// 以及设备正响应的原始字节与状态记录。状态记录只原样保存，不解释其业务含义（§10.6）。
/// </summary>
public sealed class EraseResult
{
    private EraseResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText,
        byte[] requestUds,
        byte[]? response)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
        RequestUds = requestUds;
        Response = response;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（如正响应长度不符）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>本次实际发送的 12 字节 UDS 请求（不含 NAD、PCI、填充与校验），供日志逐字节追溯。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备正响应原始 UDS；失败时为 null。</summary>
    public byte[]? Response { get; }

    /// <summary>
    /// 正响应中 71 01 FF 00 之后的状态记录（可能为空数组）。
    /// 设备契约未确认前不解释其含义，也不据此判定“擦除完成”（§10.6）。
    /// </summary>
    public byte[] StateRecord => Response is { Length: > 4 } response ? response[4..] : [];

    /// <summary>状态记录的十六进制文本；无状态记录时为空字符串。</summary>
    public string StateRecordText => StateRecord.Length == 0
        ? string.Empty
        : BitConverter.ToString(StateRecord).Replace('-', ' ');

    /// <summary>
    /// 是否收到设备对擦除例程的接受（71 01 FF 00）。
    /// 该值只表示“例程已启动/被接受”，**不等于** Flash 已擦除完成（§10.6）。
    /// </summary>
    public bool RequestAccepted => Success;

    /// <summary>失败文案：优先补充说明，其次负响应原始数据，最后按失败原因映射。</summary>
    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static EraseResult Succeeded(byte[] requestUds, byte[] response) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response);

    public static EraseResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText, requestUds, null);
}

/// <summary>
/// 擦除业务层（架构/OTA升级流程.md 第十步）：
/// 只构造 31 01 FF 00 + 起始地址 + 字节长度的 12 字节请求，并提供显式多帧配置；
/// 回显核对、分片发送、3D 轮询、超时与 78 续期全部由 <see cref="DiagnosticTransactionService"/> 负责，
/// 本类不新增发送出口、不重发擦除、不追加 31 03 结果查询（§10.5、§10.6）。
/// </summary>
public sealed class EraseFlashService
{
    /// <summary>RoutineControl 正响应服务号（§10.6）。</summary>
    private const byte PositiveResponseServiceId = 0x71;

    /// <summary>
    /// 正响应前缀：71 + 控制类型回显 + RID 回显（§10.6）。
    /// 命中只说明设备接受了 StartRoutine；完成状态还需设备契约确认。
    /// </summary>
    private static readonly byte[] PositiveResponsePrefix =
    [
        PositiveResponseServiceId,
        ErasePlan.StartRoutineSubFunction,
        (byte)(ErasePlan.EraseRoutineId >> 8),
        (byte)(ErasePlan.EraseRoutineId & 0xFF)
    ];

    /// <summary>正响应最短长度：71 01 FF 00（§10.6）。</summary>
    private const int MinimumPositiveResponseLength = 4;

    private readonly DiagnosticTransactionService transactionService;

    public EraseFlashService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 发送第十步擦除请求（§10.2、§10.5）。请求固定 12 字节，必须提供 <see cref="DiagnosticMultiFrameOptions"/>；
    /// 时序复用多帧默认值（单片写入 500 ms、单片回显 1000 ms、隔离 50 ms、响应预算 5 s、3D 20 ms 节拍），
    /// 有效 7F 31 78 由协调层续期并继续 3D，本类不重发。正响应只核对 71 01 FF 00，
    /// 其后状态记录原样返回，不解释、不作为“擦除完成”判据（§10.6）。
    /// </summary>
    public async Task<EraseResult> EraseAsync(
        byte nad,
        ErasePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var udsRequest = plan.ToUdsRequest();
        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = PositiveResponsePrefix,
            // 长度 12 > 6：必须显式给出多帧选项，SF 的 DiagnosticTiming 不参与（§10.5）。
            MultiFrameOptions = new DiagnosticMultiFrameOptions()
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return EraseResult.Failed(result.Reason, udsRequest, result.NegativeResponse);
        }

        // 协调层按前缀核对；这里再显式确认长度与四字节前缀，避免契约变化时误报接受。
        var response = result.Response!;
        if (response.Length < MinimumPositiveResponseLength
            || !response.AsSpan(0, MinimumPositiveResponseLength).SequenceEqual(PositiveResponsePrefix))
        {
            return EraseResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                detailText: $"31 01 正响应不是 71 01 FF 00（实际 {response.Length} 字节：{FormatBytes(response)}）");
        }

        return EraseResult.Succeeded(udsRequest, response);
    }

    private static string FormatBytes(byte[] values) => BitConverter.ToString(values).Replace('-', ' ');
}
