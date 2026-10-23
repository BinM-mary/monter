using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// OTA 升级流程编排（架构/OTA升级流程.md 第一至第十五步）：
/// - 第一步：暂停应用通信后的 TesterPresent 连续在线确认（连续三次成功通过、累计两次失败退出、两笔间隔 1 s）；
/// - 第二步：读取软件版本（DID F1 95）；
/// - 第三步：读取 Bootloader 版本（DID F1 80）——**当前默认跳过**（见 <see cref="ReadBootloaderAndHardwareVersions"/>）；
/// - 第四步：读取硬件版本（DID F1 93）——**当前默认跳过**；启用时第二步至第四步任一步失败立即退出本次 OTA；
/// - 第五步：再次进行两次在线确认（复用第一步机制，目标连续两次成功，计数独立初始化）；
/// - 第六步：发送 10 03 进入扩展诊断会话；
/// - 第七步：发送 10 02 进入编程会话；
/// - 第八步：发送 27 09 请求四字节 Seed；
/// - 第九步：按中间件 FLB_SeedToKey 原式计算 Key 并发送 27 0A；
/// - 第十步：按本次固件的起始地址与大小发送 31 01 FF 00 擦除请求（12 字节多帧）；
/// - 第十一步：发送 34 00 44 申请下载，解析 74 的最大块长并折算每包固件容量；
/// - 第十二步：逐块发送 36 + 块号 + 数据，只认匹配块号的 76，块号按 FE FF 00 01 回绕；
/// - 第十三步：全部块确认后发送 37 结束数据传输，要求恰好 77；
/// - 第十四步：发送 31 01 02 02 + 地址 + 大小 + CRC32，结果 00 通过、01 失败；
/// - 第十五步：校验通过后发送 11 01 复位，只认恰好 2 字节 51 01；复位确认后等待 50 ms 读取一次 APP 版本（22 F1 95）作确认，读不到不影响升级结论。
/// 第六至第十五步的非 78 有效负响应在日志显示原始 UDS 与 NRC 后退出；
/// 有效 NRC 78 由协调层续期并继续接收最终响应，本类只转发一条“设备处理中”提示。
///
/// 分层边界：报文组装、3C 回显核对、3D 轮询、超时与诊断独占全部复用
/// <see cref="DiagnosticTransactionService"/>；版本请求与解析复用 <see cref="DeviceInformationService"/>；
/// 会话请求与正响应格式判定复用 <see cref="DiagnosticSessionService"/>；
/// 安全访问请求、正响应格式判定与 Key 计算复用 <see cref="SecurityAccessService"/>；
/// 擦除请求构造与正响应判定复用 <see cref="EraseFlashService"/>；
/// 下载申请、块传输与结束传输复用 <see cref="FirmwareDownloadService"/>；
/// 固件校验请求与结果解析复用 <see cref="FirmwareVerificationService"/>；
/// 复位请求与正响应判定复用 <see cref="EcuResetService"/>。
/// 本类只做步骤编排、块切片顺序、计数决策与结果汇总，不新增发送出口，不改动协议字段与既有发送流程。
/// 复位成功只代表设备返回了 51 01；新固件是否启动需实机验证，本轮不增加第十六步。
/// </summary>
public sealed class OtaUpgradeService
{
    /// <summary>第一步在线确认要求的连续成功次数（§1.1、§1.5）。</summary>
    public const int RequiredConsecutiveProbes = 3;

    /// <summary>第五步再次在线确认要求的连续成功次数（§5.1、§5.3）。</summary>
    public const int RequiredSecondProbeSuccesses = 2;

    /// <summary>
    /// 在线确认阶段的累计失败上限，达到即退出本次 OTA（§1.1、§1.5）；第五步沿用同一上限（§5.3）。
    /// </summary>
    public const int MaxProbeFailures = 2;

    /// <summary>两笔探测之间的固定间隔：从上一笔最终结果时刻起算（§1.4、§5.2）。</summary>
    public static TimeSpan ProbeInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 相邻两个固件块之间的固定间隔（§12.4）：从上一块 76 确认返回后起算，
    /// 到下一块 36 请求发出前结束。块内分片仍由回显自同步，不加额外延时。
    /// </summary>
    public static TimeSpan BlockInterval { get; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 复位确认后到读取 APP 版本的固定等待（§15.7）：50 ms，自 `51 01` 到达后起算。
    /// 留出设备复位后的通信恢复时间；该等待不计入任何响应预算，也不改变复位成功判定。
    /// </summary>
    public static TimeSpan ResetVersionDelay { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 是否读取 Bootloader 版本与硬件版本（第三、第四步）。默认 `false`：本次升级**只读 APP（软件）版本**，
    /// 第三、第四步整步跳过——不发送对应的版本读取请求，不算失败，也不写入结果。
    /// <para>
    /// 步骤编号仍与协议文档保持一致（第三、第四步只是“不执行”），需要恢复三步读取时把它设为 `true`。
    /// 调试时也可以在即时窗口写 `OtaUpgrade.ReadBootloaderAndHardwareVersions = true` 临时恢复。
    /// </para>
    /// </summary>
    public bool ReadBootloaderAndHardwareVersions { get; set; }

    /// <summary>TesterPresent 请求（§1.3.1）：完整 UDS 为 3E 00。</summary>
    private static readonly byte[] TesterPresentRequest = [0x3E, 0x00];

    /// <summary>TesterPresent 正响应（§1.3.4）：完整 UDS 必须恰好为 7E 00。</summary>
    private static readonly byte[] TesterPresentPositiveResponse = [0x7E, 0x00];

    private readonly DiagnosticTransactionService diagnostics;
    private readonly DeviceInformationService deviceInfo;
    private readonly DiagnosticSessionService sessionService;
    private readonly SecurityAccessService securityAccess;
    private readonly EraseFlashService eraseFlash;
    private readonly FirmwareDownloadService downloadService;
    private readonly FirmwareVerificationService verificationService;
    private readonly EcuResetService resetService;
    private readonly TimeProvider timeProvider;

    public OtaUpgradeService(
        DiagnosticTransactionService diagnostics,
        DeviceInformationService deviceInfo,
        DiagnosticSessionService sessionService,
        SecurityAccessService securityAccess,
        EraseFlashService eraseFlash,
        FirmwareDownloadService downloadService,
        FirmwareVerificationService verificationService,
        EcuResetService resetService,
        TimeProvider? timeProvider = null)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.deviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
        this.sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        this.securityAccess = securityAccess ?? throw new ArgumentNullException(nameof(securityAccess));
        this.eraseFlash = eraseFlash ?? throw new ArgumentNullException(nameof(eraseFlash));
        this.downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        this.verificationService = verificationService
            ?? throw new ArgumentNullException(nameof(verificationService));
        this.resetService = resetService ?? throw new ArgumentNullException(nameof(resetService));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        // NRC 78 由协调层续期并继续接收最终响应；此处只转成升级日志提示（§6.5）。
        this.diagnostics.ResponsePendingReceived += HandleResponsePending;
    }

    /// <summary>
    /// 分步进展上报（后台线程触发）：探测序号、单次结果、连续成功与累计失败计数、版本读取与会话结果。
    /// 界面据此写日志与进度；订阅方异常不得影响流程本身。
    /// </summary>
    public event Action<OtaStepReport>? StepReported;

    /// <summary>
    /// 非失败的流程提示（后台线程触发）：当前用于 NRC 78 响应待定的“设备处理中，等待已刷新”。
    /// 只写升级日志，不改变进度、成功判定或步骤计数。
    /// </summary>
    public event Action<string>? NoticeReported;

    /// <summary>
    /// 执行本次 OTA（第一至第十五步）。全程持续诊断独占，队列暂时清空也不恢复应用帧（§1.2、§1.6）；
    /// 成功表示第一至第十五步全部通过：已保存 APP（软件）版本（第三、第四步按默认配置跳过）、进入扩展诊断会话与编程会话、完成安全访问、
    /// 设备接受擦除请求（71 01 FF 00）、接受下载申请（74）、全部固件块确认（76）并结束传输（77）、
    /// 固件校验结果为 00、设备返回复位肯定响应（51 01），并在复位后 50 ms 读取一次 APP 版本；
    /// **不代表已确认擦除完成，也不代表设备已启动运行新固件**（§10.6、§14.4、§15.5）。
    /// 擦除、下载与校验的起始地址与长度取自 <paramref name="firmware"/> 的归一化映像，提交后不再回读界面；
    /// BIN 文件因缺少确定的烧录起始地址，会在第十步拒绝生成请求，不发送猜测报文。
    /// </summary>
    public async Task<OtaSessionResult> RunAsync(
        byte nad,
        FirmwareImage firmware,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firmware);

        diagnostics.BeginOtaExclusiveHold();
        var consecutiveSuccess = 0;
        var failureCount = 0;
        var currentStage = OtaStage.NotStarted;
        string? softwareVersion = null;
        string? bootloaderVersion = null;
        string? hardwareVersion = null;

        try
        {
            // 第一步：连续三次在线确认。
            currentStage = OtaStage.OnlineProbe;
            var probe = await RunOnlineProbeAsync(
                nad, RequiredConsecutiveProbes, OtaStage.OnlineProbe, "在线确认", cancellationToken).ConfigureAwait(false);
            consecutiveSuccess = probe.ConsecutiveSuccessCount;
            failureCount = probe.FailureCount;
            if (!probe.Success)
            {
                return OtaSessionResult.Failed(
                    OtaStage.OnlineProbe,
                    probe.FailureText ?? "在线确认未通过",
                    consecutiveSuccess,
                    failureCount);
            }

            // 第二步：软件版本（固件版本）。失败即退出，不重试、不跳过（"第二至第四步的共用规则"）。
            currentStage = OtaStage.SoftwareVersion;
            var software = await ReadVersionStepAsync(
                OtaStage.SoftwareVersion, DeviceVersionKind.Firmware, "软件版本", nad).ConfigureAwait(false);
            if (!software.Success)
            {
                return OtaSessionResult.Failed(
                    OtaStage.SoftwareVersion, software.FailureText ?? "软件版本读取失败",
                    consecutiveSuccess, failureCount);
            }

            softwareVersion = software.VersionText;

            // 第三、第四步：Bootloader 版本与硬件版本。当前配置只读 APP（软件）版本，
            // 这两步整步跳过：不发送版本读取请求、不计失败、不写结果（ReadBootloaderAndHardwareVersions）。
            if (ReadBootloaderAndHardwareVersions)
            {
                // 第三步：Bootloader 版本。
                currentStage = OtaStage.BootloaderVersion;
                var bootloader = await ReadVersionStepAsync(
                    OtaStage.BootloaderVersion, DeviceVersionKind.Bootloader, "Bootloader 版本", nad).ConfigureAwait(false);
                if (!bootloader.Success)
                {
                    return OtaSessionResult.Failed(
                        OtaStage.BootloaderVersion, bootloader.FailureText ?? "Bootloader 版本读取失败",
                        consecutiveSuccess, failureCount, softwareVersion);
                }

                bootloaderVersion = bootloader.VersionText;

                // 第四步：硬件版本。
                currentStage = OtaStage.HardwareVersion;
                var hardware = await ReadVersionStepAsync(
                    OtaStage.HardwareVersion, DeviceVersionKind.Hardware, "硬件版本", nad).ConfigureAwait(false);
                if (!hardware.Success)
                {
                    return OtaSessionResult.Failed(
                        OtaStage.HardwareVersion, hardware.FailureText ?? "硬件版本读取失败",
                        consecutiveSuccess, failureCount, softwareVersion, bootloaderVersion);
                }

                hardwareVersion = hardware.VersionText;
            }
            else
            {
               // Notice("第三、第四步：按当前配置跳过 Bootloader 版本与硬件版本读取，本次只读取 APP 版本。");
            }

            // 第五步：再次在线确认。连续两次成功通过，计数独立初始化；
            // 与第四步之间不额外套用探测间的固定 1 s 间隔（§5.2、§5.3）。
            currentStage = OtaStage.OnlineProbeAgain;
            var secondProbe = await RunOnlineProbeAsync(
                nad, RequiredSecondProbeSuccesses, OtaStage.OnlineProbeAgain, "再次在线确认", cancellationToken)
                .ConfigureAwait(false);
            if (!secondProbe.Success)
            {
                return OtaSessionResult.Failed(
                    OtaStage.OnlineProbeAgain,
                    secondProbe.FailureText ?? "再次在线确认未通过",
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            // 第六步：进入扩展诊断会话（10 03）。正响应只判长度与前两字节，P2/P2* 不解析（§6.3）；
            // 非 78 的有效负响应把原始 UDS 与 NRC 写入升级日志后立即退出，不重试、不弹窗（§6.4）。
            currentStage = OtaStage.ExtendedSession;
            var session = await sessionService
                .EnterExtendedSessionAsync(nad, cancellationToken).ConfigureAwait(false);
            if (!session.Success)
            {
                Report(OtaStage.ExtendedSession, 1, false,
                    $"第六步：进入扩展诊断会话失败。{DescribeFailure(session.NegativeResponse, session.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.ExtendedSession,
                    FailureSummary("进入扩展诊断会话失败", session.NegativeResponse, session.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            Report(OtaStage.ExtendedSession, 1, true,
                "第六步：已进入扩展诊断会话，响应校验通过。",
                consecutiveSuccess, failureCount);

            // 第七步：进入编程会话（10 02）。与第六步同构，只换子功能与预期前缀（§7.5）；
            // 会话时间参数不解析，负响应的日志与退出规则与第六步共用同一段逻辑（§7.4）。
            currentStage = OtaStage.ProgrammingSession;
            var programming = await sessionService
                .EnterProgrammingSessionAsync(nad, cancellationToken).ConfigureAwait(false);
            if (!programming.Success)
            {
                Report(OtaStage.ProgrammingSession, 1, false,
                    $"第七步：进入编程会话失败。{DescribeFailure(programming.NegativeResponse, programming.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.ProgrammingSession,
                    FailureSummary("进入编程会话失败", programming.NegativeResponse, programming.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            Report(OtaStage.ProgrammingSession, 1, true,
                "第七步：已进入编程会话，响应校验通过。",
                consecutiveSuccess, failureCount);

            // 第八步：请求 Seed（27 09）。正响应完整 UDS 恰为 67 09 + 四字节 Seed，按报文顺序取用（§8.2）；
            // 非 78 负响应记原始 UDS 与 NRC 后退出，不自动重新请求 Seed（§9.4）。
            currentStage = OtaStage.SeedRequest;
            var seedResult = await securityAccess
                .RequestSeedAsync(nad, cancellationToken).ConfigureAwait(false);
            if (!seedResult.Success)
            {
                Report(OtaStage.SeedRequest, 1, false,
                    $"第八步：请求 Seed 失败。{DescribeFailure(seedResult.NegativeResponse, seedResult.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.SeedRequest,
                    FailureSummary("请求 Seed 失败", seedResult.NegativeResponse, seedResult.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            // 全零 Seed 的处理路径尚未确认（§8.3）：既不跳过第九步，也不照常计算发送，
            // 以免把“已解锁”误判为成功或白白消耗设备安全访问次数。
            if (seedResult.IsZeroSeed)
            {
                const string zeroSeedText =
                    "设备返回全零 Seed（可能已处于解锁状态）；零 Seed 处理方式待确认，本次未发送 Key";
                Report(OtaStage.SeedRequest, 1, false, $"第八步：{zeroSeedText}。",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.SeedRequest, zeroSeedText,
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            Report(OtaStage.SeedRequest, 1, true,
                "第八步：请求 Seed 成功",
                consecutiveSuccess, failureCount);

            // 第九步：按中间件 FLB_SeedToKey 原式计算 Key，高字节先发送（§9.1、§9.3）。
            // Key 只在本次 OTA 内使用，不缓存、不复用上一轮 Seed/Key（§8.2）；
            // 35/36/37 等负响应只记录并退出，不自动重试以免消耗设备尝试次数（§9.4）。
            currentStage = OtaStage.KeySend;
            var key = SecurityAccessService.CreateKey(seedResult.Seed!);
            var keyResult = await securityAccess
                .SendKeyAsync(nad, key, cancellationToken).ConfigureAwait(false);
            if (!keyResult.Success)
            {
                Report(OtaStage.KeySend, 1, false,
                    $"第九步：发送 Key 失败（Key = {FormatBytes(key)}）。" +
                    $"{DescribeFailure(keyResult.NegativeResponse, keyResult.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.KeySend,
                    FailureSummary("发送 Key 失败", keyResult.NegativeResponse, keyResult.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            Report(OtaStage.KeySend, 1, true,
                $"第九步：发送 Key 成功，Key = {FormatBytes(key)}，校验通过。",
                consecutiveSuccess, failureCount);

            // 第十步：擦除（§10.2～§10.6）。安全访问成功后保持 NAD、连接与 OTA 独占；
            // 起始地址与字节长度来自本次固件映像的不可变快照，不写死截图数值、不把结束地址当长度。
            currentStage = OtaStage.Erase;
            if (!ErasePlan.TryCreate(firmware, out var erasePlan, out var planError))
            {
                var planFailure = planError ?? "擦除参数不可用";
                Report(OtaStage.Erase, 1, false,
                    $"第十步：未生成擦除请求。{planFailure}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.Erase, $"擦除参数不可用：{planFailure}",
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            Report(OtaStage.Erase, 1, true,
                $"第十步：擦除范围已按固件确定：{erasePlan!.RangeText}；" +
                $"来源 {erasePlan.FirmwareFileName}（{(erasePlan.Format == FirmwareFileFormat.IntelHex ? "HEX" : "BIN")}）。",
                consecutiveSuccess, failureCount);

            // 12 字节请求由既有 TP 编码为 FF + 两条 CF，逐片回显、隔离与 3D 轮询全部复用协调层。
            var erase = await eraseFlash
                .EraseAsync(nad, erasePlan, cancellationToken).ConfigureAwait(false);
            if (!erase.Success)
            {
                Report(OtaStage.Erase, 1, false,
                    $"第十步：擦除请求失败（请求 {FormatBytes(erase.RequestUds)}）。" +
                    $"{DescribeFailure(erase.NegativeResponse, erase.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.Erase,
                    FailureSummary("擦除请求失败", erase.NegativeResponse, erase.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion);
            }

            // 71 01 FF 00 只证明例程被接受；状态记录语义与“擦除完成”判据待设备契约确认（§10.6），
            // 因此 CompletionConfirmed 明确为 false，日志措辞也不写“擦除完成”。
            var eraseOutcome = new OtaEraseOutcome(
                erasePlan.StartAddress,
                erasePlan.Length,
                erase.StateRecord,
                erase.RequestAccepted,
                CompletionConfirmed: false);

            Report(OtaStage.Erase, 1, true,
                $"第十步：设备已接受擦除请求，NAD=0x{nad:X2}" +
                (erase.StateRecord.Length > 0
                    ? $"状态记录 {erase.StateRecordText}"
                    : "设备未返回状态记录。") ,
                consecutiveSuccess, failureCount);

            // 第十一步：申请下载（§11.1～§11.3）。地址与长度直接复用第十步已提交的范围，
            // 数据与 CRC32 取自同一份映像快照，不重新读界面、不重新读文件。
            // 前提是“第十步满足设备约定的擦除完成条件”；当前 CompletionConfirmed 恒为 false（契约待确认），
            // 只按“设备已接受擦除请求”继续，并在日志中明确标注该前提未确认，不写成“已擦除”。
            currentStage = OtaStage.DownloadRequest;
            if (!FirmwareTransferPlan.TryCreate(firmware, erasePlan, out var transferPlan, out var transferError))
            {
                var transferPlanFailure = transferError ?? "下载参数不可用";
                Report(OtaStage.DownloadRequest, 1, false,
                    $"第十一步：未生成下载申请。{transferPlanFailure}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.DownloadRequest, $"下载参数不可用：{transferPlanFailure}",
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion, eraseOutcome);
            }

            Report(OtaStage.DownloadRequest, 1, true,
                $"第十一步：下载范围与擦除同源：{transferPlan!.RangeText}；CRC32 = {transferPlan.Crc32Text}。" +
                (eraseOutcome.CompletionConfirmed
                    ? "擦除完成已确认。"
                    : "注意：擦除完成判据尚未确认（CompletionConfirmed=false），按设备已接受擦除请求继续。"),
                consecutiveSuccess, failureCount);

            var download = await downloadService
                .RequestDownloadAsync(nad, transferPlan, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
            {
                Report(OtaStage.DownloadRequest, 1, false,
                    $"第十一步：下载申请失败（请求 {FormatBytes(download.RequestUds)}）。" +
                    $"{DescribeFailure(download.NegativeResponse, download.FailureText, nad)}",
                    consecutiveSuccess, failureCount);

                return OtaSessionResult.Failed(
                    OtaStage.DownloadRequest,
                    FailureSummary("下载申请失败", download.NegativeResponse, download.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion, eraseOutcome);
            }

            Report(OtaStage.DownloadRequest, 1, true,
                $"第十一步：设备已接受下载申请；" +
                $"最大块长 {download.MaxNumberOfBlockLength} 字节，每包固件数据 {download.PayloadCapacity} 字节。",
                consecutiveSuccess, failureCount);

            // 第十二步：逐块传输（§12.1～§12.4）。每块是一笔独立事务：发送 36 + 块号 + 数据，
            // 只有收到与当前块号一致的 76 才推进偏移与块号；失败记录块号/偏移/长度/NRC 后退出，不自动重传。
            currentStage = OtaStage.DataTransfer;
            var totalBytes = (int)transferPlan.Length;
            var totalBlocks = transferPlan.GetBlockCount(download.PayloadCapacity);
            // 日志按块上报会淹没步骤信息：约每 5% 一条，且首块与末块必报（§12.2 的进度只按已确认字节）。
            var reportInterval = Math.Max(1, totalBlocks / 20);
            var confirmedBytes = 0;
            byte blockCounter = 0x01;
            var blockIndex = 0;

            while (confirmedBytes < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 块间间隔（§12.4）：上一块 76 确认返回后先停 BlockInterval 再发下一块。
                // 实测 76 到下一块首帧原本只隔约 1 ms，而块首那一帧前面没有回显做过屏障，
                // 是整条链路上唯一没有自同步保护的提交点。首块之前不等待。
                if (blockIndex > 0)
                {
                    await Task.Delay(BlockInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                }

                var blockLength = Math.Min(download.PayloadCapacity, totalBytes - confirmedBytes);
                var blockOffset = confirmedBytes;
                var block = await downloadService
                    .TransferDataAsync(nad, transferPlan, blockOffset, blockLength, blockCounter, cancellationToken)
                    .ConfigureAwait(false);

                if (!block.Success)
                {
                    Report(OtaStage.DataTransfer, blockIndex + 1, false,
                        $"第十二步：固件块 {blockIndex + 1}/{totalBlocks} 未确认（块号 0x{blockCounter:X2}，" +
                        $"偏移 0x{blockOffset:X8}，长度 {blockLength} 字节，累计已确认 {confirmedBytes:N0}/{totalBytes:N0} 字节）。" +
                        $"{DescribeFailure(block.NegativeResponse, block.FailureText, nad)}",
                        consecutiveSuccess, failureCount,
                        completedBytes: confirmedBytes, totalBytes: totalBytes);

                    return OtaSessionResult.Failed(
                        OtaStage.DataTransfer,
                        FailureSummary(
                            $"固件块 {blockIndex + 1}/{totalBlocks} 传输失败" +
                            $"（块号 0x{blockCounter:X2}，偏移 0x{blockOffset:X8}，长度 {blockLength} 字节）",
                            block.NegativeResponse, block.FailureText),
                        consecutiveSuccess, failureCount,
                        softwareVersion, bootloaderVersion, hardwareVersion, eraseOutcome,
                        CreateDownloadOutcome(download, totalBlocks, confirmedBytes, totalBytes, transferExited: false));
                }

                confirmedBytes += blockLength;
                blockIndex++;
                // 8 位块号：...FE FF 00 01... 回绕（§12.2）；只在本块 76 确认后加一。
                blockCounter = unchecked((byte)(blockCounter + 1));

                if (blockIndex == 1 || blockIndex == totalBlocks || blockIndex % reportInterval == 0)
                {
                    Report(OtaStage.DataTransfer, blockIndex, true,
                        $"第十二步：固件块 {blockIndex}/{totalBlocks} 已确认（块号 0x{block.RequestUds[1]:X2}，" +
                        $"偏移 0x{blockOffset:X8}，长度 {blockLength} 字节，累计 {confirmedBytes:N0}/{totalBytes:N0} 字节）。",
                        consecutiveSuccess, failureCount,
                        completedBytes: confirmedBytes, totalBytes: totalBytes);
                }
            }

            // 第十三步：结束数据传输（§13.1～§13.3）。仅在全部块已确认、累计字节等于申请长度后发送，
            // 不把 37 当成中止命令，也不在仍有未确认块时提前发送。
            currentStage = OtaStage.TransferExit;
            var exit = await downloadService
                .RequestTransferExitAsync(nad, cancellationToken).ConfigureAwait(false);
            if (!exit.Success)
            {
                Report(OtaStage.TransferExit, 1, false,
                    $"第十三步：结束数据传输失败（请求 {FormatBytes(exit.RequestUds)}）。" +
                    $"{DescribeFailure(exit.NegativeResponse, exit.FailureText, nad)}",
                    consecutiveSuccess, failureCount,
                    completedBytes: confirmedBytes, totalBytes: totalBytes);

                return OtaSessionResult.Failed(
                    OtaStage.TransferExit,
                    FailureSummary("结束数据传输失败", exit.NegativeResponse, exit.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion, eraseOutcome,
                    CreateDownloadOutcome(download, totalBlocks, confirmedBytes, totalBytes, transferExited: false));
            }

            var downloadOutcome = CreateDownloadOutcome(
                download, totalBlocks, confirmedBytes, totalBytes, transferExited: true);

            Report(OtaStage.TransferExit, 1, true,
                $"第十三步：设备已确认结束固件数据传输，共 {blockIndex} 块、" +
                $"{confirmedBytes:N0} 字节；",
                consecutiveSuccess, failureCount,
                completedBytes: confirmedBytes, totalBytes: totalBytes);

            // 第十四步：固件校验（§14.1～§14.6）。CRC32 来自传输快照，与已发送字节同源；
            // 只认完整 5 字节 71 01 02 02 + 结果：00 通过、01 业务失败、其他值按未知失败。
            currentStage = OtaStage.Verification;
            var verify = await verificationService
                .VerifyAsync(nad, transferPlan, cancellationToken).ConfigureAwait(false);
            if (!verify.Success)
            {
                Report(OtaStage.Verification, 1, false,
                    $"第十四步：固件校验未通过（请求 {FormatBytes(verify.RequestUds)}，CRC32 = {transferPlan.Crc32Text}）。" +
                    $"{DescribeFailure(verify.NegativeResponse, verify.FailureText, nad)}",
                    consecutiveSuccess, failureCount,
                    completedBytes: confirmedBytes, totalBytes: totalBytes);

                return OtaSessionResult.Failed(
                    OtaStage.Verification,
                    FailureSummary("固件校验未通过", verify.NegativeResponse, verify.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion, eraseOutcome, downloadOutcome,
                    CreateVerificationOutcome(transferPlan, verify));
            }

            var verificationOutcome = CreateVerificationOutcome(transferPlan, verify);

            Report(OtaStage.Verification, 1, true,
                $"第十四步：固件校验通过，CRC32 = {transferPlan.Crc32Text}，" +
                $"范围 {transferPlan.RangeText}。",
                consecutiveSuccess, failureCount,
                completedBytes: confirmedBytes, totalBytes: totalBytes);

            // 第十五步：设备复位（§15.1～§15.5）。只有校验返回 00 且事务收尾完成后才发送 11 01，
            // 校验失败、断线或取消都不进入本步；两字节请求走既有 SF 时序与 3D 轮询。
            currentStage = OtaStage.EcuReset;
            var reset = await resetService.ResetAsync(nad, cancellationToken).ConfigureAwait(false);
            if (!reset.Success)
            {
                Report(OtaStage.EcuReset, 1, false,
                    $"第十五步：设备复位未确认（请求 {FormatBytes(reset.RequestUds)}）。" +
                    $"{DescribeFailure(reset.NegativeResponse, reset.FailureText, nad)}" +
                    "前序传输与校验结果仍然有效。",
                    consecutiveSuccess, failureCount,
                    completedBytes: confirmedBytes, totalBytes: totalBytes);

                return OtaSessionResult.Failed(
                    OtaStage.EcuReset,
                    FailureSummary("设备复位未确认", reset.NegativeResponse, reset.FailureText),
                    consecutiveSuccess, failureCount,
                    softwareVersion, bootloaderVersion, hardwareVersion,
                    eraseOutcome, downloadOutcome, verificationOutcome);
            }

            // 51 01 只证明设备接受了复位请求：不把它写成“新固件已启动”，也不自动重连或补发 10 01（§15.5）。
            Report(OtaStage.EcuReset, 1, true,
                $"第十五步：设备已返回复位肯定响应；" ,
                consecutiveSuccess, failureCount,
                completedBytes: confirmedBytes, totalBytes: totalBytes);

            // 复位后版本确认（§15.7）：自 51 01 起等待 50 ms，再读一次 APP 版本（22 F1 95）。
            // 只用于确认复位后的软件版本：读不到不算本次升级失败（设备可能仍在启动），不重试、不重连。
            await Task.Delay(ResetVersionDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            var afterReset = await deviceInfo
                .ReadVersionAsync(DeviceVersionKind.Firmware, nad).ConfigureAwait(false);
            string? appVersionAfterReset = afterReset.Success ? afterReset.VersionText : null;
            if (afterReset.Success)
            {
                Report(OtaStage.EcuReset, 2, true,
                    $"读取 APP 版本成功：{appVersionAfterReset}。",
                    consecutiveSuccess, failureCount,
                    completedBytes: confirmedBytes, totalBytes: totalBytes);
            }
            else
            {
                Report(OtaStage.EcuReset, 2, false,
                    $"复位后等待 {ResetVersionDelay.TotalMilliseconds:0} ms 未能读到 APP 版本：" +
                    $"{afterReset.FailureText}；不影响本次升级结论（设备可能仍在启动），" +
                    "可稍后在执行器调试页手动读取。",
                    consecutiveSuccess, failureCount,
                    completedBytes: confirmedBytes, totalBytes: totalBytes);
            }

            var resetOutcome = new OtaResetOutcome(reset.Response!, appVersionAfterReset);

            // 完成只作为进度节点上报（界面据此到 100%），不再复述前十五步的结果：
            // 每一步的过程日志已经逐条给出结论，重复汇总会把有用记录挤出视野。
            Report(OtaStage.Completed, 1, true,
                "十五步升级报文流程完成（设备已返回复位肯定响应）。",
                consecutiveSuccess, failureCount,
                completedBytes: confirmedBytes, totalBytes: totalBytes);

            return OtaSessionResult.Succeeded(
                consecutiveSuccess, failureCount, softwareVersion, bootloaderVersion, hardwareVersion,
                eraseOutcome, downloadOutcome, verificationOutcome, resetOutcome);
        }
        catch (OperationCanceledException)
        {
            Report(currentStage, 0, false, "本次 OTA 已取消。", consecutiveSuccess, failureCount);
            return OtaSessionResult.Failed(
                currentStage, "本次 OTA 已取消", consecutiveSuccess, failureCount,
                softwareVersion, bootloaderVersion, hardwareVersion);
        }
        finally
        {
            // 无论成功、失败还是取消都释放独占：断线时调度器不会恢复应用发送（§1.6.2、§11.5）。
            await diagnostics.EndOtaExclusiveHoldAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在线确认阶段：本阶段要求 <paramref name="requiredSuccesses"/> 次连续成功（第一步 3 次、第五步 2 次）；
    /// 累计第二次失败退出本次 OTA；失败后连续成功计数清零，累计失败不清零（§1.5、§5.3）。
    /// 每笔探测间隔固定 1 s，最后一次成功后不额外等待，直接交给下一步骤。
    /// </summary>
    private async Task<ProbeOutcome> RunOnlineProbeAsync(
        byte nad,
        int requiredSuccesses,
        OtaStage stage,
        string displayName,
        CancellationToken cancellationToken)
    {
        var consecutiveSuccess = 0;
        var failureCount = 0;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var result = await diagnostics.ExecuteAsync(
                new DiagnosticRequest
                {
                    Nad = nad,
                    UdsRequest = TesterPresentRequest,
                    ExpectedResponsePrefix = TesterPresentPositiveResponse,
                    // 沿用现有 SF 时序：t0 + 20 ms 首拍与节拍、回显 1000 ms、总截止 5 s（§1.8.1、§5.2）。
                    Timing = DiagnosticTiming.Default
                },
                cancellationToken).ConfigureAwait(false);

            // 间隔起点：本笔被判定成功或失败的时刻（§1.4）。
            var completedAt = timeProvider.GetTimestamp();

            // 协调层按前缀匹配；本阶段要求完整 UDS 恰好为 7E 00，不接受额外字节（§1.3.4、§5.2）。
            var response = result.Response;
            var success = result.Success
                && response is { Length: 2 }
                && response[0] == TesterPresentPositiveResponse[0]
                && response[1] == TesterPresentPositiveResponse[1];

            if (success)
            {
                consecutiveSuccess++;
                Report(stage, attempt, true,
                    $"第 {attempt} 笔{displayName}成功（连续成功 {consecutiveSuccess}/{requiredSuccesses}）",
                    consecutiveSuccess, failureCount);

                if (consecutiveSuccess >= requiredSuccesses)
                {
                    return ProbeOutcome.Passed(consecutiveSuccess, failureCount);
                }
            }
            else
            {
                failureCount++;
                consecutiveSuccess = 0;
                var reason = result.Success
                    ? "响应不是完整的 7E 00"
                    : DiagnosticFailureText.Describe(result.Reason);
                Report(stage, attempt, false,
                    $"第 {attempt} 笔{displayName}失败：{reason}（累计失败 {failureCount}/{MaxProbeFailures}）",
                    consecutiveSuccess, failureCount);

                if (failureCount >= MaxProbeFailures)
                {
                    return ProbeOutcome.Failed(
                        $"{displayName}累计失败 {failureCount} 次，退出本次 OTA（{reason}）",
                        consecutiveSuccess, failureCount);
                }
            }

            // 两笔探测之间固定 1 s：成功达到目标的最后一笔不额外等待，直接交接下一步骤（§1.4）。
            var remaining = ProbeInterval - timeProvider.GetElapsedTime(completedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>第二至第四步的单次版本读取：复用执行器调试页的请求、时序与解析规则。</summary>
    private async Task<VersionOutcome> ReadVersionStepAsync(
        OtaStage stage,
        DeviceVersionKind kind,
        string displayName,
        byte nad)
    {
        var result = await deviceInfo.ReadVersionAsync(kind, nad).ConfigureAwait(false);
        if (!result.Success)
        {
            var text = $"{displayName}读取失败：{result.FailureText}";
            Report(stage, 1, false, text, 0, 1);
            return VersionOutcome.Failed(text);
        }

        var versionText = result.VersionText ?? string.Empty;
        Report(stage, 1, true, $"{displayName}读取成功：{versionText}", 1, 0);
        return VersionOutcome.Succeeded(versionText);
    }

    /// <summary>
    /// 会话与安全访问步骤失败的日志细节：优先原始负响应数据，其次失败说明；两者都带 NAD（§6.4、§7.4、§9.4）。
    /// </summary>
    private static string DescribeFailure(DiagnosticNegativeResponse? negative, string failureText, byte nad) =>
        negative is { } value
            ? $"设备负响应：{value.Describe()}；NAD=0x{nad:X2}。"
            : $"{failureText}；NAD=0x{nad:X2}。";

    /// <summary>结果对象的失败摘要（不含“第几步”前缀，由调用方补充步骤文案）。</summary>
    private static string FailureSummary(
        string prefix,
        DiagnosticNegativeResponse? negative,
        string failureText) =>
        negative is { } rejected
            ? $"{prefix}（{rejected.NrcText}）"
            : $"{prefix}（{failureText}）";

    /// <summary>四个字节的十六进制文本，与日志中 Seed / Key 的表达一致（不用于报文发送）。</summary>
    private static string FormatBytes(byte[] values) => BitConverter.ToString(values).Replace('-', ' ');

    /// <summary>
    /// NRC 78 续期提示（§6.5）：设备仍在处理，等待窗口已刷新，事务继续。
    /// 高频重复由协调层按每次有效接收逐条上报；此处只做文本转换，不合并日志、不影响计时。
    /// </summary>
    private void HandleResponsePending(DiagnosticResponsePendingNotice notice) =>
        Notice($"设备处理中；第 {notice.PendingCount} 次，" +
            $"响应等待已刷新，继续轮询。");

    /// <summary>
    /// 非步骤结果的流程提示：只写升级日志（Info），不推进进度、不改变成功判定与计数。
    /// 订阅方（界面）异常不得影响流程推进。
    /// </summary>
    private void Notice(string message)
    {
        Debug.WriteLine($"[OTA] notice {message}");
        try
        {
            NoticeReported?.Invoke(message);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void Report(
        OtaStage stage,
        int attemptNumber,
        bool success,
        string message,
        int consecutiveSuccessCount,
        int cumulativeFailureCount,
        int completedBytes = 0,
        int totalBytes = 0)
    {
        Debug.WriteLine($"[OTA] {stage} #{attemptNumber} {(success ? "OK" : "FAIL")} {message}");
        try
        {
            StepReported?.Invoke(new OtaStepReport(
                stage, attemptNumber, success, message, consecutiveSuccessCount, cumulativeFailureCount)
            {
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes
            });
        }
        catch (Exception exception)
        {
            // 订阅方（界面）异常不得影响流程推进。
            Debug.WriteLine(exception);
        }
    }

    /// <summary>
    /// 下载结果快照（§11.2、§12.3）：只在下载申请成功后调用，
    /// <paramref name="transferExited"/> 表示是否已收到 77，不代表校验通过。
    /// </summary>
    private static OtaDownloadOutcome CreateDownloadOutcome(
        DownloadRequestResult download,
        int blockCount,
        int confirmedBytes,
        int totalBytes,
        bool transferExited) =>
        new(download.MaxNumberOfBlockLength ?? 0,
            download.PayloadCapacity,
            blockCount,
            confirmedBytes,
            totalBytes,
            transferExited);

    /// <summary>
    /// 校验结果快照（§14.4）：只有真正取到 5 字节正响应与结果字节时才生成，
    /// 传输层失败（无响应）返回 null，不虚构结果值。
    /// </summary>
    private static OtaVerificationOutcome? CreateVerificationOutcome(
        FirmwareTransferPlan plan,
        FirmwareVerificationResult result) =>
        result.Response is not null && result.ResultValue is { } resultValue
            ? new OtaVerificationOutcome(plan.Crc32, resultValue, result.Success)
            : null;

    private readonly record struct ProbeOutcome(
        bool Success,
        int ConsecutiveSuccessCount,
        int FailureCount,
        string? FailureText)
    {
        public static ProbeOutcome Passed(int consecutiveSuccessCount, int failureCount) =>
            new(true, consecutiveSuccessCount, failureCount, null);

        public static ProbeOutcome Failed(string failureText, int consecutiveSuccessCount, int failureCount) =>
            new(false, consecutiveSuccessCount, failureCount, failureText);
    }

    private readonly record struct VersionOutcome(bool Success, string? VersionText, string? FailureText)
    {
        public static VersionOutcome Succeeded(string versionText) => new(true, versionText, null);

        public static VersionOutcome Failed(string failureText) => new(false, null, failureText);
    }
}
