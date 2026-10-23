using System.Buffers.Binary;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 第十四步固件校验的结果（架构/OTA升级流程.md §14.4）：
/// 完整正响应必须恰好 5 字节 `71 01 02 02 Result`；结果 00 通过、01 失败、其他值按未知结果失败处理。
/// 结果 01 属于“肯定响应里的业务失败”，与 `7F 31 NRC` 的结构化负响应分开保存。
/// </summary>
public sealed class FirmwareVerificationResult
{
    private FirmwareVerificationResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText,
        byte[] requestUds,
        byte[]? response,
        byte? resultValue,
        bool routineFailed)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
        RequestUds = requestUds;
        Response = response;
        ResultValue = resultValue;
        RoutineFailed = routineFailed;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（响应长度/结果字节不符等）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>本次实际发送的 16 字节 UDS 请求（不含 NAD、PCI、填充与校验）。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备正响应原始 UDS（含结果字节）；失败时为 null。</summary>
    public byte[]? Response { get; }

    /// <summary>正响应第 5 字节（例程结果）；未取到结果字节时为 null。</summary>
    public byte? ResultValue { get; }

    /// <summary>是否收到“肯定响应但例程结果为失败”（71 01 02 02 01）：不是 NRC（§14.4）。</summary>
    public bool RoutineFailed { get; }

    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static FirmwareVerificationResult Succeeded(byte[] requestUds, byte[] response, byte resultValue) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response, resultValue, false);

    public static FirmwareVerificationResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null,
        byte[]? response = null,
        byte? resultValue = null,
        bool routineFailed = false) =>
        new(false, reason, negativeResponse, detailText, requestUds, response, resultValue, routineFailed);
}

/// <summary>
/// 固件校验业务层（架构/OTA升级流程.md 第十四步）：
/// 只构造 `31 01 02 02 + 起始地址 + 字节长度 + CRC32` 的 16 字节请求并解析结果字节；
/// 回显核对、分片发送、3D 轮询、超时与 78 续期全部由 <see cref="DiagnosticTransactionService"/> 负责。
/// CRC32 由传输快照按实际发送的同一份数据算出（§14.6），不硬编码示例值。
/// </summary>
public sealed class FirmwareVerificationService
{
    /// <summary>RoutineControl 服务号（§14.2）。</summary>
    private const byte RoutineControlServiceId = 0x31;

    /// <summary>StartRoutine 子功能（§14.2）。</summary>
    private const byte StartRoutineSubFunction = 0x01;

    /// <summary>固件校验例程标识 RID 0x0202（§14.2），不同于第十步擦除的 FF00。</summary>
    public const ushort VerificationRoutineId = 0x0202;

    /// <summary>请求长度：SID 1 + 子功能 1 + RID 2 + 地址 4 + 大小 4 + CRC32 4 = 16 字节（§14.2），1FF+2CF。</summary>
    public const int RequestLength = 16;

    /// <summary>正响应固定长度：71 01 02 02 + 结果字节（§14.4）。</summary>
    private const int PositiveResponseLength = 5;

    /// <summary>例程结果为校验通过（§14.1）。</summary>
    public const byte ResultPassed = 0x00;

    /// <summary>例程结果为校验失败（§14.1）。</summary>
    public const byte ResultFailed = 0x01;

    private static readonly byte[] PositiveResponsePrefix =
    [
        (byte)(RoutineControlServiceId + 0x40),
        StartRoutineSubFunction,
        (byte)(VerificationRoutineId >> 8),
        (byte)(VerificationRoutineId & 0xFF)
    ];

    private readonly DiagnosticTransactionService transactionService;

    public FirmwareVerificationService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 第十四步：发送 `31 01 02 02 + 地址 + 大小 + CRC32`（§14.2），
    /// 只认完整 5 字节 `71 01 02 02 Result`；00 通过、01 校验失败、其他结果值按未知失败（§14.4）。
    /// 有效 `7F 31 78` 由协调层续期并继续 3D，本类不重发请求（§14.5）。
    /// </summary>
    public async Task<FirmwareVerificationResult> VerifyAsync(
        byte nad,
        FirmwareTransferPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var udsRequest = new byte[RequestLength];
        udsRequest[0] = RoutineControlServiceId;
        udsRequest[1] = StartRoutineSubFunction;
        BinaryPrimitives.WriteUInt16BigEndian(udsRequest.AsSpan(2, 2), VerificationRoutineId);
        BinaryPrimitives.WriteUInt32BigEndian(udsRequest.AsSpan(4, 4), plan.StartAddress);
        BinaryPrimitives.WriteUInt32BigEndian(udsRequest.AsSpan(8, 4), plan.Length);
        // CRC32 结果按最高字节到最低字节发送，不依赖宿主内存端序（§14.6）。
        BinaryPrimitives.WriteUInt32BigEndian(udsRequest.AsSpan(12, 4), plan.Crc32);

        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = PositiveResponsePrefix,
            // 长度 16 > 6：必须显式给出多帧选项，时序沿用第十步的多帧默认值（§14.5）。
            MultiFrameOptions = new DiagnosticMultiFrameOptions()
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return FirmwareVerificationResult.Failed(
                result.Reason, udsRequest, result.NegativeResponse);
        }

        var response = result.Response!;
        if (response.Length != PositiveResponseLength
            || !response.AsSpan(0, PositiveResponsePrefix.Length).SequenceEqual(PositiveResponsePrefix))
        {
            return FirmwareVerificationResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                detailText: $"校验正响应格式不符：期望 5 字节 71 01 02 02 + 结果，" +
                    $"实际 {response.Length} 字节：{FormatBytes(response)}",
                response: response);
        }

        var resultValue = response[4];
        switch (resultValue)
        {
            case ResultPassed:
                return FirmwareVerificationResult.Succeeded(udsRequest, response, resultValue);

            case ResultFailed:
                // 肯定响应里的业务失败：不能写成 NRC=01，也不是传输层负响应（§14.4）。
                return FirmwareVerificationResult.Failed(
                    DiagnosticFailureReason.ResponseMismatch,
                    udsRequest,
                    detailText: "固件校验失败：设备返回 71 01 02 02 01（例程结果 0x01），" +
                        "不是 NRC；不自动重擦除、重传或复位",
                    response: response,
                    resultValue: resultValue,
                    routineFailed: true);

            default:
                return FirmwareVerificationResult.Failed(
                    DiagnosticFailureReason.ResponseMismatch,
                    udsRequest,
                    detailText: $"校验结果未知：0x{resultValue:X2}（原始 UDS {FormatBytes(response)}），按失败处理",
                    response: response,
                    resultValue: resultValue);
        }
    }

    private static string FormatBytes(byte[] values) => BitConverter.ToString(values).Replace('-', ' ');
}
