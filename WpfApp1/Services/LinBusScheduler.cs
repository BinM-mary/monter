using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 统一发送调度器（由 LinFramePollingService 演进而来，依据 WpfApp1诊断架构讨论记录.md §5、§11、§15）：
/// - 应用通信：串口连接后按 100 ms 一拍发送，先写 0x15（MotorCommandService 快照）再读 0x33，
///   发送忙则跳过该拍，不补发、不排队堆积；
/// - 单帧边界暂停：按下诊断按键后，当前单帧串口写入完成即暂停应用发送并保存游标，
///   不等待应用回复，不把应用帧插入诊断帧之间，一次事务内也不穿插应用帧；
/// - 诊断独占：3C 由协调层经本类发送；3D 按 t0 + n×20 ms 的固定节拍发送，忙则跳过该拍；
/// - 恢复：队列清空后从游标位置立即发送一帧，并衔接 100 ms 整拍；
/// - 串口唯一发送出口：应用帧与诊断帧都不绕过本类；断线停止所有调度，重连恢复应用发送。
/// </summary>
public sealed class LinBusScheduler : IDisposable
{
    private enum AppCursor : byte
    {
        WriteControlFrame,
        ReadStatusFrame
    }

    private enum SchedulerMode : byte
    {
        AppRunning,
        DiagnosticExclusive,
        Disconnected
    }

    private readonly LinFrameChannel channel;
    private readonly MotorCommandService? motorCommands;
    private readonly TimeSpan appPeriod;
    private readonly TimeProvider timeProvider;
    private readonly object stateLock = new();
    private readonly ITimer appTimer;
    private readonly ITimer pollTimer;
    private readonly byte[] statusReadIds;

    private SchedulerMode mode = SchedulerMode.Disconnected;
    private AppCursor cursor = AppCursor.WriteControlFrame;
    private int statusReadIndex;
    private bool pauseRequested;
    private bool pollingActive;
    private long pollFirstTarget;
    private long pollPeriod;
    private long pollDeadline;
    private int pollBeatIndex;

    /// <summary>本轮询所属事务的身份令牌：随 3D 提交通知回传，旧通知不能打开新事务的认领（F2 §3.3-1）。</summary>
    private object? pollOwnerToken;

    private Task? activeSendTask;
    private bool sendBusy;
    private long sendGeneration;

    /// <summary>独占申请代次：每次申请与每次撤销都递增，迟到的旧申请不得切换模式（F4）。</summary>
    private long enterRequestGeneration;

    private bool disposed;

    public LinBusScheduler(
        LinFrameChannel channel,
        MotorCommandService? motorCommands = null,
        TimeSpan? appPeriod = null,
        TimeProvider? timeProvider = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.motorCommands = motorCommands;
        this.appPeriod = appPeriod ?? TimeSpan.FromMilliseconds(100);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.appPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(appPeriod));
        }

        statusReadIds = channel.Catalog.Definitions
            .Where(definition => !definition.IsDiagnostic && definition.Publisher == LinPublisher.Slave)
            .Select(definition => definition.Id)
            .ToArray();

        appTimer = this.timeProvider.CreateTimer(
            AppTimerCallback,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        pollTimer = this.timeProvider.CreateTimer(
            PollTimerCallback,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        channel.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    public bool IsConnected
    {
        get
        {
            lock (stateLock)
            {
                return mode != SchedulerMode.Disconnected;
            }
        }
    }

    /// <summary>
    /// 一次 3D 读取即将开始提交时触发（在调度锁之外，先于通道写入）。
    /// 参数为该轮询所属事务的身份令牌；协调层据此开始有界暂存候选（F2）。
    /// </summary>
    public event Action<object?>? DiagnosticReadAttemptStarting;

    /// <summary>
    /// 一次 3D 读取写入取得成功回执后触发（在调度锁与通道锁之外）。
    /// 协调层据此确认“本笔已提供实际读取机会”，才开放响应认领（F2，审查方案 §3.3）。
    /// </summary>
    public event Action<object?>? DiagnosticReadCommitted;

    /// <summary>一次 3D 读取写入失败后触发；暂存候选随本次未提交上下文一并丢弃（F2 §3.3-5）。</summary>
    public event Action<object?>? DiagnosticReadAttemptFailed;

    /// <summary>
    /// 申请暂停应用发送并进入诊断独占：等待当前单帧串口写入完成，不等待应用回复。
    /// 已处于诊断独占时立即返回；串口未连接时抛出，由协调层结束业务。
    /// 申请绑定代次：被 <see cref="CancelPendingEnterRequest"/> 撤销或被更新批次取代后，
    /// 迟到的完成不得再切换模式（F4 §5.2）。
    /// </summary>
    public async Task EnterDiagnosticModeAsync()
    {
        Task? runningSend;
        long requestGeneration;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode == SchedulerMode.DiagnosticExclusive)
            {
                return;
            }

            if (mode == SchedulerMode.Disconnected)
            {
                throw new InvalidOperationException("串口未连接，不能进入诊断模式。");
            }

            requestGeneration = ++enterRequestGeneration;
            pauseRequested = true;
            runningSend = activeSendTask;
        }

        // 暂停边界为单帧：正在写的应用帧写完即停，配套应用帧不再发送。
        if (runningSend is not null)
        {
            await WaitSafeAsync(runningSend).ConfigureAwait(false);
        }

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode == SchedulerMode.Disconnected)
            {
                throw new InvalidOperationException("串口断开，诊断模式未能建立。");
            }

            if (requestGeneration != enterRequestGeneration)
            {
                // 申请已被撤销或被更新批次取代：不切换模式、不清除暂停标志
                //（清除属于撤销方，避免误清更新批次的暂停意图）。
                return;
            }

            mode = SchedulerMode.DiagnosticExclusive;
            pollingActive = false;
            appTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 撤销尚未完成的独占申请：提升代次并清除暂停意图。
    /// 只应在协调层确认已无有效等待者时调用（F4 §5.2 第 6 条）；
    /// 该撤销只影响独占意图，不撤回已开始的应用帧写入。
    /// </summary>
    public void CancelPendingEnterRequest()
    {
        lock (stateLock)
        {
            enterRequestGeneration++;
            if (mode == SchedulerMode.AppRunning)
            {
                pauseRequested = false;
            }
        }
    }

    /// <summary>经唯一出口发送 3C 诊断请求；返回写入回执（成功与否及完成单调时间）。</summary>
    public Task<FrameWriteReceipt> SendDiagnosticRequestAsync(
        ReadOnlyMemory<byte> pdu,
        CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode != SchedulerMode.DiagnosticExclusive)
            {
                throw new InvalidOperationException("未处于诊断独占模式，不能发送 3C 诊断请求。");
            }

            if (sendBusy)
            {
                throw new InvalidOperationException("串口发送出口被上一次写入占用。");
            }

            // 先用 sendBusy 标记占用（独立于任务引用），再启动任务；
            // 任务无论同步/异步完成都在 finally 按代次释放，避免同步完成被赋回造成永久占用。
            sendBusy = true;
            var generation = ++sendGeneration;
            var task = SendDiagnosticRequestCoreAsync(pdu, generation, cancellationToken);
            activeSendTask = task;
            return task;
        }
    }

    /// <summary>
    /// 启动本笔业务的 3D 轮询：首个 3D 在 anchor + firstDelay，之后按 period 固定节拍发送；
    /// 达到 deadline 后不再启动新的 3D。SF 路径将 firstDelay 与 period 传同一值，保持原有首拍行为；
    /// 多帧路径的 firstDelay 由隔离窗口与首次读取延迟共同决定（实施计划 §4.3）。
    /// </summary>
    public void StartResponsePolling(
        long anchor,
        TimeSpan firstDelay,
        TimeSpan period,
        long deadline,
        object? ownerToken = null)
    {
        lock (stateLock)
        {
            if (disposed || mode != SchedulerMode.DiagnosticExclusive)
            {
                return;
            }

            pollingActive = true;
            pollOwnerToken = ownerToken;
            pollFirstTarget = anchor + ToTimestampUnits(firstDelay);
            pollPeriod = ToTimestampUnits(period);
            if (pollPeriod <= 0)
            {
                pollPeriod = 1;
            }

            pollDeadline = deadline;
            pollBeatIndex = 1;
            ScheduleNextPollLocked();
        }
    }

    /// <summary>立即停止提交新的 3D；已在途的串口写入由 <see cref="WaitForSendIdleAsync"/> 等待。</summary>
    public void StopResponsePolling()
    {
        lock (stateLock)
        {
            pollingActive = false;
            if (disposed)
            {
                return;
            }

            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 等待发送出口空闲：先等调度器登记的在途任务，再等传输层真实写入排空。
    /// 外层 Send 等待被取消不代表物理写入已结束，排空以发送工作线程的
    /// 实际完成信号为准（F3/R8，审查方案 §4.3）。
    /// </summary>
    public async Task WaitForSendIdleAsync()
    {
        Task? pending;
        lock (stateLock)
        {
            pending = activeSendTask;
        }

        if (pending is not null)
        {
            await WaitSafeAsync(pending).ConfigureAwait(false);
        }

        await channel.WaitForTransmitIdleAsync().ConfigureAwait(false);
    }

    /// <summary>退出诊断模式并从保存的游标位置恢复应用发送，随后衔接 100 ms 整拍。</summary>
    public async Task ExitDiagnosticModeAsync()
    {
        Task? resumeSend = null;
        lock (stateLock)
        {
            if (disposed || mode != SchedulerMode.DiagnosticExclusive)
            {
                return;
            }

            pauseRequested = false;
            mode = SchedulerMode.AppRunning;
            pollingActive = false;
            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            if (!sendBusy && statusReadIds.Length > 0)
            {
                sendBusy = true;
                var generation = ++sendGeneration;
                resumeSend = RunAppBeatAsync(generation);
                activeSendTask = resumeSend;
            }
        }

        if (resumeSend is null)
        {
            return;
        }

        await WaitSafeAsync(resumeSend).ConfigureAwait(false);

        lock (stateLock)
        {
            // 恢复期间若又有诊断入队，重新进入的暂停仍按单帧边界生效，此处不再启动应用定时器。
            if (!disposed && !pauseRequested && mode == SchedulerMode.AppRunning)
            {
                appTimer.Change(appPeriod, appPeriod);
            }
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
            mode = SchedulerMode.Disconnected;
            pauseRequested = false;
            pollingActive = false;
            sendBusy = false;
            activeSendTask = null;
            appTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
        appTimer.Dispose();
        pollTimer.Dispose();
    }

    private void AppTimerCallback(object? state)
    {
        // 不在锁内直接发起写入：SendWriteRequestAsync / SendReadRequestAsync 会经通道申请
        // 通道锁，若此处持调度锁会与接收路径的"通道锁 → 协调层锁 → 调度锁"构成锁环。
        Task? toStart = null;
        lock (stateLock)
        {
            if (disposed || mode != SchedulerMode.AppRunning || pauseRequested || sendBusy)
            {
                return;
            }

            sendBusy = true;
            var generation = ++sendGeneration;
            toStart = RunAppBeatAsync(generation);
            activeSendTask = toStart;
        }
    }

    private void PollTimerCallback(object? state)
    {
        Task? toStart = null;
        lock (stateLock)
        {
            if (!pollingActive || disposed || mode != SchedulerMode.DiagnosticExclusive)
            {
                return;
            }

            var now = timeProvider.GetTimestamp();
            if (now >= pollDeadline)
            {
                pollingActive = false;
                pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!sendBusy)
            {
                sendBusy = true;
                var generation = ++sendGeneration;
                var owner = pollOwnerToken;
                toStart = SendDiagnosticReadCoreAsync(generation, owner);
                activeSendTask = toStart;
            }

            // 本拍已处理（发出或跳过），下一次仍按 t0 + n×period 的原定节拍。
            pollBeatIndex++;
            ScheduleNextPollLocked();
        }
    }

    private void ScheduleNextPollLocked()
    {
        var now = timeProvider.GetTimestamp();
        // 目标节拍 = 首拍时刻 + (index - 1) × period；错过则跳过，不补发。
        while (pollFirstTarget + (long)(pollBeatIndex - 1) * pollPeriod < now)
        {
            pollBeatIndex++;
        }

        var target = pollFirstTarget + (long)(pollBeatIndex - 1) * pollPeriod;
        if (!pollingActive || target >= pollDeadline)
        {
            pollingActive = false;
            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        pollTimer.Change(FromTimestampUnits(target - now), Timeout.InfiniteTimeSpan);
    }

    private async Task RunAppBeatAsync(long generation)
    {
        try
        {
            while (true)
            {
                var sendControlFrame = false;
                var readId = (byte)0;
                lock (stateLock)
                {
                    if (disposed || mode != SchedulerMode.AppRunning || pauseRequested || statusReadIds.Length == 0)
                    {
                        return;
                    }

                    sendControlFrame = cursor == AppCursor.WriteControlFrame && motorCommands is not null;
                    readId = statusReadIds[statusReadIndex % statusReadIds.Length];
                }

                // 实际写入在锁外执行，避免持调度锁申请通道锁。
                if (sendControlFrame && motorCommands is not null)
                {
                    await channel.SendWriteRequestAsync(
                        MotorCommandService.FrameId,
                        motorCommands.CreateDataSnapshot()).ConfigureAwait(false);
                }
                else
                {
                    await channel.SendReadRequestAsync(readId).ConfigureAwait(false);
                    statusReadIndex = (statusReadIndex + 1) % statusReadIds.Length;
                }

                lock (stateLock)
                {
                    cursor = sendControlFrame
                        ? AppCursor.ReadStatusFrame
                        : AppCursor.WriteControlFrame;

                    // 暂停请求已在单帧写完后的帧边界生效，配套应用帧不再发送。
                    if (disposed || mode != SchedulerMode.AppRunning || pauseRequested)
                    {
                        return;
                    }
                }

                // 写入帧的配套读取帧在同一拍内继续发送；读取帧后本拍结束（§15.3）。
                if (!sendControlFrame)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            // 断开瞬间的发送失败只记日志，不中断调度；断开事件会停止定时器。
            Debug.WriteLine(exception);
        }
        finally
        {
            ClearSendTask(generation);
        }
    }

    private async Task SendDiagnosticReadCoreAsync(long generation, object? ownerToken)
    {
        // 本方法可能由 PollTimerCallback 在调度锁内同步进入；先让出执行权，
        // 确保提交/完成通知都在调度锁与通道锁之外触发，不构成“调度锁 → 协调层锁”锁环（F2）。
        await Task.Yield();

        try
        {
            RaiseReadAttemptStarting(ownerToken);
            await channel.SendDiagnosticReadRequestAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 3D 发送失败（多为断线）记日志；业务结束由协调层的断线与截止规则处理。
            Debug.WriteLine(exception);
            RaiseReadAttemptFailed(ownerToken);
            return;
        }
        finally
        {
            ClearSendTask(generation);
        }

        // 写入取得成功回执：通知协调层“本笔已提供实际读取机会”，随后才允许响应认领（F2）。
        RaiseReadCommitted(ownerToken);
    }

    /// <summary>通知处理器异常不得中断发送调度；协调层的问题是协调层的事。</summary>
    private static void RaiseSafely(Action<object?>? handler, object? ownerToken)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(ownerToken);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void RaiseReadAttemptStarting(object? ownerToken) =>
        RaiseSafely(DiagnosticReadAttemptStarting, ownerToken);

    private void RaiseReadCommitted(object? ownerToken) =>
        RaiseSafely(DiagnosticReadCommitted, ownerToken);

    private void RaiseReadAttemptFailed(object? ownerToken) =>
        RaiseSafely(DiagnosticReadAttemptFailed, ownerToken);

    private async Task<FrameWriteReceipt> SendDiagnosticRequestCoreAsync(
        ReadOnlyMemory<byte> pdu,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await channel.SendDiagnosticRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearSendTask(generation);
        }
    }

    /// <summary>按代次清理在途发送：仅当仍由本任务占用时才清空，不误清后续任务。</summary>
    private void ClearSendTask(long generation)
    {
        lock (stateLock)
        {
            // 代次校验：只有当前登记的仍是本任务时才清空。
            // 若期间已被更晚的任务替换，说明出口已被后续任务接管，此处不动。
            if (sendGeneration == generation)
            {
                sendBusy = false;
                activeSendTask = null;
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

            if (connected)
            {
                mode = SchedulerMode.AppRunning;
                pauseRequested = false;
                pollingActive = false;
                cursor = AppCursor.WriteControlFrame;
                statusReadIndex = 0;
                pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                appTimer.Change(appPeriod, appPeriod);
            }
            else
            {
                // 断线停止应用与诊断调度；重连后重新从 0x15 开始，不恢复断线前的诊断事务。
                mode = SchedulerMode.Disconnected;
                pauseRequested = false;
                pollingActive = false;
                // 提升代次并清空在途标记：断线后旧任务即使稍后完成也不会误清新连接的任务。
                sendGeneration++;
                sendBusy = false;
                activeSendTask = null;
                appTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static async Task WaitSafeAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 等待在途写入只关心占用结束；失败原因由发起方（业务协调层）处理。
            Debug.WriteLine(exception);
        }
    }

    private long ToTimestampUnits(TimeSpan value)
    {
        return (long)(value.TotalSeconds * timeProvider.TimestampFrequency);
    }

    private TimeSpan FromTimestampUnits(long delta)
    {
        return delta <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)delta / timeProvider.TimestampFrequency);
    }
}
