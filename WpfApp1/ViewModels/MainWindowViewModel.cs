using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        SerialSettings = new SerialSettingsViewModel(
            new SerialPortDiscoveryService(),
            new SerialPortConnectionService());

        MenuItems = new ObservableCollection<MenuItemViewModel>
        {
            new("执行器调试", "\uE713", new ActuatorDebugViewModel(SerialSettings))
        };

        SelectedMenuItem = MenuItems[0];
    }

    public ObservableCollection<MenuItemViewModel> MenuItems { get; }

    public SerialSettingsViewModel SerialSettings { get; }

    [ObservableProperty]
    private MenuItemViewModel? selectedMenuItem;

    [ObservableProperty]
    private object? currentViewModel;

    partial void OnSelectedMenuItemChanged(MenuItemViewModel? value)
    {
        CurrentViewModel = value?.ViewModel;
    }
}
