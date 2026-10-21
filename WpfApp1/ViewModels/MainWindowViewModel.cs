using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
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
        // 诊断事务协调层：同一总线串行执行，3C 回显核对、超时与统一收尾。
        Diagnostics = new DiagnosticTransactionService(
            FrameChannel,
            BusScheduler,
            TimeProvider.System);
        DeviceInfo = new DeviceInformationService(Diagnostics);
        ActuatorDebug = new ActuatorDebugViewModel(SerialSettings, MotorCommands, DeviceInfo, Diagnostics);
        // 固件升级页与执行器调试页共享同一 SerialSettings 实例：
        // 串口连接状态由主窗口级服务持有，页面切换不重置、不断开。
        FirmwareUpgrade = new FirmwareUpgradeViewModel(SerialSettings);
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
