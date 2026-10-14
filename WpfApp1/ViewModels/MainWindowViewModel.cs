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
        // 信号布局见 LIN_APPLICATION_READ.md 第 2.2 节；业务绑定待后续阶段接入。
        FrameCatalog = new LinFrameCatalog(
        [
            new LinFrameDefinition(0x33, 8, LinPublisher.Slave, LinChecksumType.Enhanced)
        ]);
        FrameChannel = new LinFrameChannel(connectionService, CommunicationLog, FrameCatalog);
        ApplicationFrames = new ApplicationFrameService(FrameChannel);
        // 串口连接成功后每 100 ms 自动发送一次读取请求，不等待回复；回复由通道解析分发。
        FramePolling = new LinFramePollingService(FrameChannel);
        ActuatorDebug = new ActuatorDebugViewModel(SerialSettings);

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

    public ActuatorDebugViewModel ActuatorDebug { get; }

    public void Dispose()
    {
        FramePolling.Dispose();
        ApplicationFrames.Dispose();
        FrameChannel.Dispose();
        SerialSettings.Dispose();
        connectionService.Dispose();
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
