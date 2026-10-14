using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 串口连接成功后按固定周期发送读取请求（现为每 100 ms 一条），
/// 不等待回复；回复由 LinFrameChannel 统一解析并经 FrameReceived 分发。
/// 周期到点时若上一次发送尚未完成则跳过本拍，不排队堆积。
/// 多个从节点应用帧时按登记顺序轮流发送。
/// </summary>
public sealed class LinFramePollingService : IDisposable
{
    private readonly LinFrameChannel channel;
    private readonly TimeSpan period;
    private readonly object stateLock = new();
    private readonly Timer pollTimer;
    private byte[] pollIds = [];
    private int currentIndex;
    private bool isRunning;
    private bool disposed;
    private int sendInProgress;

    public LinFramePollingService(LinFrameChannel channel, TimeSpan? period = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.period = period ?? TimeSpan.FromMilliseconds(100);
        if (this.period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        pollTimer = new Timer(PollTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        channel.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    public void Start()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            pollIds = channel.Catalog.Definitions
                .Where(definition => !definition.IsDiagnostic && definition.Publisher == LinPublisher.Slave)
                .Select(definition => definition.Id)
                .ToArray();
            currentIndex = 0;
            if (pollIds.Length == 0 || isRunning)
            {
                return;
            }

            isRunning = true;
            pollTimer.Change(period, period);
        }
    }

    public void Stop()
    {
        lock (stateLock)
        {
            isRunning = false;
            pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
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
            isRunning = false;
        }

        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
        pollTimer.Dispose();
    }

    private void HandleConnectionStateChanged(bool connected)
    {
        if (connected)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void PollTimerCallback(object? state)
    {
        byte id;
        lock (stateLock)
        {
            if (!isRunning || disposed || pollIds.Length == 0)
            {
                return;
            }

            id = pollIds[currentIndex];
            currentIndex = (currentIndex + 1) % pollIds.Length;
        }

        if (Interlocked.CompareExchange(ref sendInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = SendAsync(id);
    }

    private async Task SendAsync(byte id)
    {
        try
        {
            await channel.SendReadRequestAsync(id).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 断开瞬间的发送失败只记日志，不中断轮询；断开事件会停止定时器。
            Debug.WriteLine(exception);
        }
        finally
        {
            Interlocked.Exchange(ref sendInProgress, 0);
        }
    }
}
