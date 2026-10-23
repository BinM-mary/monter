using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 第十五步设备复位的结果（架构/OTA升级流程.md §15.3、§15.4）：
/// 完整正响应必须恰好 2 字节 `51 01`，子功能必须回显 01；
/// `51 02` 等其他子功能、错误长度或非 78 负响应都不判成功。
/// </summary>
public sealed class EcuResetResult
{
    private EcuResetResult(
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

    /// <summary>补充说明（响应长度/子功能不符等）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>本次实际发送的 2 字节 UDS 请求 `11 01`（不含 NAD、PCI、填充与校验）。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备正响应原始 UDS（恰好 `51 01`）；失败时为 null。</summary>
    public byte[]? Response { get; }

    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static EcuResetResult Succeeded(byte[] requestUds, byte[] response) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response);

    public static EcuResetResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText, requestUds, null);
}

/// <summary>
/// 设备复位业务层（架构/OTA升级流程.md 第十五步）：
/// 只构造两字节 `11 01` 请求并核对完整正响应恰为 `51 01`；
/// 3C 回显核对、3D 轮询、超时与 `7F 11 78` 续期全部由
/// <see cref="DiagnosticTransactionService"/> 负责，本类不新建发送出口、不重发复位。
/// </summary>
public sealed class EcuResetService
{
    /// <summary>ECUReset 服务号（§15.1）。</summary>
    public const byte ResetServiceId = 0x11;

    /// <summary>复位子功能 01（§15.1）。</summary>
    public const byte ResetSubFunction = 0x01;

    /// <summary>请求长度：SID 1 + 子功能 1 = 2 字节，走 SF（§15.1）。</summary>
    public const int RequestLength = 2;

    /// <summary>正响应固定长度：`51 01` 恰好 2 字节（§15.3）。</summary>
    private const int PositiveResponseLength = 2;

    /// <summary>正响应服务号（0x11 + 0x40）与子功能回显（§15.1）。</summary>
    private static readonly byte[] PositiveResponse =
    [
        ResetServiceId + 0x40,
        ResetSubFunction
    ];

    private readonly DiagnosticTransactionService transactionService;

    public EcuResetService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 第十五步：发送两字节 `11 01`（§15.1、§15.2），只认完整 2 字节 `51 01`（§15.3）。
    /// 有效 `7F 11 78` 由协调层续期 5 s 并继续 3D，本类不重发复位（§15.4）；
    /// 非 78 负响应按既有契约返回，由编排层记录原始 UDS、NRC 与中文原因后退出。
    /// </summary>
    public async Task<EcuResetResult> ResetAsync(
        byte nad,
        CancellationToken cancellationToken = default)
    {
        var udsRequest = new[] { ResetServiceId, ResetSubFunction };
        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = PositiveResponse,
            // 两字节请求走既有 SF 时序：t0 + 20 ms 首拍、20 ms 节拍、回显 1000 ms、总截止 5 s（§15.3）。
            Timing = DiagnosticTiming.Default
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return EcuResetResult.Failed(result.Reason, udsRequest, result.NegativeResponse);
        }

        var response = result.Response!;
        if (response.Length != PositiveResponseLength
            || !response.AsSpan(0, PositiveResponse.Length).SequenceEqual(PositiveResponse))
        {
            return EcuResetResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                detailText: $"复位正响应格式不符：期望 2 字节 51 01，" +
                    $"实际 {response.Length} 字节：{FormatBytes(response)}");
        }

        return EcuResetResult.Succeeded(udsRequest, response);
    }

    private static string FormatBytes(byte[] values) => BitConverter.ToString(values).Replace('-', ' ');
}
