using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

/// <summary>
/// 固件升级页视图模型：持有主窗口共享的串口设置，
/// 串口连接状态由 MainWindowViewModel 级的 SerialSettings/connectionService 管理，
/// 与执行器调试页之间切换视图不会触碰连接。
/// 升级业务（文件选择、传输进度、校验等）尚未实现，当前仅承载页面框架。
/// </summary>
public partial class FirmwareUpgradeViewModel : ObservableObject
{
    public FirmwareUpgradeViewModel(SerialSettingsViewModel serialSettings)
    {
        SerialSettings = serialSettings;
    }

    /// <summary>串口设置与连接控制，与执行器调试页共用同一实例。</summary>
    public SerialSettingsViewModel SerialSettings { get; }
}
