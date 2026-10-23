using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 一次安全访问请求（第八步请求 Seed 或第九步发送 Key）的结果：
/// 成败、失败原因、设备负响应原始数据，以及成功后取到的四字节 Seed。
/// </summary>
public sealed class SecurityAccessResult
{
    private SecurityAccessResult(
        bool success,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse,
        string? detailText,
        byte[]? seed)
    {
        Success = success;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
        DetailText = detailText;
        Seed = seed;
    }

    public bool Success { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    /// <summary>补充说明（如正响应长度不符）；无补充时为 null。</summary>
    public string? DetailText { get; }

    /// <summary>请求 Seed 成功时的四字节 Seed，按响应报文顺序保存，未做字节交换（§8.2）。</summary>
    public byte[]? Seed { get; }

    /// <summary>Seed 的十六进制文本（如 12 34 56 78）；非 Seed 结果时为 null。</summary>
    public string? SeedText => Seed is null
        ? null
        : BitConverter.ToString(Seed).Replace('-', ' ');

    /// <summary>是否收到全零 Seed（§8.3 待确认路径）；非 Seed 结果时为 false。</summary>
    public bool IsZeroSeed => Seed is { Length: SecurityAccessService.SeedLength } seed
        && seed[0] == 0 && seed[1] == 0 && seed[2] == 0 && seed[3] == 0;

    /// <summary>失败文案：优先补充说明，其次负响应原始数据，最后按失败原因映射。</summary>
    public string FailureText => DetailText
        ?? (NegativeResponse is { } negative
            ? $"设备返回负响应：{negative.Describe()}"
            : DiagnosticFailureText.Describe(FailureReason));

    public static SecurityAccessResult Succeeded(byte[]? seed = null) =>
        new(true, DiagnosticFailureReason.None, null, null, seed);

    public static SecurityAccessResult Failed(
        DiagnosticFailureReason reason,
        DiagnosticNegativeResponse? negativeResponse = null,
        string? detailText = null) =>
        new(false, reason, negativeResponse, detailText, null);
}

/// <summary>
/// 安全访问业务层（架构/OTA升级流程.md 第八步、第九步）：
/// 只做请求构造、正响应格式判定与 Key 计算，不处理 TP、调度、超时与 OTA 独占，也不自行调度串口。
/// Seed 仅与本次连接、本次 OTA 关联；本类不缓存上一轮 Seed/Key，调用方每次重新请求（§8.2）。
/// </summary>
public sealed class SecurityAccessService
{
    /// <summary>SecurityAccess 服务号（§8.2、§9.3）。</summary>
    private const byte SecurityAccessServiceId = 0x27;

    /// <summary>请求 Seed 的子功能（§8.2）。</summary>
    private const byte RequestSeedSubFunction = 0x09;

    /// <summary>发送 Key 的子功能（§9.3）。</summary>
    private const byte SendKeySubFunction = 0x0A;

    /// <summary>安全访问正响应服务号（§8.2）。</summary>
    private const byte PositiveResponseServiceId = 0x67;

    /// <summary>Seed / Key 的字节数（§8.2、§9.3）。</summary>
    public const int SeedLength = 4;

    /// <summary>Seed 正响应的完整 UDS 字节数：67 09 + 四字节 Seed（§8.2）。</summary>
    private const int SeedPositiveResponseLength = 2 + SeedLength;

    /// <summary>Key 正响应的完整 UDS 字节数：恰好 67 0A（§9.3）。</summary>
    private const int KeyPositiveResponseLength = 2;

    /// <summary>
    /// 中间件安全访问 mask（§9.1，`s_securityMask = 0x3B61B270`）。
    /// 按高字节到低字节依次为 3B、61、B2、70，与 Seed 四字节一一 XOR。
    /// </summary>
    public const uint SecurityMask = 0x3B61B270;

    private readonly DiagnosticTransactionService transactionService;

    public SecurityAccessService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService
            ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 发送 27 09 请求 Seed（§8.2）。成功要求有效 3C 回显、完整 UDS 长度为 6 字节且前两字节为 67 09；
    /// 后四字节按报文顺序原样取出，不做字节颠倒。全零 Seed 不在本层判断处理路径，由调用方决定（§8.3）。
    /// </summary>
    public async Task<SecurityAccessResult> RequestSeedAsync(
        byte nad,
        CancellationToken cancellationToken = default)
    {
        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = [SecurityAccessServiceId, RequestSeedSubFunction],
            ExpectedResponsePrefix = [PositiveResponseServiceId, RequestSeedSubFunction]
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return SecurityAccessResult.Failed(result.Reason, result.NegativeResponse);
        }

        // 协调层按前缀核对；长度仍需在业务层按已确认格式判定，短于或长于 6 字节都不能误报成功。
        var response = result.Response!;
        if (response.Length != SeedPositiveResponseLength)
        {
            return SecurityAccessResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                detailText: $"27 09 正响应长度为 {response.Length} 字节，预期 {SeedPositiveResponseLength} 字节");
        }

        return SecurityAccessResult.Succeeded(response[2..SeedPositiveResponseLength]);
    }

    /// <summary>
    /// 发送 27 0A + 四字节 Key（§9.3）。Key 必须是四字节二进制，不能转成 ASCII 十六进制字符串；
    /// 成功要求有效 3C 回显、完整 UDS 长度恰为 2 字节且为 67 0A。
    /// </summary>
    public async Task<SecurityAccessResult> SendKeyAsync(
        byte nad,
        byte[] key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != SeedLength)
        {
            throw new ArgumentException($"Key 必须是 {SeedLength} 字节。", nameof(key));
        }

        var udsRequest = new byte[2 + SeedLength];
        udsRequest[0] = SecurityAccessServiceId;
        udsRequest[1] = SendKeySubFunction;
        key.CopyTo(udsRequest, 2);

        var request = new DiagnosticRequest
        {
            Nad = nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = [PositiveResponseServiceId, SendKeySubFunction]
        };

        var result = await transactionService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return SecurityAccessResult.Failed(result.Reason, result.NegativeResponse);
        }

        var response = result.Response!;
        if (response.Length != KeyPositiveResponseLength)
        {
            return SecurityAccessResult.Failed(
                DiagnosticFailureReason.ResponseMismatch,
                detailText: $"27 0A 正响应长度为 {response.Length} 字节，预期 {KeyPositiveResponseLength} 字节");
        }

        return SecurityAccessResult.Succeeded();
    }

    /// <summary>
    /// 按中间件 `FLB_SeedToKey` 原式计算 Key 的 32 位数值（§9.1）：
    /// C = Seed XOR mask，四个 K 字节按原式高低位交叉拼接，返回值 = K0&lt;&lt;24 | K1&lt;&lt;16 | K2&lt;&lt;8 | K3。
    /// 严格保留原式（含 K3 的 OR 表达式），不改为旋转、不截断 31 位、不调整字节序。
    /// </summary>
    public static uint SeedToKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException($"Seed 必须是 {SeedLength} 字节。", nameof(seed));
        }

        var c0 = (byte)(seed[0] ^ 0x3B);
        var c1 = (byte)(seed[1] ^ 0x61);
        var c2 = (byte)(seed[2] ^ 0xB2);
        var c3 = (byte)(seed[3] ^ 0x70);

        var k0 = (byte)(((c0 & 0x0F) << 4) | (c1 & 0x0F));
        var k1 = (byte)(((c1 & 0xF0) >> 4) | ((c2 & 0x0F) << 4));
        var k2 = (byte)(((c2 & 0xF0) >> 4) | (c3 & 0xF0));
        var k3 = (byte)((c3 & 0x0F) | ((c0 & 0xF0) >> 4));

        return ((uint)k0 << 24) | ((uint)k1 << 16) | ((uint)k2 << 8) | k3;
    }

    /// <summary>
    /// 生成线上顺序的四字节 Key：已确认高字节先发送，即依次取返回值的 31–24、23–16、15–8、7–0 位（§9.1、§9.3）。
    /// 不使用依赖宿主端序的内存拷贝，避免大小端导致顺序颠倒。
    /// </summary>
    public static byte[] CreateKey(ReadOnlySpan<byte> seed)
    {
        var value = SeedToKey(seed);
        return
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ];
    }
}
