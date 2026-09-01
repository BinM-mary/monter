using System.IO.Ports;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 隔离操作系统串口枚举，避免 ViewModel 依赖 System.IO.Ports。
/// </summary>
public sealed class SerialPortDiscoveryService : ISerialPortDiscoveryService
{
    public Task<IReadOnlyList<SerialPortOption>> GetAvailablePortsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<SerialPortOption>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return SerialPort.GetPortNames()
                .OrderBy(ExtractPortNumber)
                .ThenBy(portName => portName, StringComparer.OrdinalIgnoreCase)
                .Select(portName => new SerialPortOption(portName))
                .ToArray();
        }, cancellationToken);
    }

    private static int ExtractPortNumber(string portName)
    {
        return int.TryParse(portName.AsSpan("COM".Length), out var portNumber)
            ? portNumber
            : int.MaxValue;
    }
}
