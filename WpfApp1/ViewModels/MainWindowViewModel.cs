using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
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
        // 电机应用帧表尚未提供；不借用旧 CMD 或 BMW 大灯的 ID/长度。
        FrameCatalog = new LinFrameCatalog([]);
        FrameChannel = new LinFrameChannel(connectionService, CommunicationLog, FrameCatalog);
        ApplicationFrames = new ApplicationFrameService(FrameChannel);
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

    public ActuatorDebugViewModel ActuatorDebug { get; }

    public void Dispose()
    {
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
