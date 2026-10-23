using System.Buffers.Binary;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 第十一步申请下载的结果（架构/OTA升级流程.md §11.1、§11.2）：
/// 成败、失败原因、设备负响应原始数据，以及 74 正响应解析出的设备最大块长与本阶段每包容量。
/// </summary>
public sealed class DownloadRequestResult
{
    private DownloadRequestResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText,
        byte[] requestUds,
        byte[]? response,
        uint? maxNumberOfBlockLength,
        int payloadCapacity)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
        RequestUds = requestUds;
        Response = response;
        MaxNumberOfBlockLength = maxNumberOfBlockLength;
        PayloadCapacity = payloadCapacity;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（如 74 长度格式不符）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>本次实际发送的 11 字节 UDS 请求（不含 NAD、PCI、填充与校验）。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备 74 正响应原始 UDS；失败或解析前格式不符时为 null。</summary>
    public byte[]? Response { get; }

    /// <summary>设备返回的最大块长（UDS 总长，含 SID 与块号）；未成功解析时为 null。</summary>
    public uint? MaxNumberOfBlockLength { get; }

    /// <summary>
    /// 本阶段每条 36 请求可携带的固件字节数 = maxNumberOfBlockLength - 2，
    /// 且不超过 128（§11.2）；失败时为 0。
    /// </summary>
    public int PayloadCapacity { get; }

    /// <summary>失败文案：优先补充说明，其次负响应原始数据，最后按失败原因映射。</summary>
    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static DownloadRequestResult Succeeded(
        byte[] requestUds,
        byte[] response,
        uint maxNumberOfBlockLength,
        int payloadCapacity) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response,
            maxNumberOfBlockLength, payloadCapacity);

    public static DownloadRequestResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null,
        byte[]? response = null) =>
        new(false, reason, negativeResponse, detailText, requestUds, response, null, 0);
}

/// <summary>
/// 第十二步单个固件块的传输结果（架构/OTA升级流程.md §12.1、§12.2）：
/// 只有收到与当前块号一致的完整 76 才算确认，块号不符或响应超长都按失败处理。
/// </summary>
public sealed class TransferBlockResult
{
    private TransferBlockResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText,
        byte[] requestUds,
        byte[]? response,
        byte blockCounter)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
        RequestUds = requestUds;
        Response = response;
        BlockCounter = blockCounter;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（如 76 块号与当前块号不符）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>本块实际发送的 UDS 请求（36 + 块号 + 本块固件字节）。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备 76 正响应原始 UDS；失败时为 null。</summary>
    public byte[]? Response { get; }

    /// <summary>本块的 UDS 块序号（1～255、0，按 ...FE FF 00 01... 回绕），不是 LIN CF 序号。</summary>
    public byte BlockCounter { get; }

    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static TransferBlockResult Succeeded(byte[] requestUds, byte[] response, byte blockCounter) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response, blockCounter);

    public static TransferBlockResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        byte blockCounter,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText, requestUds, null, blockCounter);
}

/// <summary>第十三步结束数据传输的结果（架构/OTA升级流程.md §13.1、§13.3）：77 必须恰好一字节。</summary>
public sealed class TransferExitResult
{
    private TransferExitResult(
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

    public DiagnosticNegativeResponse? NegativeResponse { get; }

    public string? DetailText { get; }

    /// <summary>本次请求的完整 UDS，固定单字节 37。</summary>
    public byte[] RequestUds { get; }

    /// <summary>设备 77 正响应原始 UDS；失败时为 null。</summary>
    public byte[]? Response { get; }

    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static TransferExitResult Succeeded(byte[] requestUds, byte[] response) =>
        new(true, DiagnosticFailureReason.None, null, null, requestUds, response);

    public static TransferExitResult Failed(
        DiagnosticFailureReason reason,
        byte[] requestUds,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText, requestUds, null);
}

/// <summary>
/// 下载业务层（架构/OTA升级流程.md 第十一至第十三步）：
/// 只构造/解析 `34 00 44 + 地址 + 大小`、`36 + 块号 + 数据`、`37` 这三类请求，
/// 回显核对、分片发送、3D 轮询、超时与 78 续期全部由 <see cref="DiagnosticTransactionService"/> 负责。
/// 本类不保存块偏移状态、不自行重传，也不新增发送出口（§12.4、§12.5）。
/// </summary>
public sealed class FirmwareDownloadService
{
    /// <summary>RequestDownload 服务号（§11.1）。</summary>
    public const byte RequestDownloadServiceId = 0x34;

    /// <summary>TransferData 服务号（§12.1）。</summary>
    public const byte TransferDataServiceId = 0x36;

    /// <summary>RequestTransferExit 服务号（§13.2）。</summary>
    public const byte TransferExitServiceId = 0x37;

    /// <summary>请求下载的正响应服务号（§11.1）。</summary>
    private const byte RequestDownloadPositiveServiceId = 0x74;

    /// <summary>传输数据的正响应服务号（§12.1）。</summary>
    private const byte TransferDataPositiveServiceId = 0x76;

    /// <summary>结束传输的正响应服务号（§13.2）。</summary>
    private const byte TransferExitPositiveServiceId = 0x77;

    /// <summary>
    /// 块内相邻 3C 分片的最小发送间隔（§12.4）：自上一片串口写入完成起算。
    /// 实测单片回显约 11～12 ms，取 20 ms 在相邻帧之间留出稳定余量；
    /// 该值必须不大于 <see cref="DiagnosticSendTiming.InterFrameGapBudget"/>（默认 1000 ms）。
    /// </summary>
    public static TimeSpan FrameGap { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>36 请求的多帧选项：仅固定分片间隔，其余时序沿用默认值。</summary>
    private static readonly DiagnosticMultiFrameOptions TransferMultiFrameOptions = new()
    {
        Send = DiagnosticSendTiming.Default with { MinimumInterFrameGap = FrameGap }
    };

    /// <summary>
    /// 请求下载的数据格式标识（§11.1）：`00` 无压缩无加密，`44` 表示地址 4 字节、大小 4 字节。
    /// </summary>
    private const byte DataFormatIdentifier = 0x00;

    private const byte AddressAndLengthFormat = 0x44;

    /// <summary>请求下载完整 UDS 长度：34 + 格式 2 + 地址 4 + 大小 4 = 11 字节（§11.1），必然多帧。</summary>
    public const int RequestDownloadLength = 11;

    private readonly DiagnosticTransactionService transactionService;

    public FirmwareDownloadService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 第十一步：发送 `34 00 44 + 大端起始地址 + 大端字节长度`（§11.1），
    /// 解析 74 正响应中的最大块长（§11.2）。地址与长度来自传输快照，不写死示例值。
    /// </summary>
    public async Task<DownloadRequestResult> RequestDownloadAsync(
        byte nad,
        FirmwareTransferPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var udsRequest = new byte[RequestDownloadLength];
        udsRequest[0] = RequestDownloadServiceId;
        udsRequest[1] = DataFormatIdentifier;
        udsRequest[2] = AddressAndLengthFormat;
        BinaryPrimitives.WriteUInt32BigEndian(udsRequest.AsSpan(3, 4), plan.StartAddress);
        BinaryPrimitives.WriteUInt32BigEndian(udsRequest.AsSpan(7, 4), plan.Length);

        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = [RequestDownloadPositiveServiceId],
            // 长度 11 > 6：必须显式给出多帧选项，SF 的 DiagnosticTiming 不参与（§11.3）。
            MultiFrameOptions = new DiagnosticMultiFrameOptions()
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return DownloadRequestResult.Failed(result.Reason, udsRequest, result.NegativeResponse);
        }

        var response = result.Response!;
        if (!TryParseMaxBlockLength(response, out var maxBlockLength, out var error))
        {
            return DownloadRequestResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                detailText: error,
                response: response);
        }

        // 本阶段每包固件数据不超过 128 字节；设备返回更小容量时受更小值限制（§11.2）。
        var capacity = (int)Math.Min(maxBlockLength - 2, (uint)FirmwareTransferPlan.MaxBlockPayload);
        return DownloadRequestResult.Succeeded(udsRequest, response, maxBlockLength, capacity);
    }

    /// <summary>
    /// 第十二步：发送一个固件块 `36 + 块号 + 数据`，并核对 76 的块号（§12.1、§12.2）。
    /// 只有完整 `76 BSC` 与当前块号一致才算确认；块号不符、响应超长都按失败返回，
    /// 由编排层记录块号/偏移/长度后退出，不自动重传（§12.4）。
    /// </summary>
    public async Task<TransferBlockResult> TransferDataAsync(
        byte nad,
        FirmwareTransferPlan plan,
        int offset,
        int count,
        byte blockCounter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (offset < 0 || count < 1
            || count > FirmwareTransferPlan.MaxBlockPayload
            || (long)offset + count > plan.Data.Length)
        {
            var invalid = new byte[2];
            invalid[0] = TransferDataServiceId;
            invalid[1] = blockCounter;
            return TransferBlockResult.Failed(
                DiagnosticFailureReason.InvalidRequest,
                invalid,
                blockCounter,
                detailText: $"块参数非法：偏移 {offset}、长度 {count}、固件总长 {plan.Data.Length}。");
        }

        var udsRequest = new byte[2 + count];
        udsRequest[0] = TransferDataServiceId;
        udsRequest[1] = blockCounter;
        plan.Data.AsSpan(offset, count).CopyTo(udsRequest.AsSpan(2));

        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = [TransferDataPositiveServiceId],
            // 块长 128 时 UDS 为 130 字节，必须多帧；仅末块很短（UDS ≤ 6）时按单帧发送。
            // 多帧显式把分片间隔固定为 FrameGap（20 ms），不依赖 DiagnosticSendTiming 的 0 ms 默认值。
            Timing = DiagnosticTiming.Default,
            MultiFrameOptions = udsRequest.Length > 6 ? TransferMultiFrameOptions : null
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return TransferBlockResult.Failed(
                result.Reason, udsRequest, blockCounter, result.NegativeResponse);
        }

        var response = result.Response!;
        // 约定正响应恰好 76 BSC；迟到或错号响应不能推进偏移和进度（§12.1、§12.2）。
        if (response.Length != 2 || response[0] != TransferDataPositiveServiceId)
        {
            return TransferBlockResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                blockCounter,
                detailText: $"76 正响应格式不符：期望 2 字节 76 {blockCounter:X2}，" +
                    $"实际 {response.Length} 字节：{FormatBytes(response)}");
        }

        if (response[1] != blockCounter)
        {
            return TransferBlockResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                blockCounter,
                detailText: $"76 块号不符：期望 0x{blockCounter:X2}，设备返回 0x{response[1]:X2}" +
                    $"（{FormatBytes(response)}），本块未确认");
        }

        return TransferBlockResult.Succeeded(udsRequest, response, blockCounter);
    }

    /// <summary>
    /// 第十三步：发送单字节 `37` 结束数据传输，要求完整正响应恰好为单字节 `77`（§13.2、§13.3）。
    /// </summary>
    public async Task<TransferExitResult> RequestTransferExitAsync(
        byte nad,
        CancellationToken cancellationToken = default)
    {
        var udsRequest = new[] { TransferExitServiceId };
        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = [TransferExitPositiveServiceId],
            // 单字节请求走既有 SF 时序：t0 + 20 ms 首拍、20 ms 节拍、回显 1000 ms、总截止 5 s（§13.3）。
            Timing = DiagnosticTiming.Default
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return TransferExitResult.Failed(result.Reason, udsRequest, result.NegativeResponse);
        }

        var response = result.Response!;
        if (response.Length != 1 || response[0] != TransferExitPositiveServiceId)
        {
            return TransferExitResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                udsRequest,
                detailText: $"77 正响应格式不符：期望 1 字节 77，实际 {response.Length} 字节：{FormatBytes(response)}");
        }

        return TransferExitResult.Succeeded(udsRequest, response);
    }

    /// <summary>
    /// 解析 74 正响应 `74 + 长度格式 + 最大块长`（§11.2）：
    /// 高四位为后续长度字段字节数（1～4），要求字段完整、无多余字节、块长大于 2 且不超过 LIN-TP 上限 4095。
    /// 不静默截断，也不把 130 当成纯固件大小。
    /// </summary>
    private static bool TryParseMaxBlockLength(byte[] response, out uint maxBlockLength, out string? error)
    {
        maxBlockLength = 0;
        error = null;

        if (response.Length < 3 || response[0] != RequestDownloadPositiveServiceId)
        {
            error = $"74 正响应长度不足：实际 {response.Length} 字节：{FormatBytes(response)}";
            return false;
        }

        var lengthFormat = response[1];
        var lengthBytes = lengthFormat >> 4;
        if (lengthBytes is < 1 or > 4)
        {
            error = $"74 长度格式非法：0x{lengthFormat:X2}（高四位 {lengthBytes}，应为 1～4）：{FormatBytes(response)}";
            return false;
        }

        if (response.Length != 2 + lengthBytes)
        {
            error = $"74 长度字段不完整：长度格式声明 {lengthBytes} 字节，" +
                $"实际响应 {response.Length} 字节：{FormatBytes(response)}";
            return false;
        }

        uint value = 0;
        for (var index = 0; index < lengthBytes; index++)
        {
            value = (value << 8) | response[2 + index];
        }

        if (value <= 2)
        {
            error = $"74 最大块长 {value} 不大于 2，无法容纳 SID 与块号：{FormatBytes(response)}";
            return false;
        }

        if (value > FirmwareTransferPlan.MaxBlockLengthLimit)
        {
            error = $"74 最大块长 {value} 超过本项目 LIN-TP 的 UDS 长度上限 " +
                $"{FirmwareTransferPlan.MaxBlockLengthLimit}：{FormatBytes(response)}";
            return false;
        }

        maxBlockLength = value;
        return true;
    }

    private static string FormatBytes(byte[] values) => BitConverter.ToString(values).Replace('-', ' ');
}
