using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WpfApp1.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        MenuItems = new ObservableCollection<MenuItemViewModel>
        {
            new("执行器调试", "\uE713", new ActuatorDebugViewModel())
        };

        SelectedMenuItem = MenuItems[0];
    }

    public ObservableCollection<MenuItemViewModel> MenuItems { get; }

    [ObservableProperty]
    private MenuItemViewModel? selectedMenuItem;

    [ObservableProperty]
    private object? currentViewModel;

    partial void OnSelectedMenuItemChanged(MenuItemViewModel? value)
    {
        CurrentViewModel = value?.ViewModel;
    }
}
