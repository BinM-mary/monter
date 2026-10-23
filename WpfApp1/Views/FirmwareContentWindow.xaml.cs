using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfApp1.Views;

/// <summary>
/// 文件内容独立窗口：以只读方式查看当前已打开固件的十六进制内容。
/// 数据上下文为 FirmwareUpgradeViewModel（与主页面共享），主页面加载新文件时本窗口实时刷新；
/// 关闭即销毁，不驻留后台。标题栏去掉最小化按钮（保留最大化与关闭）。
/// </summary>
public partial class FirmwareContentWindow : Window
{
    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;

    public FirmwareContentWindow()
    {
        InitializeComponent();
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current.MainWindow;
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>去掉最小化按钮：WPF 没有对应属性，需清窗口样式的 WS_MINIMIZEBOX 位。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, style & ~WsMinimizeBox);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
