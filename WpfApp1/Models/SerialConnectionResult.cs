namespace WpfApp1.Models;

/// <summary>
/// 串口打开的业务结果。设备或驱动导致的预期 I/O 失败由结果表达，不向界面抛出异常。
/// </summary>
public sealed record SerialConnectionResult(bool IsSuccess, string? ErrorMessage)
{
    public static SerialConnectionResult Success() => new(true, null);

    public static SerialConnectionResult Failure(string errorMessage) => new(false, errorMessage);
}
