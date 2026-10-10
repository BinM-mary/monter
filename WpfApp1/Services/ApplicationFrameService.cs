using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 应用帧操作：主节点发布完整帧，从节点数据通过 Header 读取。
/// 一次读取的等待锁位于本层，不占用底层接收线程或串口写入锁。
/// </summary>
public sealed class ApplicationFrameService : IDisposable
{
    private readonly LinFrameChannel channel;
    private readonly SemaphoreSlim readLock = new(1, 1);
    private readonly object pendingLock = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private PendingRead? pendingRead;
    private long connectionVersion;
    private bool disposed;

    public ApplicationFrameService(LinFrameChannel channel)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        channel.FrameReceived += HandleFrameReceived;
        channel.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    /// <summary>有效的从节点应用帧。周期上报和一次读取均经此入口分发。</summary>
    public event Action<LinFrame>? FrameReceived;

    public Task PublishAsync(byte id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        GetApplicationDefinition(id, LinPublisher.Master);
        return channel.SendFrameAsync(id, data, cancellationToken);
    }

    public async Task<LinFrame> ReadAsync(
        byte id,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        GetApplicationDefinition(id, LinPublisher.Slave);
        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        long startingVersion;
        lock (pendingLock)
        {
            startingVersion = connectionVersion;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetimeCancellation.Token);
        await readLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        var request = new PendingRead(id, linkedCancellation);
        try
        {
            lock (pendingLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (startingVersion != connectionVersion)
                {
                    throw new InvalidOperationException("串口连接已变化，排队的应用帧读取已终止。");
                }
                // 先登记再发送，避免响应很快返回时漏掉完整帧。
                pendingRead = request;
            }

            await channel.SendHeaderAsync(id, linkedCancellation.Token).ConfigureAwait(false);
            return await request.Completion.Task
                .WaitAsync(responseTimeout, linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (pendingLock)
            {
                if (ReferenceEquals(pendingRead, request))
                {
                    pendingRead = null;
                }
            }

            readLock.Release();
        }
    }

    public void Dispose()
    {
        lock (pendingLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingRead?.Completion.TrySetException(new ObjectDisposedException(nameof(ApplicationFrameService)));
        }

        lifetimeCancellation.Cancel();
        channel.FrameReceived -= HandleFrameReceived;
        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
        // 不在仍有异步等待者时释放 readLock，等待任务退出后由 GC 回收。
    }

    private LinFrameDefinition GetApplicationDefinition(byte id, LinPublisher publisher)
    {
        lock (pendingLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        var definition = channel.Catalog.Get(id);
        if (definition.IsDiagnostic || definition.Publisher != publisher)
        {
            throw new InvalidOperationException("该帧不属于当前操作对应的应用帧发布方向。");
        }

        return definition;
    }

    private void HandleFrameReceived(LinFrame frame)
    {
        if (frame.Definition.IsDiagnostic || frame.Definition.Publisher != LinPublisher.Slave)
        {
            return;
        }

        lock (pendingLock)
        {
            if (disposed)
            {
                return;
            }

            if (pendingRead is { } request && request.Id == frame.Id)
            {
                request.Completion.TrySetResult(frame);
            }
        }

        FrameReceived?.Invoke(frame);
    }

    private void HandleConnectionStateChanged(bool connected)
    {
        // 新连接不能沿用旧读取等待；连接/断开事件都结束当前事务。
        lock (pendingLock)
        {
            connectionVersion++;
            pendingRead?.Completion.TrySetException(new InvalidOperationException("串口连接已变化，应用帧读取已终止。"));
            // 同时取消尚在底层排队的 Header，防止旧请求在重连后被发送。
            pendingRead?.Cancellation.Cancel();
        }
    }

    private sealed class PendingRead(byte id, CancellationTokenSource cancellation)
    {
        public byte Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<LinFrame> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
