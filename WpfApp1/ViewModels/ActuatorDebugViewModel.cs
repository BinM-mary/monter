using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WpfApp1.ViewModels;

public partial class ActuatorDebugViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "执行器调试";

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string statusText = "系统就绪";

    [RelayCommand]
    private void Test()
    {
        StatusText = $"你好，{UserName}";
    }
}
