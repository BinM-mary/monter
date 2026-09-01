using WpfApp1.Models;

namespace WpfApp1.Services;

public interface ISerialPortDiscoveryService
{
    Task<IReadOnlyList<SerialPortOption>> GetAvailablePortsAsync(CancellationToken cancellationToken = default);
}
