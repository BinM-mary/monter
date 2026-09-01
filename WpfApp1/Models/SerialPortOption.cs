namespace WpfApp1.Models;

/// <summary>
/// 供界面选择的、由系统枚举出的串口。
/// </summary>
public sealed record SerialPortOption(string Name)
{
    public string DisplayName => Name;
}
