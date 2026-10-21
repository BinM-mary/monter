using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 统一发送调度器（由 LinFramePollingService 演进而来，依据 架构/WpfApp1诊断架构讨论记录.md §5、§11、§15）：
/// - 应用通信：串口连接后按 100 ms 一拍发送，拍内按 0x15（MotorCommandService 快照）→ 0x33 → 0x34
///   的顺序发送，相邻应用帧间隔 10 ms；发送忙则跳过该拍，不补发、不排队堆积；
/// - 单帧边界暂停：按下诊断按键后立即停止启动后续应用帧，当前正在写的单帧照常写完并保存序列位置；
/// - 诊断前应用回复收尾：按 ID 登记待回复标志，从触发时刻起最多等 100 ms；标志全清即提前放行，
///   到期强制全清后放行；放行前仍等已开始的那一笔写入完成，完成后不再追加等待；
/// - 不把应用帧插入诊断帧之间，一次事务内也不穿插应用帧；
/// - 诊断独占：3C 由协调层经本类发送；3D 按 t0 + n×20 ms 的固定节拍发送，忙则跳过该拍；
/// - 恢复：队列清空后从保存的序列位置立即续发，并衔接 100 ms 整拍；
/// - 串口唯一发送出口：应用帧与诊断帧都不绕过本类；断线停止所有调度，重连恢复应用发送。
/// </summary>
public sealed class LinBusScheduler : IDisposable
{
    private readonly record struct AppFrameStep(byte Id, bool IsWrite);

    private enum SchedulerMode : byte
    {
        AppRunning,
        DiagnosticExclusive,
        Disconnected
    }

    private readonly LinFrameChannel channel;
    private readonly MotorCommandService? motorCommands;
    private readonly TimeSpan appPeriod;
    private readonly TimeSpan appInterFrameGap;
    private readonly TimeProvider timeProvider;
    private readonly object stateLock = new();
    private readonly ITimer appTimer;
    private readonly ITimer pollTimer;
    private readonly AppFrameStep[] appSequence;

    /// <summary>诊断前应用回复收尾窗口：从触发诊断时刻起算的最长等待，默认 100 ms。</summary>
    private readonly TimeSpan appReplyDrainWindow;

    /// <summary>
    /// 应用待回复登记：按帧 ID 合并的状态，不是逐请求计数器（诊断讨论记录 §19.2）。
    /// 发送前登记，收到完整且校验通过的对应 ID 回复时清除；部分字节与错误候选不清除。
    /// </summary>
    private readonly HashSet<byte> pendingAppReplyIds = [];

    /// <summary>
    /// 收尾等待信号：登记清空时唤醒等待方，等待结束即置空。
    /// 必须带 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>，
    /// 否则在锁内 TrySetResult 会同步执行续体，与接收线程的调用栈交叉成环。
    /// </summary>
    private TaskCompletionSource? appReplyDrainSignal;

    private SchedulerMode mode = SchedulerMode.Disconnected;
    private int appStepIndex;
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
        TimeSpan? appInterFrameGap = null,
        TimeSpan? appReplyDrainWindow = null,
        TimeProvider? timeProvider = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.motorCommands = motorCommands;
        this.appPeriod = appPeriod ?? TimeSpan.FromMilliseconds(100);
        this.appInterFrameGap = appInterFrameGap ?? TimeSpan.FromMilliseconds(10);
        this.appReplyDrainWindow = appReplyDrainWindow ?? TimeSpan.FromMilliseconds(100);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.appPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(appPeriod));
        }

        if (this.appInterFrameGap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(appInterFrameGap));
        }

        if (this.appReplyDrainWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(appReplyDrainWindow));
        }

        var statusReadIds = channel.Catalog.Definitions
            .Where(definition => !definition.IsDiagnostic && definition.Publisher == LinPublisher.Slave)
            .Select(definition => definition.Id)
            .ToArray();

        // 应用拍序列：0x15 写入在前，随后按帧表顺序读取 0x33、0x34。
        // 没有从节点读取帧时不启动应用拍，保持原行为。
        var steps = new List<AppFrameStep>();
        if (statusReadIds.Length > 0)
        {
            if (this.motorCommands is not null)
            {
                steps.Add(new AppFrameStep(MotorCommandService.FrameId, IsWrite: true));
            }

            steps.AddRange(statusReadIds.Select(id => new AppFrameStep(id, IsWrite: false)));
        }

        appSequence = steps.ToArray();

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
        // 应用回复到达即清对应 ID 的待回复登记；诊断候选走 DiagnosticPacketReceived，不进入此处。
        channel.FrameReceived += HandleAppFrameReceived;
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
    /// 申请暂停应用发送并进入诊断独占：先按单帧边界停止启动后续应用帧，
    /// 再从触发时刻起最多等待 <see cref="appReplyDrainWindow"/> 让已发出应用帧的回复收尾，
    /// 最后等待正在写的单帧串口写入完成。已处于诊断独占时立即返回；串口未连接时抛出，由协调层结束业务。
    /// 申请绑定代次：被 <see cref="CancelPendingEnterRequest"/> 撤销或被更新批次取代后，
    /// 迟到的完成不得再切换模式（F4 §5.2）。
    /// </summary>
    public async Task EnterDiagnosticModeAsync()
    {
        // t_req：有效诊断操作触发并发起应用暂停申请的时刻，收尾截止固定为 t_req + 窗口长度。
        // 不随后续写入完成、回复到达或新字节刷新，避免把“最多等 100 ms”变成“至少等 100 ms”。
        var requestTimestamp = timeProvider.GetTimestamp();

        Task? runningSend;
        long requestGeneration;
        Task? drainSignalTask = null;
        TimeSpan drainRemaining = TimeSpan.Zero;
        bool replyDrainPending = false;

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

            if (pendingAppReplyIds.Count > 0)
            {
                replyDrainPending = true;

                // 扣除从 t_req 到进入临界区的消耗，使截止保持为绝对时刻。
                drainRemaining = appReplyDrainWindow - timeProvider.GetElapsedTime(requestTimestamp);
                if (drainRemaining > TimeSpan.Zero)
                {
                    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    appReplyDrainSignal = signal;
                    drainSignalTask = signal.Task;
                }
            }
        }

        if (replyDrainPending)
        {
            // 与登记清空竞速：全部清除即提前放行，到期则放弃等待。
            var drainStartTimestamp = timeProvider.GetTimestamp();
            if (drainSignalTask is not null)
            {
                await Task
                    .WhenAny(drainSignalTask, Task.Delay(drainRemaining, timeProvider))
                    .ConfigureAwait(false);
            }

            FinishReplyDrain(drainStartTimestamp);
        }

        // 不打断已开始的应用帧写入：写入跨过收尾截止时，写完即放行，不追加第二轮等待。
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
    /// 结束应用回复收尾等待：无论提前清空还是到期，都清空全部登记（提前清空时本就为空，幂等），
    /// 并记录等待结果，供实机复测判断收尾窗口是否真正生效。
    /// </summary>
    private void FinishReplyDrain(long drainStartTimestamp)
    {
        int forced;
        TimeSpan waited;
        lock (stateLock)
        {
            forced = pendingAppReplyIds.Count;
            waited = timeProvider.GetElapsedTime(drainStartTimestamp);
            ClearAllPendingRepliesLocked();
        }

        // 只在锁外写日志：诊断切换频率很低，但仍然不把 I/O 放在调度锁内。
        Debug.WriteLine(forced > 0
            ? $"应用回复收尾：等待 {waited.TotalMilliseconds:F0} ms 到期，强制清除 {forced} 个待回复标志"
            : $"应用回复收尾：等待 {waited.TotalMilliseconds:F0} ms 后标志全部清空");
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

            // 撤销即放弃本轮收尾等待：不清登记与信号的话，撤销要拖满整个窗口才生效，
            // 会把紧跟其后的新一批诊断进入一并推迟。
            ClearAllPendingRepliesLocked();
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

    /// <summary>退出诊断模式并从保存的序列位置恢复应用发送，随后衔接 100 ms 整拍。</summary>
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

            // 兜底：进入诊断时已清空，此处防止异常路径把旧登记带到下一轮切换。
            ClearAllPendingRepliesLocked();

            if (!sendBusy && appSequence.Length > 0)
            {
                // 暂停在最后一帧之后：新一轮应用拍从 0x15 重新开始。
                if (appStepIndex >= appSequence.Length)
                {
                    appStepIndex = 0;
                }

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
                appTimer.Change(appPeriod, Timeout.InfiniteTimeSpan);
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
            // 释放等待方，避免 Dispose 期间仍有任务挂在收尾窗口上。
            ClearAllPendingRepliesLocked();
            appTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
        channel.FrameReceived -= HandleAppFrameReceived;
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
            if (disposed || mode != SchedulerMode.AppRunning)
            {
                return;
            }

            // 单次定时器：本拍启动后，从当前时刻起 100 ms 再尝试下一拍。
            // 相比固定周期定时器，可避免回调延迟造成的"上一拍 85 ms、下一拍 110 ms"式相位压缩。
            // 即使 pauseRequested 为真也继续排下一拍：独占申请被撤销时应用通信能自动恢复。
            if (appSequence.Length > 0)
            {
                appTimer.Change(appPeriod, Timeout.InfiniteTimeSpan);
            }

            if (pauseRequested || sendBusy)
            {
                return;
            }

            appStepIndex = 0;
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
                AppFrameStep step;
                lock (stateLock)
                {
                    if (disposed || mode != SchedulerMode.AppRunning || pauseRequested
                        || appStepIndex >= appSequence.Length)
                    {
                        return;
                    }

                    step = appSequence[appStepIndex];

                    // 登记必须早于实际发送：设备回复可能在 Send 返回之前就已到达解析器，
                    // 若在发送之后登记会漏掉这次快速回复，白等到窗口到期。
                    pendingAppReplyIds.Add(step.Id);
                }

                // 实际写入在锁外执行，避免持调度锁申请通道锁。
                try
                {
                    if (step.IsWrite)
                    {
                        var commands = motorCommands
                            ?? throw new InvalidOperationException("应用序列包含 0x15 写入，但未配置 MotorCommandService。");
                        await channel.SendWriteRequestAsync(
                            step.Id,
                            commands.CreateDataSnapshot()).ConfigureAwait(false);
                    }
                    else
                    {
                        await channel.SendReadRequestAsync(step.Id).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // 未发出即失败：撤回本帧登记，避免把一次失败的发送当成在途拖满收尾窗口。
                    lock (stateLock)
                    {
                        RemovePendingReplyLocked(step.Id);
                    }

                    throw;
                }

                lock (stateLock)
                {
                    appStepIndex++;

                    // 暂停请求已在单帧写完后的帧边界生效，后续应用帧不再发送。
                    if (disposed || mode != SchedulerMode.AppRunning || pauseRequested)
                    {
                        return;
                    }

                    // 本拍序列已发完，等待下一次 100 ms 定时器触发。
                    if (appStepIndex >= appSequence.Length)
                    {
                        return;
                    }
                }

                // 相邻应用帧间隔 10 ms，从上一帧串口写入完成起算。
                // 等待期间若收到诊断暂停请求，循环顶部会重新检查，不会多发下一帧。
                await Task.Delay(appInterFrameGap, timeProvider).ConfigureAwait(false);
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

    /// <summary>
    /// 应用回复到达（完整帧 + 校验通过 + ID 反算匹配，由 LinReadReplyParser 保证）：
    /// 只清除待回复登记，不表示电机动作已执行成功。
    /// 运行在接收线程，只做登记清除与信号释放，不得阻塞或 await。
    /// </summary>
    private void HandleAppFrameReceived(LinFrame frame)
    {
        lock (stateLock)
        {
            if (pendingAppReplyIds.Remove(frame.Id))
            {
                CompleteDrainSignalIfDrainedLocked();
            }
        }
    }

    private void RemovePendingReplyLocked(byte id)
    {
        if (pendingAppReplyIds.Remove(id))
        {
            CompleteDrainSignalIfDrainedLocked();
        }
    }

    /// <summary>登记全部清空时提前放行收尾等待（须在调度锁内调用）。</summary>
    private void CompleteDrainSignalIfDrainedLocked()
    {
        if (pendingAppReplyIds.Count > 0)
        {
            return;
        }

        // RunContinuationsAsynchronously：在锁内 TrySetResult 也不会同步执行续体。
        appReplyDrainSignal?.TrySetResult();
        appReplyDrainSignal = null;
    }

    /// <summary>
    /// 强制清空全部登记并释放等待：收尾到期、撤销申请、断线与释放统一走此处
    /// （须在调度锁内调用）。清登记不等于清串口或解析器缓存。
    /// </summary>
    private void ClearAllPendingRepliesLocked()
    {
        pendingAppReplyIds.Clear();
        appReplyDrainSignal?.TrySetResult();
        appReplyDrainSignal = null;
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
                appStepIndex = 0;
                // 新连接不继承旧连接的待回复登记。
                ClearAllPendingRepliesLocked();
                pollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                appTimer.Change(appPeriod, Timeout.InfiniteTimeSpan);
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
                // 旧连接上的应用回复不会再到达，清空登记并释放收尾等待。
                ClearAllPendingRepliesLocked();
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
