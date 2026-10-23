using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>一次会话切换的结果：成败、失败原因与设备负响应原始数据（供升级日志显示）。</summary>
public sealed class DiagnosticSessionResult
{
    private DiagnosticSessionResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（如正响应长度不符）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>失败文案：优先补充说明，其次负响应原始数据，最后按失败原因映射。</summary>
    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static DiagnosticSessionResult Succeeded() =>
        new(true, DiagnosticFailureReason.None, null, null);

    public static DiagnosticSessionResult Failed(
        DiagnosticFailureReason reason,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText);
}

/// <summary>
/// 诊断会话控制业务层（架构/OTA升级流程.md 第六步、第七步）：
/// 只做请求构造与正响应格式判定，不处理 TP、调度、超时与 OTA 独占，也不自行调度串口。
/// 正响应 P2/P2* 数据（后四字节）按已确认要求不解析、不建立会话属性、不改变任何计时。
/// </summary>
public sealed class DiagnosticSessionService
{
    /// <summary>DiagnosticSessionControl 服务号（§6.2）。</summary>
    private const byte DiagnosticSessionControl = 0x10;

    /// <summary>扩展诊断会话子功能（§6.2、§6.3）；进入它不等于已解锁或允许下载。</summary>
    private const byte ExtendedSessionType = 0x03;

    /// <summary>编程会话子功能（§7.2）；进入它同样不等于已解锁，安全访问属第八步及后续。</summary>
    private const byte ProgrammingSessionType = 0x02;

    /// <summary>会话控制正响应服务号（§6.3）。</summary>
    private const byte PositiveResponseServiceId = 0x50;

    /// <summary>会话控制正响应的完整 UDS 字节数：50 + 子功能 + P2(2) + P2*(2)（§6.3、§7.3）。</summary>
    private const int SessionPositiveResponseLength = 6;

    private readonly DiagnosticTransactionService transactionService;

    public DiagnosticSessionService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 发送 10 03 进入扩展诊断会话。成功要求有效 3C 回显、完整 UDS 长度为 6 字节且前两字节为 50 03；
    /// 后四字节作为未解释数据接收，不检查数值、不参与计时（§6.3）。
    /// </summary>
    public Task<DiagnosticSessionResult> EnterExtendedSessionAsync(
        byte nad,
        CancellationToken cancellationToken = default) =>
        EnterSessionAsync(ExtendedSessionType, nad, cancellationToken);

    /// <summary>
    /// 发送 10 02 进入编程会话（§7.1）。判定规则与扩展会话完全一致：有效 3C 回显 +
    /// 完整 UDS 长度 6 字节且前两字节为 50 02；后四字节 P2/P2* 不解析、不参与计时。
    /// 进入编程会话不等于已解锁或允许下载，安全访问与下载类服务不在本方法内。
    /// </summary>
    public Task<DiagnosticSessionResult> EnterProgrammingSessionAsync(
        byte nad,
        CancellationToken cancellationToken = default) =>
        EnterSessionAsync(ProgrammingSessionType, nad, cancellationToken);

    /// <summary>
    /// 会话切换的共用实现：请求 10 + 子功能，预期正响应 50 + 子功能，完整 UDS 恰为 6 字节。
    /// 子功能只影响请求字节、预期前缀与失败说明文案，其余（时序、回显、负响应）全部由协调层处理。
    /// </summary>
    private async Task<DiagnosticSessionResult> EnterSessionAsync(
        byte sessionType,
        byte nad,
        CancellationToken cancellationToken)
    {
        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = [DiagnosticSessionControl, sessionType],
            ExpectedResponsePrefix = [PositiveResponseServiceId, sessionType]
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return DiagnosticSessionResult.Failed(result.Reason, result.NegativeResponse);
        }

        // 协调层按前缀核对；长度仍需在业务层按已确认格式判定，短于或长于 6 字节都不能误报成功。
        var response = result.Response!;
        if (response.Length != SessionPositiveResponseLength)
        {
            return DiagnosticSessionResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                detailText: $"10 {sessionType:X2} 正响应长度为 {response.Length} 字节，预期 {SessionPositiveResponseLength} 字节");
        }

        return DiagnosticSessionResult.Succeeded();
    }
}
