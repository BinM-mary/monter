using System.Text;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>一次版本读取的结果：成功文本或失败原因（供界面提示）。</summary>
public sealed class DeviceVersionResult
{
    private DeviceVersionResult(bool success, string? versionText, DiagnosticFailureReason failureReason)
    {
        Success = success;
        VersionText = versionText;
        FailureReason = failureReason;
    }

    public bool Success { get; }

    /// <summary>版本字符串（正响应去掉 0x62 与 DID 后的 ASCII 内容）。</summary>
    public string? VersionText { get; }

    public DiagnosticFailureReason FailureReason { get; }

    public static DeviceVersionResult Succeeded(string versionText) =>
        new(true, versionText, DiagnosticFailureReason.None);

    public static DeviceVersionResult Failed(DiagnosticFailureReason reason) =>
        new(false, null, reason);

    public string FailureText => FailureReason switch
    {
        DiagnosticFailureReason.EchoTimeout => "未收到 3C 回显",
        DiagnosticFailureReason.RequestWriteFailed => "诊断请求发送失败",
        DiagnosticFailureReason.ResponseMismatch => "设备响应与预期不符",
        DiagnosticFailureReason.DeadlineExceeded => "等待设备响应超时",
        DiagnosticFailureReason.Disconnected => "串口已断开",
        DiagnosticFailureReason.Shutdown => "诊断已终止",
        _ => "读取失败"
    };
}

/// <summary>
/// 设备信息业务层（WpfApp1诊断架构讨论记录.md §2、§6）：
/// 只做请求构造、响应解释与界面文案，不处理 TP、调度和超时；
/// 响应长度按 FF 声明动态处理，不为具体版本写死长度或读取次数。
/// </summary>
public sealed class DeviceInformationService
{
    /// <summary>统一的诊断 NAD（§2）。</summary>
    public const byte DiagnosticNad = 0x78;

    private const byte ReadDataByIdentifier = 0x22;
    private const byte ReadDataPositiveResponse = 0x62;

    private readonly DiagnosticTransactionService transactionService;

    public DeviceInformationService(DiagnosticTransactionService transactionService)
    {
        this.transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    public async Task<DeviceVersionResult> ReadVersionAsync(DeviceVersionKind kind)
    {
        var (highByte, lowByte) = GetIdentifier(kind);
        var request = new DiagnosticRequest
        {
            Nad = DiagnosticNad,
            UdsRequest = [ReadDataByIdentifier, highByte, lowByte],
            ExpectedResponsePrefix = [ReadDataPositiveResponse, highByte, lowByte]
        };

        var result = await transactionService.ExecuteAsync(request).ConfigureAwait(false);
        if (!result.Success)
        {
            return DeviceVersionResult.Failed(result.Reason);
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
