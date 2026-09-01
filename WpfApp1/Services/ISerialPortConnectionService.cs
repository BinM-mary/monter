using WpfApp1.Models;

namespace WpfApp1.Services;

public interface ISerialPortConnectionService
{
    /// <summary>
    /// 最近一次成功打开且尚未释放的端口名。即使系统已使底层句柄失效，也保留该值以便检测拔出事件。
    /// </summary>
    string? ConnectedPortName { get; }

    /// <summary>
    /// 判断串口最近是否持续有接收活动，用于避免在报文高频到达时进行不必要的端口枚举。
    /// </summary>
    bool HasRecentReceiveActivity(TimeSpan inactivityThreshold);

    /// <summary>
    /// 供后续统一接收管线在完成一次有效接收后上报活动时间。
    /// </summary>
    void ReportReceiveActivity();

    Task<SerialConnectionResult> ConnectAsync(SerialPortSettings settings, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
