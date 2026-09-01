using System.IO.Ports;

namespace WpfApp1.Models;

/// <summary>
/// 串口通信参数。该模型只描述配置，不负责打开或关闭串口。
/// </summary>
public sealed record SerialPortSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    int ReadTimeout,
    int WriteTimeout)
{
    public string DisplayText => $"{PortName} · {BaudRate} · {DataBits}{ToParityAbbreviation(Parity)}{ToStopBitsText(StopBits)}";

    private static string ToParityAbbreviation(Parity parity) => parity switch
    {
        Parity.Even => "E",
        Parity.Odd => "O",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => "N"
    };

    private static string ToStopBitsText(StopBits stopBits) => stopBits switch
    {
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => "1"
    };
}
