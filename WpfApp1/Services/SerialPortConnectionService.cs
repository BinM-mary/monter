using System.IO;
using System.Diagnostics;
using System.IO.Ports;
using System.Collections.Concurrent;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 管理单个串口的打开与释放，并将串口事件、接收解析和串口写入解耦。
/// </summary>
public sealed class SerialPortConnectionService : ISerialPortConnectionService, IDisposable
{
    private const int ReceiveQueueCapacity = 256;
    private const int TransmitQueueCapacity = 256;

    private readonly object syncRoot = new();
    private readonly ConcurrentQueue<ReceiveChunk> receiveQueue = new();
    private readonly SemaphoreSlim receiveQueueSlots =
        new(ReceiveQueueCapacity, ReceiveQueueCapacity);
    private readonly SemaphoreSlim receiveSignal = new(0);
    private readonly ConcurrentQueue<PendingWrite> transmitQueue = new();
    private readonly SemaphoreSlim transmitQueueSlots =
        new(TransmitQueueCapacity, TransmitQueueCapacity);
    private readonly SemaphoreSlim transmitSignal = new(0);
    private readonly CancellationTokenSource workerCancellation = new();
    private readonly Task receiveWorker;
    private readonly Task transmitWorker;
    private SerialPort? serialPort;
    private long lastReceiveTimestamp;
    private long connectionGeneration;
    private PendingWrite? lastTransmitRequest;
    private int disposed;

    public SerialPortConnectionService()
    {
        receiveWorker = Task.Run(ProcessReceiveQueueAsync);
        transmitWorker = Task.Run(ProcessTransmitQueueAsync);
    }

    public event Action<byte[]>? BytesReceived;

    public event Action<bool>? ConnectionStateChanged;

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

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        PendingWrite? request = null;
        await transmitQueueSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
                if (serialPort is not { IsOpen: true })
                {
                    throw new InvalidOperationException("串口未连接。");
                }

                request = new PendingWrite(
                    data.ToArray(),
                    connectionGeneration,
                    cancellationToken);
                transmitQueue.Enqueue(request);
                // 记录最近一次已提交的写入：排空等待以发送工作线程的实际完成信号为准（F3）。
                lastTransmitRequest = request;
            }

            transmitSignal.Release();
        }
        catch
        {
            transmitQueueSlots.Release();
            throw;
        }

        await request!.Completion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 等待最近一次已提交写入的真实完成。发送按单工作线程 FIFO 串行执行，
    /// 等待最近一次提交即覆盖“已排队 + 正在写入”两层在途。
    /// 等待方取消（SendAsync 的 WaitAsync 包装抛出 OCE）不改变这里的等待对象；
    /// 已取消但仍在队列中的请求最终由工作线程置为取消完成，同样会释放本等待。
    /// </summary>
    public async Task WaitForTransmitIdleAsync()
    {
        PendingWrite? last;
        lock (syncRoot)
        {
            last = lastTransmitRequest;
        }

        if (last is null)
        {
            return;
        }

        try
        {
            await last.Completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 排空等待只关心占用结束；失败原因由发起方（调度器/协调层）处理。
            Debug.WriteLine(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ClosePort();
        workerCancellation.Cancel();
        receiveSignal.Release();
        transmitSignal.Release();

        try
        {
            Task.WaitAll(receiveWorker, transmitWorker);
        }
        catch (AggregateException)
        {
            // 工作线程在释放阶段的取消由各自循环处理，不能影响应用退出。
        }

        receiveQueueSlots.Dispose();
        receiveSignal.Dispose();
        transmitQueueSlots.Dispose();
        transmitSignal.Dispose();
        workerCancellation.Dispose();
    }

    private void OpenPort(SerialPortSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var port = CreatePort(settings);
        port.DataReceived += HandleDataReceived;

        try
        {
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();

            var replacedConnection = false;
            lock (syncRoot)
            {
                replacedConnection = ClosePortCore();
                ThrowIfDisposed();
                serialPort = port;
                connectionGeneration++;
                port = null!;
                ReportReceiveActivity();
            }

            if (replacedConnection)
            {
                ConnectionStateChanged?.Invoke(false);
            }

            ConnectionStateChanged?.Invoke(true);
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
        var wasConnected = false;
        lock (syncRoot)
        {
            wasConnected = ClosePortCore();
        }

        if (wasConnected)
        {
            ConnectionStateChanged?.Invoke(false);
        }
    }

    private bool ClosePortCore()
    {
        var port = serialPort;
        serialPort = null;
        connectionGeneration++;
        Interlocked.Exchange(ref lastReceiveTimestamp, 0);

        FailQueuedTransmits(new InvalidOperationException("串口已断开。"));
        ClearQueuedReceives();

        if (port is null)
        {
            return false;
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

        return true;
    }

    private void HandleDataReceived(object sender, SerialDataReceivedEventArgs eventArgs)
    {
        byte[]? receivedBytes = null;
        long generation;
        lock (syncRoot)
        {
            if (!ReferenceEquals(sender, serialPort) || serialPort is null)
            {
                return;
            }

            try
            {
                var byteCount = serialPort.BytesToRead;
                if (byteCount == 0)
                {
                    return;
                }

                receivedBytes = new byte[byteCount];
                var readCount = serialPort.Read(receivedBytes, 0, byteCount);
                if (readCount != byteCount)
                {
                    Array.Resize(ref receivedBytes, readCount);
                }

                generation = connectionGeneration;
                ReportReceiveActivity();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
            {
                return;
            }
        }

        if (receivedBytes is { Length: > 0 })
        {
            EnqueueReceivedBytes(receivedBytes, generation);
        }
    }

    private void EnqueueReceivedBytes(byte[] data, long generation)
    {
        try
        {
            // RX 队列有界但不丢弃数据；队列满时短暂阻塞串口事件线程，等待解析线程释放空间。
            receiveQueueSlots.Wait(workerCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            receiveQueueSlots.Release();
            return;
        }

        receiveQueue.Enqueue(new ReceiveChunk(data, generation));
        receiveSignal.Release();
    }

    private async Task ProcessReceiveQueueAsync()
    {
        try
        {
            while (true)
            {
                await receiveSignal.WaitAsync(workerCancellation.Token).ConfigureAwait(false);
                while (receiveQueue.TryDequeue(out var chunk))
                {
                    receiveQueueSlots.Release();

                    if (!IsCurrentConnection(chunk.Generation))
                    {
                        continue;
                    }

                    try
                    {
                        BytesReceived?.Invoke(chunk.Data);
                    }
                    catch (Exception exception)
                    {
                        // 解析或业务订阅异常不能终止后续 RX 队列消费。
                        Debug.WriteLine(exception);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessTransmitQueueAsync()
    {
        try
        {
            while (true)
            {
                await transmitSignal.WaitAsync(workerCancellation.Token).ConfigureAwait(false);
                while (transmitQueue.TryDequeue(out var request))
                {
                    transmitQueueSlots.Release();
                    ProcessPendingWrite(request);
                }
            }
        }
        catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            FailQueuedTransmits(new ObjectDisposedException(nameof(SerialPortConnectionService)));
        }
    }

    private void ProcessPendingWrite(PendingWrite request)
    {
        try
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            Write(request);
            request.Completion.TrySetResult(null);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            TrySetCanceled(request);
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
        }
    }

    private void Write(PendingWrite request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            if (request.Generation != connectionGeneration
                || serialPort is not { IsOpen: true } port)
            {
                throw new InvalidOperationException("串口未连接。");
            }

            port.Write(request.Data, 0, request.Data.Length);
        }
    }

    private bool IsCurrentConnection(long generation)
    {
        lock (syncRoot)
        {
            return generation == connectionGeneration
                && serialPort is { IsOpen: true };
        }
    }

    private void ClearQueuedReceives()
    {
        while (receiveQueue.TryDequeue(out _))
        {
            receiveQueueSlots.Release();
        }
    }

    private void FailQueuedTransmits(Exception exception)
    {
        while (transmitQueue.TryDequeue(out var request))
        {
            transmitQueueSlots.Release();
            request.Completion.TrySetException(exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SerialPortConnectionService));
        }
    }

    private static void TrySetCanceled(PendingWrite request)
    {
        if (request.CancellationToken.CanBeCanceled)
        {
            request.Completion.TrySetCanceled(request.CancellationToken);
        }
        else
        {
            request.Completion.TrySetCanceled();
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

    private readonly record struct ReceiveChunk(byte[] Data, long Generation);

    private sealed class PendingWrite
    {
        public PendingWrite(byte[] data, long generation, CancellationToken cancellationToken)
        {
            Data = data;
            Generation = generation;
            CancellationToken = cancellationToken;
        }

        public byte[] Data { get; }

        public long Generation { get; }

        public CancellationToken CancellationToken { get; }

        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
