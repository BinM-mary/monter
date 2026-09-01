using System.IO.Ports;
using System.IO;
using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 管理单个串口的打开与释放，确保连接状态不会散落在界面代码中。
/// </summary>
public sealed class SerialPortConnectionService : ISerialPortConnectionService, IDisposable
{
    private readonly object syncRoot = new();
    private SerialPort? serialPort;
    private long lastReceiveTimestamp;

    public string? ConnectedPortName
    {
        get
        {
            lock (syncRoot)
            {
                // 设备拔出后 IsOpen 可能已变为 false；仍需返回端口名，供监测层触发状态同步与资源释放。
                return serialPort?.PortName;
            }
        }
    }

    public bool HasRecentReceiveActivity(TimeSpan inactivityThreshold)
    {
        var timestamp = Interlocked.Read(ref lastReceiveTimestamp);
        return timestamp != 0 && Stopwatch.GetElapsedTime(timestamp) < inactivityThreshold;
    }

    public void ReportReceiveActivity()
    {
        Interlocked.Exchange(ref lastReceiveTimestamp, Stopwatch.GetTimestamp());
    }

    public Task<SerialConnectionResult> ConnectAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TryOpenPort(settings, cancellationToken), cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(ClosePort, cancellationToken);
    }

    public void Dispose()
    {
        ClosePort();
    }

    private void OpenPort(SerialPortSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = CreatePort(settings);
        port.DataReceived += HandleDataReceived;

        try
        {
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                ClosePortCore();
                serialPort = port;
                port = null!;
                ReportReceiveActivity();
            }
        }
        finally
        {
            port?.DataReceived -= HandleDataReceived;
            port?.Dispose();
        }
    }

    private SerialConnectionResult TryOpenPort(SerialPortSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            OpenPort(settings, cancellationToken);
            return SerialConnectionResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SerialConnectionResult.Failure(exception.Message);
        }
    }

    private void ClosePort()
    {
        lock (syncRoot)
        {
            ClosePortCore();
        }
    }

    private void ClosePortCore()
    {
        var port = serialPort;
        serialPort = null;
        Interlocked.Exchange(ref lastReceiveTimestamp, 0);

        if (port is null)
        {
            return;
        }

        try
        {
            port.DataReceived -= HandleDataReceived;
            port.Dispose();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // 物理拔出后清理底层端口句柄可能失败，状态已被置为断开，不能让清理异常影响界面。
        }
    }

    private void HandleDataReceived(object sender, SerialDataReceivedEventArgs eventArgs)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(sender, serialPort))
            {
                // 只记录活动，不读取任何字节，接收缓冲区完全由后续接收管线独占。
                ReportReceiveActivity();
            }
        }
    }

    private static SerialPort CreatePort(SerialPortSettings settings)
    {
        return new SerialPort(
            settings.PortName,
            settings.BaudRate,
            settings.Parity,
            settings.DataBits,
            settings.StopBits)
        {
            ReadTimeout = settings.ReadTimeout,
            WriteTimeout = settings.WriteTimeout
        };
    }
}
