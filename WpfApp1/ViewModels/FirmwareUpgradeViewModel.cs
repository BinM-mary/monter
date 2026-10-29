using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfApp1.Models;
using WpfApp1.Services;
using WpfApp1.Views;

namespace WpfApp1.ViewModels;

/// <summary>
/// 固件升级页视图模型：持有主窗口共享的串口设置，串口连接状态由
/// MainWindowViewModel 级的 SerialSettings/connectionService 管理，与执行器调试页
/// 之间切换视图不会触碰连接。
/// 当前实现固件文件的打开、解析与十六进制预览，并把 OtaUpgradeService 的第一至
/// 第十五步执行结果映射为日志与进度；新固件是否启动仍由实机验证。文件读取、
/// HEX 解析、CRC32、预览行构建全部在 FirmwareFileService / IntelHexParser /
/// FirmwareHexFormatter 中，本类只负责调用服务并把结果映射为界面状态。
/// </summary>
public partial class FirmwareUpgradeViewModel : ObservableObject
{
    /// <summary>日志保留上限：超过后移除最旧条目，避免长时间使用无限增长。</summary>
    private const int MaxLogEntries = 500;

    private readonly FirmwareFileService firmwareService;
    private readonly OtaUpgradeService otaService;
    private List<HexPreviewLine> previewLines = [];
    private bool previewTruncated;

    /// <summary>
    /// 本次加载的固件映像：OTA 第十至第十四步的起始地址、长度、数据与 CRC32 的唯一来源（§10.3、§14.6）。
    /// 与界面展示属性同步设置；重新打开文件或重置查看器时一并更新，避免用过期映像算擦除/下载范围。
    /// </summary>
    private FirmwareImage? currentImage;

    /// <summary>OTA 进行中标志：避免重复启动；不用于协议层判定。</summary>
    private bool isOtaRunning;

    /// <summary>本次 OTA 的取消源；界面暂未提供中止入口，仅用于释放路径（TODO：接入"停止"按钮）。</summary>
    private CancellationTokenSource? otaCancellation;

    public FirmwareUpgradeViewModel(
        SerialSettingsViewModel serialSettings,
        OtaUpgradeService otaService,
        FirmwareFileService? firmwareService = null)
    {
        SerialSettings = serialSettings;
        this.otaService = otaService ?? throw new ArgumentNullException(nameof(otaService));
        this.firmwareService = firmwareService ?? new FirmwareFileService();
        SerialSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SerialSettingsViewModel.IsConnected))
            {
                StartUpgradeCommand.NotifyCanExecuteChanged();
            }
        };
        FirmwareLogs = [];
        AddLog(FirmwareLogLevel.Info, "提示", "请打开一个 .bin 或 .hex 固件文件。");
    }

    /// <summary>串口设置与连接控制，与执行器调试页共用同一实例。</summary>
    public SerialSettingsViewModel SerialSettings { get; }

    public ObservableCollection<FirmwareLogEntry> FirmwareLogs { get; }

    /// <summary>
    /// 请求日志面板重新回到"跟随最新"：打开新固件、开始新一轮 OTA 时触发，
    /// 让用户即使之前在翻阅历史，也能直接看到新日志。
    /// 只影响视口是否跟随，不冻结、不清空任何条目；清空日志走集合 Reset 通知。
    /// </summary>
    public event EventHandler? LogFollowRequested;

    private void RequestLogFollow() => LogFollowRequested?.Invoke(this, EventArgs.Empty);

    // —— 文件信息条 ——

    [ObservableProperty]
    private string fileName = "尚未选择文件";

    /// <summary>完整路径（显示在文件名 ToolTip 与日志中）。</summary>
    [ObservableProperty]
    private string filePath = string.Empty;

    [ObservableProperty]
    private string fileTypeCaption = " HEX 固件文件";

    /// <summary>文件图标框内的短标签（BIN / HEX），未加载时显示 FILE。</summary>
    [ObservableProperty]
    private string fileGlyph = "FILE";

    [ObservableProperty]
    private bool isFirmwareLoaded;

    partial void OnIsFirmwareLoadedChanged(bool value)
    {
        StartUpgradeCommand.NotifyCanExecuteChanged();
        ShowContentCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string formatText = "—";

    [ObservableProperty]
    private string sizeText = "—";

    [ObservableProperty]
    private string checksumText = "—";

    [ObservableProperty]
    private string modifiedText = "—";

    // —— 内容查看器 ——

    [ObservableProperty]
    private string viewerSubTitle = "等待打开文件";

    [ObservableProperty]
    private string contentSummaryText = "0 行 / 0 字节";

    [ObservableProperty]
    private string addressRangeText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<HexPreviewLine>? hexPreviewLines;

    /// <summary>复制内容按钮可用性：有预览行才允许。</summary>
    [ObservableProperty]
    private bool hasPreviewContent;

    /// <summary>文件内容独立窗口的当前实例（null 表示未打开；窗口 Closed 后置空，不驻留）。</summary>
    private FirmwareContentWindow? contentWindow;

    /// <summary>OTA 发送的目标节点地址输入（支持 0x78 / 78 十六进制写法，单字节 0x00–0xFF）。</summary>
    [ObservableProperty]
    private string nadText = "0x" + DeviceInformationService.DiagnosticNad.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析 NAD 输入：接受 "0x78" / "78" 十六进制写法，限单字节 0x00–0xFF。
    /// 输入非法或为空时回退默认值 0x78 并返回 false（由调用方给出提示）。
    /// OTA 发送中凡涉及 NAD 字段的组装统一经此取值，不再使用硬编码。
    /// </summary>
    public bool TryGetNad(out byte nad)
    {
        var text = NadText?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (digits.Length is > 0 and <= 2
                && digits.All(Uri.IsHexDigit)
                && byte.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                nad = value;
                return true;
            }
        }

        nad = DeviceInformationService.DiagnosticNad;
        return false;
    }

    private void ResetNadToDefault()
    {
        NadText = "0x" + DeviceInformationService.DiagnosticNad.ToString("X2", CultureInfo.InvariantCulture);
        AddLog(FirmwareLogLevel.Warning, "NAD", "NAD 输入非法（支持 0x78 或 78 十六进制写法，范围 0x00–0xFF），已回退默认值 0x78。");
    }

    // —— 日志与进度（进度按 OtaUpgradeService 的阶段与已确认字节数推进） ——

    [ObservableProperty]
    private string logCountText = "1 条记录";

    [ObservableProperty]
    private string progressStatusText = "等待开始升级";

    [ObservableProperty]
    private double progressPercent;

    /// <summary>打开固件文件：选择 → 服务加载解析 → 映射为界面状态；异常只记日志不打断页面。</summary>
    [RelayCommand]
    private async Task OpenFirmwareAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开固件文件",
            Filter = FirmwareFileService.OpenFileDialogFilter,
            CheckFileExists = true,
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadFirmwareAsync(dialog.FileName);
    }

    private async Task LoadFirmwareAsync(string path)
    {
        // 新一轮文件打开：即使此前在翻阅历史日志，也把视口交还给最新一行。
        RequestLogFollow();

        IsFirmwareLoaded = false;
        HexPreviewLines = null;
        HasPreviewContent = false;
        ContentSummaryText = "读取中…";
        ViewerSubTitle = "正在读取文件";

        try
        {
            var image = await firmwareService.LoadAsync(path).ConfigureAwait(true);
            ApplyFirmwareImage(image);
        }
        catch (FirmwareFileFormatException exception)
        {
            ResetViewer("文件读取失败", "请检查文件内容后重新打开");
            AddLog(FirmwareLogLevel.Error, "错误", exception.Message);
            AddLog(FirmwareLogLevel.Error, "打开", $"未能打开文件：{Path.GetFileName(path)}");
        }
    }

    private void ApplyFirmwareImage(FirmwareImage image)
    {
        currentImage = image;
        var isHex = image.Format == FirmwareFileFormat.IntelHex;

        FileName = image.FileName;
        FilePath = image.FilePath;
        FileTypeCaption = isHex ? "HEX 固件文件" : "BIN 固件文件";
        FileGlyph = isHex ? "HEX" : "BIN";
        IsFirmwareLoaded = true;
        FormatText = isHex ? "HEX" : "BIN";
        SizeText = $"{FirmwareHexFormatter.FormatSize(image.ImageSize)} 映像 / {FirmwareHexFormatter.FormatSize(image.FileSize)} 文件";
        ChecksumText = "0x" + image.Crc32.ToString("X8", CultureInfo.InvariantCulture);
        ModifiedText = image.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        previewLines = FirmwareHexFormatter.BuildLines(image.Data, image.BaseAddress);
        previewTruncated = image.ImageSize > FirmwareHexFormatter.MaxPreviewBytes;
        HexPreviewLines = previewLines;
        HasPreviewContent = previewLines.Count > 0;

        var rows = previewLines.Count;
        ViewerSubTitle = isHex
            ? $"Intel HEX · 基地址 {FirmwareHexFormatter.FormatAddress(image.BaseAddress)}"
            : "BIN 十六进制视图";
        AddressRangeText = isHex && image.HexSummary is { } summary
            ? $"地址 {FirmwareHexFormatter.FormatAddress(summary.LowestAddress)} – {FirmwareHexFormatter.FormatAddress(summary.HighestAddress)}"
            : string.Empty;
        ContentSummaryText = $"{rows.ToString("N0", CultureInfo.InvariantCulture)} 行 / {image.ImageSize.ToString("N0", CultureInfo.InvariantCulture)} 字节";

        AddLog(FirmwareLogLevel.Success, "打开", $"已打开 {FormatText} 文件：{image.FileName}");
        if (isHex && image.HexSummary is { } hexSummary)
        {
            AddLog(FirmwareLogLevel.Success, "解析",
                $"共 {hexSummary.TotalRecords} 条记录（数据 {hexSummary.DataRecords} 条、{hexSummary.DataBytes} 字节），" +
                $"CRC32 {ChecksumText}");
            if (hexSummary.StartAddress is { } entry)
            {
                AddLog(FirmwareLogLevel.Info, "解析",
                    $"入口地址 {FirmwareHexFormatter.FormatAddress(entry)}");
            }
        }
        else
        {
            AddLog(FirmwareLogLevel.Success, "读取",
                $"读取完成，共 {image.ImageSize} 字节，CRC32 {ChecksumText}");
        }

        if (previewTruncated)
        {
            AddLog(FirmwareLogLevel.Warning, "预览",
                $"映像超过 {FirmwareHexFormatter.FormatSize(FirmwareHexFormatter.MaxPreviewBytes)}，仅预览前 {FirmwareHexFormatter.FormatSize(FirmwareHexFormatter.MaxPreviewBytes)}。");
        }
    }

    private void ResetViewer(string title, string hint)
    {
        currentImage = null;
        FileName = "尚未选择文件";
        FilePath = string.Empty;
        FileTypeCaption = " HEX 固件文件";
        FileGlyph = "FILE";
        IsFirmwareLoaded = false;
        FormatText = "—";
        SizeText = "—";
        ChecksumText = "—";
        ModifiedText = "—";
        ViewerSubTitle = title;
        ContentSummaryText = hint;
        AddressRangeText = string.Empty;
        HexPreviewLines = null;
        HasPreviewContent = false;
        previewLines = [];
        previewTruncated = false;
    }

    /// <summary>
    /// 打开文件内容独立窗口：窗口与主页面共享同一个视图模型（预览行、摘要、复制命令），
    /// 加载新文件时已打开的窗口会实时刷新。窗口点关闭即彻底 Close 销毁，不驻留后台；
    /// 再次点击重新创建。已打开时重复点击只把现有窗口带到前台。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsFirmwareLoaded))]
    private void ShowContent()
    {
        if (contentWindow is { IsLoaded: true })
        {
            contentWindow.Activate();
            return;
        }

        contentWindow = new FirmwareContentWindow { DataContext = this };
        contentWindow.Closed += (_, _) => contentWindow = null;
        contentWindow.Show();
    }

    /// <summary>复制当前预览的全部内容（偏移 + 十六进制 + ASCII）到剪贴板。</summary>
    [RelayCommand]
    private void CopyContent()
    {
        if (previewLines.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(FirmwareHexFormatter.BuildCopyText(previewLines));
            AddLog(FirmwareLogLevel.Success, "复制", $"已复制 {previewLines.Count} 行预览内容到剪贴板。");
        }
        catch (Exception exception)
        {
            AddLog(FirmwareLogLevel.Error, "复制", $"复制失败：{exception.Message}");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        FirmwareLogs.Clear();
        RefreshLogCount();
        AddLog(FirmwareLogLevel.Info, "日志", "已清空。");
    }

    /// <summary>
    /// 保存日志：把当前展示的全部日志行（时间 [标签] 消息）另存为纯文本文件。
    /// 默认文件名含保存时刻（log_yyyyMMdd_HHmmss.log）；只导出副本，不清空、不改动现有日志；
    /// 日志为空时弹窗提示且不生成空文件。
    /// </summary>
    [RelayCommand]
    private void SaveLog()
    {
        if (FirmwareLogs.Count == 0)
        {
            MessageBox.Show("当前没有日志可保存。", "保存日志", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存日志",
            FileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var content = string.Join(
                Environment.NewLine,
                FirmwareLogs.Select(entry => $"{entry.Time:HH:mm:ss} [{entry.Tag}] {entry.Message}"))
                + Environment.NewLine;
            File.WriteAllText(dialog.FileName, content);
            AddLog(FirmwareLogLevel.Success, "日志",
                $"已保存 {FirmwareLogs.Count} 条日志到 {Path.GetFileName(dialog.FileName)}。");
        }
        catch (Exception exception)
        {
            AddLog(FirmwareLogLevel.Error, "日志", $"保存日志失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 配置 NAD：校验输入框内容，合法则规范化为 0xXX 并记录；非法或为空回退默认 0x78 并提示。
    /// 该值将作为后续 OTA 发送中 NAD 字段的组装来源。
    /// </summary>
    [RelayCommand]
    private void ConfigureNad()
    {
        if (TryGetNad(out var nad))
        {
            NadText = "0x" + nad.ToString("X2", CultureInfo.InvariantCulture);
            AddLog(FirmwareLogLevel.Info, "NAD", $"NAD 已设置为 {NadText}，后续 OTA 发送将使用该节点地址。");
        }
        else
        {
            ResetNadToDefault();
        }
    }

    /// <summary>
    /// 开始升级：执行 OTA 升级流程第一至第十五步（架构/OTA升级流程.md）——
    /// 第一步 TesterPresent 连续在线确认，第二步读取 APP（软件）版本；
    /// 第三、第四步（Bootloader 版本、硬件版本）当前默认跳过，只读 APP 版本，
    /// 第五步再次连续两次在线确认，第六步进入扩展诊断会话（10 03），第七步进入编程会话（10 02），
    /// 第八步请求 Seed（27 09），第九步按中间件算法计算并发送 Key（27 0A），
    /// 第十步按本次固件的起始地址与大小发送擦除请求（31 01 FF 00，12 字节多帧），
    /// 第十一步申请下载（34 / 74），第十二步逐块传输（36 / 76），
    /// 第十三步结束数据传输（37 / 77），第十四步固件校验（31 01 02 02，结果 00/01），
    /// 第十五步设备复位（11 01 / 51 01）。
    /// 需已加载固件且串口已连接；任一步失败立即退出本次 OTA 并记录原因。
    /// 复位肯定响应不等于新固件已启动运行，启动验证另行实机确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartUpgrade))]
    private async Task StartUpgradeAsync()
    {
        if (isOtaRunning)
        {
            return;
        }

        // 新一轮升级：交还视口，保证"开始 OTA"后的过程日志立刻可见。
        RequestLogFollow();

        // 擦除/下载/校验范围来自加载时的固件映像快照；界面标志与映像必须同时有效，
        // 避免用空映像进入第十步或第十一步。
        if (currentImage is not { } firmware)
        {
            ProgressStatusText = "尚未加载固件";
            AddLog(FirmwareLogLevel.Warning, "OTA", "尚未加载固件文件，未开始 OTA。");
            return;
        }

        // NAD 组装来源统一走输入框：非法或为空时回退默认值并提示，再以回退后的值继续。
        if (!TryGetNad(out var nad))
        {
            ResetNadToDefault();
        }

        isOtaRunning = true;
        otaCancellation = new CancellationTokenSource();
        var cancellationToken = otaCancellation.Token;
        StartUpgradeCommand.NotifyCanExecuteChanged();
        otaService.StepReported += HandleOtaStepReported;
        otaService.NoticeReported += HandleOtaNotice;

        ProgressPercent = 0;
        ProgressStatusText = "正在进入 OTA（暂停应用通信）";
        AddLog(FirmwareLogLevel.Info, "OTA", $"开始 OTA：目标 NAD 0x{nad:X2}，第一步在线确认。");

        OtaSessionResult result;
        try
        {
            result = await otaService.RunAsync(nad, firmware, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // 服务内部已按失败结果返回；此处只兜底意外异常，避免异常逃出命令。
            ProgressStatusText = "OTA 异常终止";
            AddLog(FirmwareLogLevel.Error, "OTA", $"OTA 异常终止：{exception.Message}");
            result = OtaSessionResult.Failed(OtaStage.OnlineProbe, exception.Message, 0, 0);
        }
        finally
        {
            otaService.StepReported -= HandleOtaStepReported;
            otaService.NoticeReported -= HandleOtaNotice;
            otaCancellation?.Dispose();
            otaCancellation = null;
            isOtaRunning = false;
            StartUpgradeCommand.NotifyCanExecuteChanged();
        }

        ApplyOtaResult(result);
    }

    /// <summary>OTA 流程提示（如 NRC 78 续期）：后台线程触发，只写日志，不改进度与结果。</summary>
    private void HandleOtaNotice(string message)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => AddLog(FirmwareLogLevel.Info, "OTA", message));
            return;
        }

        AddLog(FirmwareLogLevel.Info, "OTA", message);
    }

    /// <summary>OTA 分步进展：服务在后台线程上报，日志与进度切回 UI 线程更新。</summary>
    private void HandleOtaStepReported(OtaStepReport report)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ApplyOtaStep(report));
            return;
        }

        ApplyOtaStep(report);
    }

    private void ApplyOtaStep(OtaStepReport report)
    {
        // 完成本身只推进度与状态文案，不再追加汇总日志：各步骤的结果日志已经完整，
        // 再复述一遍只会把有用的过程记录挤出视野。
        if (report.Stage != OtaStage.Completed)
        {
            AddLog(
                report.Success ? FirmwareLogLevel.Success : FirmwareLogLevel.Warning,
                report.Stage switch
                {
                    OtaStage.OnlineProbe => "OTA-1",
                    OtaStage.SoftwareVersion => "OTA-2",
                    OtaStage.BootloaderVersion => "OTA-3",
                    OtaStage.HardwareVersion => "OTA-4",
                    OtaStage.OnlineProbeAgain => "OTA-5",
                    OtaStage.ExtendedSession => "OTA-6",
                    OtaStage.ProgrammingSession => "OTA-7",
                    OtaStage.SeedRequest => "OTA-8",
                    OtaStage.KeySend => "OTA-9",
                    OtaStage.Erase => "OTA-10",
                    OtaStage.DownloadRequest => "OTA-11",
                    OtaStage.DataTransfer => "OTA-12",
                    OtaStage.TransferExit => "OTA-13",
                    OtaStage.Verification => "OTA-14",
                    OtaStage.EcuReset => "OTA-15",
                    _ => "OTA"
                },
                report.Message);
        }

        // 进度：第一步三笔探测占 0–18%，第二至第四步各 6%，第五步 39–42%，
        // 第六步 45%、第七步 47%、第八步 50%、第九步 53%、第十步 56%、申请下载 58%，
        // 第十二步按**已确认字节**占 58–96%，第十三步 97%、第十四步 98%、第十五步 99%，完成 100%。
        // 早期步骤的数值随本轮增补重新分配，仅影响显示，不改变任何成功判据与计时。
        // 注意：当前默认跳过第三、第四步（OtaUpgradeService.ReadBootloaderAndHardwareVersions = false），
        // 因此 30/36 两档不会命中，进度由第二步的 24 直接进入再次确认的 36–42。
        ProgressPercent = report.Stage switch
        {
            OtaStage.OnlineProbe => Math.Min(6 * report.ConsecutiveSuccessCount, 18),
            OtaStage.SoftwareVersion => 24,
            OtaStage.BootloaderVersion => 30,
            OtaStage.HardwareVersion => 36,
            OtaStage.OnlineProbeAgain => Math.Min(36 + (3 * report.ConsecutiveSuccessCount), 42),
            OtaStage.ExtendedSession => 45,
            OtaStage.ProgrammingSession => 47,
            OtaStage.SeedRequest => 50,
            OtaStage.KeySend => 53,
            OtaStage.Erase => 56,
            OtaStage.DownloadRequest => 58,
            OtaStage.DataTransfer => ComputeTransferProgress(report),
            OtaStage.TransferExit => 97,
            OtaStage.Verification => 98,
            OtaStage.EcuReset => 99,
            _ => 100
        };
        ProgressStatusText = report.Stage switch
        {
            OtaStage.OnlineProbe =>
                $"第一步 在线确认 {report.ConsecutiveSuccessCount}/{OtaUpgradeService.RequiredConsecutiveProbes}（累计失败 {report.CumulativeFailureCount}）",
            OtaStage.SoftwareVersion => "第二步 读取软件版本",
            OtaStage.BootloaderVersion => "第三步 读取 Bootloader 版本",
            OtaStage.HardwareVersion => "第四步 读取硬件版本",
            OtaStage.OnlineProbeAgain =>
                $"第五步 再次在线确认 {report.ConsecutiveSuccessCount}/{OtaUpgradeService.RequiredSecondProbeSuccesses}（累计失败 {report.CumulativeFailureCount}）",
            OtaStage.ExtendedSession => "第六步 进入扩展诊断会话",
            OtaStage.ProgrammingSession => "第七步 进入编程会话",
            OtaStage.SeedRequest => "第八步 请求 Seed",
            OtaStage.KeySend => "第九步 计算并发送 Key",
            OtaStage.Erase => "第十步 擦除 Flash",
            OtaStage.DownloadRequest => "第十一步 申请固件下载",
            OtaStage.DataTransfer =>
                $"第十二步 传输固件 第 {report.AttemptNumber} 块（{report.CompletedBytes:N0}/{report.TotalBytes:N0} 字节）",
            OtaStage.TransferExit => "第十三步 结束固件数据传输",
            OtaStage.Verification => "第十四步 固件校验",
            OtaStage.EcuReset => report.AttemptNumber >= 2
                ? "复位后确认 APP 版本"
                : "第十五步 设备复位",
            _ => "十五步升级报文流程完成（设备已返回复位肯定响应）"
        };
    }

    /// <summary>
    /// 第十二步的传输进度：按**已确认字节**折算到 58–96%，
    /// 不使用已发送块数或串口写入完成数，避免把未确认的块算成进度（§12.2）。
    /// </summary>
    private static int ComputeTransferProgress(OtaStepReport report)
    {
        if (report.TotalBytes <= 0)
        {
            return 58;
        }

        var confirmed = Math.Clamp(report.CompletedBytes, 0, report.TotalBytes);
        var percent = 58 + (int)(38.0 * confirmed / report.TotalBytes);
        return Math.Clamp(percent, 58, 96);
    }

    private void ApplyOtaResult(OtaSessionResult result)
    {
        if (result.Success)
        {
            // 成功后不再追加汇总日志：第一至第十五步的结果已由各步骤日志逐条给出，
            // 重复的汇总与“待确认项”会把真正的过程记录挤出视野；结论只留在进度区状态文案。
            // 文案与“新固件已启动运行”分开：51 01 只代表设备返回了复位肯定响应（§15.5）；
            // 复位后 50 ms 那次 APP 版本读取成功时，把它显示在状态行里便于直接核对（§15.7）。
            ProgressPercent = 100;
            ProgressStatusText = result.ResetOutcome?.AppVersionAfterReset is { Length: > 0 } afterResetVersion
                ? $"升级完成，复位后 APP 版本 {afterResetVersion}"
                : "十五步升级报文流程完成，设备已返回复位肯定响应";
            return;
        }

        ProgressPercent = 0;
        ProgressStatusText = $"OTA 已退出：{result.FailureText}";
        AddLog(FirmwareLogLevel.Error, "OTA", $"本次 OTA 结束于 {result.FailedStage}：{result.FailureText}");
    }

    private bool CanStartUpgrade() =>
        IsFirmwareLoaded && SerialSettings.IsConnected && !isOtaRunning;

    private void AddLog(FirmwareLogLevel level, string tag, string message)
    {
        FirmwareLogs.Add(new FirmwareLogEntry(DateTimeOffset.Now, level, tag, message));
        while (FirmwareLogs.Count > MaxLogEntries)
        {
            FirmwareLogs.RemoveAt(0);
        }

        RefreshLogCount();
    }

    private void RefreshLogCount()
    {
        LogCountText = $"{FirmwareLogs.Count.ToString("N0", CultureInfo.InvariantCulture)} 条记录";
    }
}
