using System.Text;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>一次版本读取的结果：成功文本或失败原因（供界面提示）。</summary>
public sealed class DeviceVersionResult
{
    private DeviceVersionResult(
        bool success,
        string? versionText,
        DiagnosticFailureReason failureReason,
        DiagnosticNegativeResponse? negativeResponse)
    {
        Success = success;
        VersionText = versionText;
        FailureReason = failureReason;
        NegativeResponse = negativeResponse;
    }

    public bool Success { get; }

    /// <summary>版本字符串（正响应去掉 0x62 与 DID 后的 ASCII 内容）。</summary>
    public string? VersionText { get; }

    public DiagnosticFailureReason FailureReason { get; }

    /// <summary>设备返回的完整负响应（7F + 原 SID + NRC）；不是负响应失败时为 null。</summary>
    public DiagnosticNegativeResponse? NegativeResponse { get; }

    public static DeviceVersionResult Succeeded(string versionText) =>
        new(true, versionText, DiagnosticFailureReason.None, null);

    public static DeviceVersionResult Failed(
        DiagnosticFailureReason reason,
        DiagnosticNegativeResponse? negativeResponse = null) =>
        new(false, null, reason, negativeResponse);

    // 失败文案与 OTA 流程共用同一套映射（Models/DiagnosticFailureText），不在本类另写一份；
    // 负响应在原因后附带原始 UDS 与 NRC，便于定位设备拒绝的具体原因。
    public string FailureText => NegativeResponse is { } negative
        ? $"{DiagnosticFailureText.Describe(FailureReason)}（{negative.Describe()}）"
        : DiagnosticFailureText.Describe(FailureReason);
}

/// <summary>
/// 设备信息业务层（架构/WpfApp1诊断架构讨论记录.md §2、§6）：
/// 只做请求构造、响应解释与界面文案，不处理 TP、调度和超时；
/// 响应长度按 FF 声明动态处理，不为具体版本写死长度或读取次数。
/// </summary>
public sealed class DeviceInformationService
{
    /// <summary>默认诊断 NAD（§2）：未显式指定时的回退值（固件页 NAD 输入框的默认值同源）。</summary>
    public const byte DiagnosticNad = 0x78;

    private const byte ReadDataByIdentifier = 0x22;
    private const byte ReadDataPositiveResponse = 0x62;

    private readonly DiagnosticTransactionService transactionService;

    public DeviceInformationService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    /// <summary>
    /// 读取设备版本。<paramref name="nad"/> 为空时使用默认 NAD（0x78）；
    /// 诊断与后续 OTA 发送的 NAD 组装统一由调用方显式传入，服务内不再写死来源。
    /// </summary>
    public async Task<DeviceVersionResult> ReadVersionAsync(DeviceVersionKind kind, byte? nad = null)
    {
        var (highByte, lowByte) = GetIdentifier(kind);
        var request = new DiagnosticRequest
        {
            Nad = nad ?? DiagnosticNad,
            UdsRequest = [ReadDataByIdentifier, highByte, lowByte],
            ExpectedResponsePrefix = [ReadDataPositiveResponse, highByte, lowByte]
        };

        var result = await transactionService.ExecuteAsync(request).ConfigureAwait(false);
        if (!result.Success)
        {
            return DeviceVersionResult.Failed(result.Reason, result.NegativeResponse);
        }

        // 正响应：62 F1 xx + 版本数据；长度由 FF 声明决定，此处按实际长度取剩余字节。
        var response = result.Response!;
        var versionText = response.Length <= 3
            ? string.Empty
            : Encoding.ASCII.GetString(response, 3, response.Length - 3);

        return DeviceVersionResult.Succeeded(versionText.TrimEnd());
    }

    private static (byte HighByte, byte LowByte) GetIdentifier(DeviceVersionKind kind) => kind switch
    {
        DeviceVersionKind.Bootloader => (0xF1, 0x80),
        DeviceVersionKind.Firmware => (0xF1, 0x95),
        DeviceVersionKind.Hardware => (0xF1, 0x93),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
