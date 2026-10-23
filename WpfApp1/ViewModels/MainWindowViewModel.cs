using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 应用帧（0x15/0x33/0x34）总开关，仅用于现场测试：
    /// false = 连接后总线不发任何应用帧（诊断与 OTA 照常工作），true = 正常 100 ms 应用拍。
    /// 临时静默总线时改为 false 并重新编译；测试完成后改回 true 即可，无其他改动。
    /// </summary>
    private const bool EnableApplicationFrames = true;

    private readonly SerialPortConnectionService connectionService;

    public MainWindowViewModel()
    {
        CommunicationLog = new CommunicationLogService(Application.Current.Dispatcher);
        connectionService = new SerialPortConnectionService();
        SerialSettings = new SerialSettingsViewModel(
            new SerialPortDiscoveryService(),
            connectionService);
        // 0x33：读取应用帧（LIN 无条件帧），从节点发布，回复 8 字节 DATA + 1 字节增强型 Checksum。
        // 信号布局见 协议/PROTOCOL.md §4.3；回复经 FrameReceived 绑定到执行器调试界面。
        // 0x34：读取应用帧，从节点发布，8 字节 DATA 为温度/电压两组小端 float（协议/PROTOCOL.md §12）。
        // 0x15：写入应用帧，主节点发布，线上格式 55 AA 55 PID 08 + 8 字节 DATA + Checksum
        // （Checksum 以 PID 为种子累加 DATA，不含 08）；数据布局见 协议/PROTOCOL.md §5.2。
        // 0x3C/0x3D：诊断帧，固定 8 字节且使用经典校验，方向与约束见 LinFrameDefinition；
        // 诊断收发格式、3C 回显核对与调度规则见 架构/WpfApp1诊断架构讨论记录.md §7、§11。
        FrameCatalog = new LinFrameCatalog(
        [
            new LinFrameDefinition(0x33, 8, LinPublisher.Slave, LinChecksumType.Enhanced),
            new LinFrameDefinition(0x34, 8, LinPublisher.Slave, LinChecksumType.Enhanced),
            new LinFrameDefinition(0x15, 8, LinPublisher.Master, LinChecksumType.Enhanced),
            new LinFrameDefinition(0x3C, 8, LinPublisher.Master, LinChecksumType.Classic),
            new LinFrameDefinition(0x3D, 8, LinPublisher.Slave, LinChecksumType.Classic)
        ]);
        FrameChannel = new LinFrameChannel(
            connectionService,
            CommunicationLog,
            FrameCatalog,
            timeProvider: TimeProvider.System);
        // 0x15 写入帧的当前数据状态：界面操作修改字段，调度每拍按快照组帧。
        MotorCommands = new MotorCommandService();
        // 统一发送调度器：应用通信 100 ms 一拍，拍内按 0x15 → 0x33 → 0x34 发送，相邻帧间隔 10 ms；
        // 诊断期间按单帧边界暂停应用发送，诊断队列清空后从保存的序列位置恢复。
        BusScheduler = new LinBusScheduler(
            FrameChannel,
            MotorCommands,
            timeProvider: TimeProvider.System);
        // 测试开关：关闭时连接后一帧应用帧都不发，诊断/OTA 不受影响（见 EnableApplicationFrames）。
        BusScheduler.ApplicationFramesEnabled = EnableApplicationFrames;
        // 诊断事务协调层：同一总线串行执行，3C 回显核对、超时与统一收尾。
        Diagnostics = new DiagnosticTransactionService(
            FrameChannel,
            BusScheduler,
            TimeProvider.System);
        DeviceInfo = new DeviceInformationService(Diagnostics);
        // 诊断会话控制（架构/OTA升级流程.md 第六、七步）：只构造 10 03/10 02 与判定正响应格式，不自行调度串口。
        SessionControl = new DiagnosticSessionService(Diagnostics);
        // 安全访问业务（第八、九步）：构造 27 09/27 0A、判定正响应格式并计算 Key，不缓存 Seed/Key。
        SecurityAccess = new SecurityAccessService(Diagnostics);
        // 擦除业务（第十步）：构造 31 01 FF 00 + 起始地址 + 长度，多帧发送与 3D 轮询复用协调层。
        EraseFlash = new EraseFlashService(Diagnostics);
        // 下载业务（第十一至第十三步）：构造 34/36/37 并解析 74/76/77，不保存块偏移、不自行重传。
        FirmwareDownload = new FirmwareDownloadService(Diagnostics);
        // 固件校验业务（第十四步）：构造 31 01 02 02 + 地址 + 大小 + CRC32，解析 71 01 02 02 + 结果。
        FirmwareVerification = new FirmwareVerificationService(Diagnostics);
        // 设备复位业务（第十五步）：构造 11 01，只认恰好 2 字节 51 01；不重发、不自动重连。
        EcuReset = new EcuResetService(Diagnostics);
        // OTA 升级流程编排（架构/OTA升级流程.md 第一至第十五步）：复用同一诊断协调层、版本读取、会话、
        // 安全访问、擦除、下载、校验与复位服务，不新增发送出口；固件升级页通过它执行 OTA。
        OtaUpgrade = new OtaUpgradeService(
            Diagnostics, DeviceInfo, SessionControl, SecurityAccess, EraseFlash,
            FirmwareDownload, FirmwareVerification, EcuReset, TimeProvider.System);
        ActuatorDebug = new ActuatorDebugViewModel(SerialSettings, MotorCommands, DeviceInfo, Diagnostics);
        // 固件升级页与执行器调试页共享同一 SerialSettings 实例：
        // 串口连接状态由主窗口级服务持有，页面切换不重置、不断开。
        FirmwareUpgrade = new FirmwareUpgradeViewModel(SerialSettings, OtaUpgrade);
        FrameChannel.FrameReceived += HandleStatusFrameReceived;

        MenuItems = new ObservableCollection<MenuItemViewModel>
        {
            new("执行器调试", "\uE713", ActuatorDebug),
            new("固件升级", "\uE895", FirmwareUpgrade)
        };

        SelectedMenuItem = MenuItems[0];
    }

    public ObservableCollection<MenuItemViewModel> MenuItems { get; }

    public SerialSettingsViewModel SerialSettings { get; }

    public ICommunicationLogService CommunicationLog { get; }

    public LinFrameCatalog FrameCatalog { get; }

    public LinFrameChannel FrameChannel { get; }

    public LinBusScheduler BusScheduler { get; }

    public DiagnosticTransactionService Diagnostics { get; }

    public DeviceInformationService DeviceInfo { get; }

    public DiagnosticSessionService SessionControl { get; }

    public SecurityAccessService SecurityAccess { get; }

    public EraseFlashService EraseFlash { get; }

    public FirmwareDownloadService FirmwareDownload { get; }

    public FirmwareVerificationService FirmwareVerification { get; }

    public EcuResetService EcuReset { get; }

    public OtaUpgradeService OtaUpgrade { get; }

    public MotorCommandService MotorCommands { get; }

    public ActuatorDebugViewModel ActuatorDebug { get; }

    public FirmwareUpgradeViewModel FirmwareUpgrade { get; }

    public void Dispose()
    {
        FrameChannel.FrameReceived -= HandleStatusFrameReceived;
        ActuatorDebug.Dispose();
        Diagnostics.Dispose();
        BusScheduler.Dispose();
        FrameChannel.Dispose();
        SerialSettings.Dispose();
        connectionService.Dispose();
    }

    // 应用读取帧业务绑定：RX 线程收到校验通过的回复后，解析信号并切回 UI 线程应用。
    private void HandleStatusFrameReceived(LinFrame frame)
    {
        switch (frame.Id)
        {
            case 0x33 when frame.Data.Length >= 8:
            {
                var status = MotorStatusFrameParser.Parse(frame.Data.Span);
                _ = Application.Current?.Dispatcher.BeginInvoke(() => ActuatorDebug.ApplyMotorStatus(status));
                break;
            }
            case 0x34 when frame.Data.Length >= 8:
            {
                var measurement = MotorMeasurementFrameParser.Parse(frame.Data.Span);
                _ = Application.Current?.Dispatcher.BeginInvoke(
                    () => ActuatorDebug.ApplyMotorMeasurements(measurement));
                break;
            }
        }
    }

    [ObservableProperty]
    private MenuItemViewModel? selectedMenuItem;

    [ObservableProperty]
    private object? currentViewModel;

    partial void OnSelectedMenuItemChanged(MenuItemViewModel? value)
    {
        CurrentViewModel = value?.ViewModel;
    }
}
