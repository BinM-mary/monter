using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 共用的 LIN 单帧通道。应用帧与后续诊断均复用串口队列和有效帧事件。
/// 读取请求为 5 字节（55 AA 55 PID 00）；回复只有 DATA + Checksum，
/// 由 LinReadReplyParser 反算 PID/ID 后分发。Send 完成仅代表串口写入完成，
/// 不代表中间件总线发送或电机动作完成。
/// </summary>
public sealed class LinFrameChannel : IDisposable
{
    private readonly ISerialPortConnectionService connectionService;
    private readonly ICommunicationLogService communicationLog;
    private readonly LinReadReplyParser parser;
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
        this.frameAssemblyTimeout = frameAssemblyTimeout ?? TimeSpan.FromMilliseconds(50);
        if (this.frameAssemblyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameAssemblyTimeout));
        }

        parser = new LinReadReplyParser(catalog);
        parser.FrameReceived += HandleFrameReceived;
        parser.FrameRejected += HandleFrameRejected;
        isConnected = connectionService.ConnectedPortName is not null;
        connectionService.BytesReceived += HandleBytesReceived;
        connectionService.ConnectionStateChanged += HandleConnectionStateChanged;
        // 10ms 检查一次：缓存非空且超过 50ms 未收到新字节即丢弃残缺回复。
        receiveTimer = new Timer(ExpirePartialFrame, null, 10, 10);
    }

    public LinFrameCatalog Catalog { get; }
    public event Action<LinFrame>? FrameReceived;
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>发送读取请求（55 AA 55 PID 00），不等待回复。</summary>
    public Task SendReadRequestAsync(byte id, CancellationToken cancellationToken = default)
    {
        var definition = Catalog.Get(id);
        if (definition.IsDiagnostic || definition.Publisher != LinPublisher.Slave)
        {
            throw new InvalidOperationException($"帧 0x{id:X2} 不是从节点应用帧，不能作为读取目标。");
        }

        return WriteAsync(LinFrameCodec.CreateReadRequest(id), definition, cancellationToken);
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

    /// <summary>
    /// 发送写入应用帧（55 AA 55 PID 08 DATA CS）：08 为写入标识，不参与校验；
    /// Checksum 与增强型校验一致，以 PID（由原始 ID 计算）为种子累加全部 DATA。
    /// </summary>
    public Task SendWriteRequestAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var definition = Catalog.Get(id);
        if (definition.IsDiagnostic || definition.Publisher != LinPublisher.Master)
        {
            throw new InvalidOperationException($"帧 0x{id:X2} 不是主节点应用帧，不能作为写入目标。");
        }

        if (data.Length != definition.DataLength)
        {
            throw new ArgumentException($"帧 0x{id:X2} 必须包含 {definition.DataLength} 个数据字节。", nameof(data));
        }

        return WriteAsync(LinFrameCodec.CreateWriteRequest(id, data.Span), definition, cancellationToken);
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
