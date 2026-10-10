using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 共用的 LIN 单帧通道。应用帧与后续诊断均复用串口队列和有效帧事件。
/// Send 完成仅代表串口写入完成，不代表中间件总线发送或电机动作完成。
/// </summary>
public sealed class LinFrameChannel : IDisposable
{
    private readonly ISerialPortConnectionService connectionService;
    private readonly ICommunicationLogService communicationLog;
    private readonly LinFrameParser parser;
    private readonly object stateLock = new();
    private readonly Timer receiveTimer;
    private readonly TimeSpan frameAssemblyTimeout;
    private bool isConnected;
    private bool disposed;

    public LinFrameChannel(
        ISerialPortConnectionService connectionService,
        ICommunicationLogService communicationLog,
        LinFrameCatalog catalog,
        TimeSpan? frameAssemblyTimeout = null)
    {
        this.connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        this.communicationLog = communicationLog ?? throw new ArgumentNullException(nameof(communicationLog));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.frameAssemblyTimeout = frameAssemblyTimeout ?? TimeSpan.FromSeconds(1);
        if (this.frameAssemblyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameAssemblyTimeout));
        }

        parser = new LinFrameParser(catalog);
        parser.FrameReceived += HandleFrameReceived;
        parser.FrameRejected += HandleFrameRejected;
        isConnected = connectionService.ConnectedPortName is not null;
        connectionService.BytesReceived += HandleBytesReceived;
        connectionService.ConnectionStateChanged += HandleConnectionStateChanged;
        receiveTimer = new Timer(ExpirePartialFrame, null, 100, 100);
    }

    public LinFrameCatalog Catalog { get; }
    public event Action<LinFrame>? FrameReceived;
    public event Action<bool>? ConnectionStateChanged;

    public Task SendHeaderAsync(byte id, CancellationToken cancellationToken = default)
    {
        var definition = Catalog.Get(id);
        return WriteAsync(LinFrameCodec.CreateHeader(id), definition, cancellationToken);
    }

    public Task SendFrameAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var definition = Catalog.Get(id);
        if (definition.Publisher != LinPublisher.Master)
        {
            throw new InvalidOperationException($"帧 0x{id:X2} 的数据由从节点发布，上位机应发送 Header 读取。");
        }

        return WriteAsync(LinFrameCodec.CreateFrame(definition, data.Span), definition, cancellationToken);
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            isConnected = false;
            parser.Reset();
            ConnectionStateChanged?.Invoke(false);
        }

        connectionService.BytesReceived -= HandleBytesReceived;
        connectionService.ConnectionStateChanged -= HandleConnectionStateChanged;
        receiveTimer.Dispose();
    }

    private async Task WriteAsync(byte[] raw, LinFrameDefinition definition, CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!isConnected)
            {
                throw new InvalidOperationException("串口未连接。");
            }
        }

        await connectionService.SendAsync(raw, cancellationToken).ConfigureAwait(false);
        communicationLog.RecordSent(raw, definition);
    }

    private void HandleBytesReceived(byte[] bytes)
    {
        lock (stateLock)
        {
            if (isConnected && !disposed)
            {
                parser.Append(bytes);
            }
        }
    }

    private void HandleConnectionStateChanged(bool connected)
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            isConnected = connected;
            parser.Reset();
            ConnectionStateChanged?.Invoke(connected);
        }
    }

    private void HandleFrameReceived(LinFrame frame, byte[] raw)
    {
        communicationLog.RecordReceived(raw, frame.Definition);
        // 某个业务订阅异常不能使同一接收块中的后续帧丢失。
        if (FrameReceived is { } handlers)
        {
            foreach (Action<LinFrame> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(frame);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }
        }
    }

    private void HandleFrameRejected(byte[] raw, LinFrameDefinition? definition, string error)
    {
        communicationLog.RecordReceived(raw, definition, error);
    }

    private void ExpirePartialFrame(object? state)
    {
        lock (stateLock)
        {
            if (!disposed && isConnected)
            {
                parser.ExpirePartialFrame(frameAssemblyTimeout);
            }
        }
    }
}
