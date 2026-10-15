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
        // 信号布局见 PROTOCOL.md §4.3；回复经 FrameReceived 绑定到执行器调试界面。
        // 0x15：写入应用帧，主节点发布，线上格式 55 AA 55 PID 08 + 8 字节 DATA + Checksum
        // （Checksum 以 PID 为种子累加 DATA，不含 08）；数据布局见 PROTOCOL.md §5.2。
        FrameCatalog = new LinFrameCatalog(
        [
            new LinFrameDefinition(0x33, 8, LinPublisher.Slave, LinChecksumType.Enhanced),
            new LinFrameDefinition(0x15, 8, LinPublisher.Master, LinChecksumType.Enhanced)
        ]);
        FrameChannel = new LinFrameChannel(connectionService, CommunicationLog, FrameCatalog);
        ApplicationFrames = new ApplicationFrameService(FrameChannel);
        // 0x15 写入帧的当前数据状态：界面操作修改字段，轮询每拍按快照组帧。
        MotorCommands = new MotorCommandService();
        // 串口连接成功后每 100 ms 一拍：先发 0x15 写入帧（当前快照），再发 0x33 读取请求，均不等待回复。
        FramePolling = new LinFramePollingService(FrameChannel, MotorCommands);
        ActuatorDebug = new ActuatorDebugViewModel(SerialSettings, MotorCommands);
        FrameChannel.FrameReceived += HandleStatusFrameReceived;

        MenuItems = new ObservableCollection<MenuItemViewModel>
        {
            new("执行器调试", "\uE713", ActuatorDebug)
        };

        SelectedMenuItem = MenuItems[0];
    }

    public ObservableCollection<MenuItemViewModel> MenuItems { get; }

    public SerialSettingsViewModel SerialSettings { get; }

    public ICommunicationLogService CommunicationLog { get; }

    public LinFrameCatalog FrameCatalog { get; }

    public LinFrameChannel FrameChannel { get; }

    public ApplicationFrameService ApplicationFrames { get; }

    public LinFramePollingService FramePolling { get; }

    public MotorCommandService MotorCommands { get; }

    public ActuatorDebugViewModel ActuatorDebug { get; }

    public void Dispose()
    {
        FrameChannel.FrameReceived -= HandleStatusFrameReceived;
        FramePolling.Dispose();
        ApplicationFrames.Dispose();
        FrameChannel.Dispose();
        SerialSettings.Dispose();
        connectionService.Dispose();
    }

    // 0x33 状态帧业务绑定：RX 线程收到校验通过的回复后，解析信号并切回 UI 线程应用。
    private void HandleStatusFrameReceived(LinFrame frame)
    {
        if (frame.Id != 0x33 || frame.Data.Length < 8)
        {
            return;
        }

        var status = MotorStatusFrameParser.Parse(frame.Data.Span);
        _ = Application.Current?.Dispatcher.BeginInvoke(() => ActuatorDebug.ApplyMotorStatus(status));
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
