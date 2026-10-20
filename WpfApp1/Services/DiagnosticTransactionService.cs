using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 诊断事务协调层（依据 WpfApp1诊断架构讨论记录.md §7.2、§7.3、§11、§15 与
/// 《3C连续帧功能实施计划.md》§4.2～§4.11）：
/// - 同一总线上的诊断串行执行：一个全局 FIFO，多个排队诊断之间不穿插应用帧；
/// - 1～6 字节请求保持原 SF 路径：发送前启用 TP，回显与 3D 轮询并行，行为不变；
/// - 长度 &gt; 6 的请求按 FF + CF 逐片发送并逐片确认回显，发送阶段禁止候选进入响应 TP；
/// - 多帧拆分发送阶段截止（t_send_start + SendBudget）与响应阶段截止（t_last_complete + ResponseBudget）；
/// - 回显与响应在字节层同构，按阶段判定表处理，历史回显表保留到本笔结束；
/// - 请求全部确认后进入固定回显隔离，隔离结束才 Reset＋BeginReceive 并启动 3D；
/// - 失败按 §4.4.1 决定单笔失败或中止整批；跨事务归属门禁只在“可能仍有未归属数据”时关闭；
/// - 断线结束当前业务并清空排队诊断，不重放；应用退出/释放时同样终止等待者。
/// </summary>
public sealed class DiagnosticTransactionService : IDisposable
{
    private enum TransactionPhase : byte
    {
        SendingRequest,
        EchoDrain,
        WaitingResponse
    }

    /// <summary>执行计划：入队前生成的不可变快照，运行期不再读取调用方对象（§4.1、§4.10）。</summary>
    private sealed class ExecutionPlan
    {
        public required byte Nad { get; init; }

        public required byte[] UdsRequest { get; init; }

        public required byte[] ExpectedResponsePrefix { get; init; }

        public required byte[][] Frames { get; init; }

        public required bool IsMultiFrame { get; init; }

        public DiagnosticTiming? SfTiming { get; init; }

        public DiagnosticMultiFrameOptions? MultiFrame { get; init; }

        public int MessageLength => UdsRequest.Length;
    }

    private sealed class PendingTransaction
    {
        // 0 = 排队中，1 = 已开始执行，2 = 已结束（取消/被清空）。
        private int state;

        public PendingTransaction(
            ExecutionPlan plan,
            long connectionGeneration,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            ConnectionGeneration = connectionGeneration;
            CancellationToken = cancellationToken;
        }

        public ExecutionPlan Plan { get; }

        /// <summary>入队时的连接代次；执行前校验，跨断线重连的旧请求不再发送。</summary>
        public long ConnectionGeneration { get; }

        public CancellationToken CancellationToken { get; }

        public CancellationTokenRegistration Registration { get; set; }

        public TaskCompletionSource<DiagnosticResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFinished => Volatile.Read(ref state) == 2;

        /// <summary>出队时竞争“开始执行”；若已被取消回调置为结束则返回 false。</summary>
        public bool TryBeginRun() => Interlocked.CompareExchange(ref state, 1, 0) == 0;

        /// <summary>排队期间取消/被整批清空：只允许从“排队中”迁移到“结束”。</summary>
        public bool TryAbort() => Interlocked.CompareExchange(ref state, 2, 0) == 0;

        /// <summary>
        /// 等待诊断独占期间被取消/断线/释放：只允许从“已出队执行”迁移到“结束”。
        /// 与 <see cref="TryAbort"/> 一起构成排队态与进入态各自的单次完成门禁，
        /// 已交接给 active 的请求由 CompleteLocked 负责，三者不重叠（F5 §7.5.4-2）。
        /// </summary>
        public bool TryFinishEntering() => Interlocked.CompareExchange(ref state, 2, 1) == 1;

        public void DisposeRegistration()
        {
            var registration = Registration;
            Registration = default;
            registration.Dispose();
        }
    }

    private sealed class ActiveTransaction
    {
        public ActiveTransaction(ExecutionPlan plan)
        {
            Plan = plan;
            Frames = plan.Frames;
            EchoRaws = new byte[Frames.Length][];
            for (var index = 0; index < Frames.Length; index++)
            {
                var pdu = Frames[index];
                var echo = new byte[pdu.Length + 1];
                pdu.CopyTo(echo, 0);
                echo[^1] = LinFrameCodec.CalculateClassicChecksum(pdu);
                EchoRaws[index] = echo;
            }

            WriteAttempted = new bool[Frames.Length];
            EchoConfirmed = new bool[Frames.Length];
        }

        public ExecutionPlan Plan { get; }

        public byte[][] Frames { get; }

        /// <summary>全部已发送分片的原始回显（8 字节 PDU + 经典校验），保留到本笔结束。</summary>
        public byte[][] EchoRaws { get; }

        public bool[] WriteAttempted { get; }

        public bool[] EchoConfirmed { get; }

        public TransactionPhase Phase { get; set; } = TransactionPhase.SendingRequest;

        /// <summary>当前进度阶段（§4.9.1），与 <see cref="Phase"/> 同步更新。</summary>
        public DiagnosticPhase CurrentPhase { get; set; } = DiagnosticPhase.EnteringDiagnostic;

        public int CurrentIndex { get; set; } = -1;

        public long EchoDeadline { get; set; }

        public long SendDeadline { get; set; }

        public long ResponseDeadline { get; set; }

        public long DrainEnd { get; set; }

        public long EchoFilterEnd { get; set; }

        public long LastWriteComplete { get; set; }

        public long LastEchoComplete { get; set; }

        public bool TpReceiveEnabled { get; set; }

        /// <summary>
        /// 本笔是否已取得至少一次 3D 写入成功回执（由调度器真实发送路径通知，F2）。
        /// 多帧响应认领以此为准，TpReceiveEnabled 只表示“已准备好重组”。
        /// </summary>
        public bool FirstReadCommitted { get; set; }

        /// <summary>当前有一次 3D 写入正在提交（已开始、尚未取得回执）；此期间的候选有界暂存。</summary>
        public bool ReadAttemptPending { get; set; }

        /// <summary>首次读取提交前有界暂存的响应候选，保留原始接收时间（F2 §3.3）。</summary>
        public List<DiagnosticPacket>? PendingReadCandidates { get; set; }

        /// <summary>拥有本事务的排队请求；结果冻结后立即完成其等待（F3：结果与排空分离）。</summary>
        public PendingTransaction? Owner { get; set; }

        public bool EchoVerified { get; set; }

        public bool ResponseVerified { get; set; }

        public byte[]? VerifiedResponse { get; set; }

        public string? TpAbandonReason { get; set; }

        public int WriteAttemptCount { get; set; }

        public int SerialWriteCompletedCount { get; set; }

        public int EchoConfirmedCount { get; set; }

        public int UdsBytesWriteCompleted { get; set; }

        /// <summary>最终结果确定时是否仍有未取得明确结果的写入。</summary>
        public bool WriteInFlight { get; set; }

        public DiagnosticPhase? TerminalPhase { get; set; }

        public bool Completed { get; set; }

        public DiagnosticResult? PublishedResult { get; set; }

        /// <summary>当前分片的回显等待信号；回显到达或事务结束时释放。</summary>
        public TaskCompletionSource<bool> EchoAwaiter { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>事务结束信号：用于让正在等待写入/回显的发送循环立即退出。</summary>
        public TaskCompletionSource<bool> Lifecycle { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DiagnosticResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly LinFrameChannel channel;
    private readonly LinBusScheduler scheduler;
    private readonly LinTransportProtocol transportProtocol;
    private readonly TimeProvider timeProvider;
    private readonly object stateLock = new();
    private readonly Queue<PendingTransaction> queue = new();
    private readonly List<CancellationTokenRegistration> deferredRegistrationDisposals = [];
    private readonly ITimer inspectTimer;

    private ActiveTransaction? active;

    /// <summary>
    /// 已出队、尚未交接给 <see cref="active"/> 的请求（正在等待诊断独占）。
    /// 只在 stateLock 内读写：断线、释放与取消据此找到“既不在 queue 也不在 active”的请求，
    /// 不必等旧应用帧写入结束即可完成结果（F5 §7.5.4）。
    /// </summary>
    private PendingTransaction? enteringPending;
    private Task? enterModeTask;
    private bool loopActive;
    private long loopGeneration;
    private long connectionGeneration;
    private bool isConnected;
    private bool disposed;

    /// <summary>整批中止后的收尾期；期间新提交返回 QueueAborted（§4.4.1）。</summary>
    private bool cleanupPending;

    /// <summary>
    /// 跨事务归属门禁；只在"确有证据表明旧回复可能仍在途"时关闭（归属异常、响应阶段取消、
    /// 带在途请求断线），单纯超时不关闭。只在确认恢复后解除（§4.4.2）。
    /// </summary>
    private bool diagnosticGateClosed;

    /// <summary>
    /// 首个 3D 写入在途期间允许暂存的候选上限。数值在阶段 0 按中间件单次反馈契约定稿；
    /// 溢出不得无界扩容或静默成功，按归属异常终止（审查方案 §3.3-4）。
    /// </summary>
    private const int MaxPendingReadCandidates = 32;

    public DiagnosticTransactionService(
        LinFrameChannel channel,
        LinBusScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        transportProtocol = new LinTransportProtocol(this.timeProvider);
        isConnected = scheduler.IsConnected;
        inspectTimer = this.timeProvider.CreateTimer(
            InspectTimerCallback,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        channel.DiagnosticPacketReceived += HandleDiagnosticPacket;
        channel.ConnectionStateChanged += HandleConnectionStateChanged;
        // 真实 3D 提交上下文：开始提交 → 有界暂存；成功回执 → 开放响应认领并重放（F2）。
        scheduler.DiagnosticReadAttemptStarting += HandleDiagnosticReadAttemptStarting;
        scheduler.DiagnosticReadCommitted += HandleDiagnosticReadCommitted;
        scheduler.DiagnosticReadAttemptFailed += HandleDiagnosticReadAttemptFailed;
    }

    /// <summary>
    /// 诊断活动状态变化：入队（true）到队列清空（false）之间，界面禁用电机按钮并保留 0x33 显示。
    /// 排队等待也算诊断进行中（§6）。
    /// </summary>
    public event Action<bool>? DiagnosticActivityChanged;

    /// <summary>跨事务归属门禁是否已关闭；关闭期间新诊断返回 DiagnosticGateClosed（§4.4.2）。</summary>
    public bool IsDiagnosticBlocked
    {
        get
        {
            lock (stateLock)
            {
                return diagnosticGateClosed;
            }
        }
    }

    /// <summary>
    /// 显式解除跨事务门禁。本阶段不接界面、不自动调用；应在人工确认设备已复位后使用。
    /// 串口重连不会自动解除门禁（§4.4.2）。
    /// </summary>
    public void ResetDiagnosticGate()
    {
        lock (stateLock)
        {
            diagnosticGateClosed = false;
        }
    }

    /// <summary>
    /// 提交一笔诊断业务；按提交顺序排队执行，等待期间不发送应用帧。
    /// 取消统一返回 <see cref="DiagnosticFailureReason.Cancelled"/> 失败结果，不抛 TaskCanceledException（§4.11）。
    /// </summary>
    public Task<DiagnosticResult> ExecuteAsync(
        DiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(DiagnosticResult.Failed(
                DiagnosticFailureReason.Cancelled,
                null,
                new DiagnosticProgress { FailedPhase = DiagnosticPhase.Validating }));
        }

        if (!TryCreatePlan(request, out var plan, out var validationError))
        {
            return Task.FromResult(DiagnosticResult.Failed(
                DiagnosticFailureReason.InvalidRequest,
                validationError,
                new DiagnosticProgress { FailedPhase = DiagnosticPhase.Validating }));
        }

        long generation;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (diagnosticGateClosed)
            {
                return Task.FromResult(DiagnosticResult.Failed(
                    DiagnosticFailureReason.DiagnosticGateClosed,
                    "诊断已锁止，需复位或重启",
                    new DiagnosticProgress
                    {
                        TotalPduCount = plan.Frames.Length,
                        FailedPhase = DiagnosticPhase.Validating
                    }));
            }

            if (cleanupPending)
            {
                return Task.FromResult(DiagnosticResult.Failed(
                    DiagnosticFailureReason.QueueAborted,
                    null,
                    new DiagnosticProgress
                    {
                        TotalPduCount = plan.Frames.Length,
                        FailedPhase = DiagnosticPhase.Queued
                    }));
            }

            generation = connectionGeneration;
        }

        var pending = new PendingTransaction(plan, generation, cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            // 取消回调不申请 stateLock 去处理排队态：排队取消只需把状态从“排队中”推进到“结束”，
            // 立即完成等待者，随后由队列循环跳过并释放注册（§4.11）。
            // 已出队、正在等待独占的请求改由协调层按 entering 归属完成（F5 §7.5.4-3）。
            pending.Registration = cancellationToken.Register(
                static state =>
                {
                    var (service, item) = ((DiagnosticTransactionService, PendingTransaction))state!;
                    if (item.TryAbort())
                    {
                        item.Completion.TrySetResult(DiagnosticResult.Failed(
                            DiagnosticFailureReason.Cancelled,
                            null,
                            new DiagnosticProgress
                            {
                                TotalPduCount = item.Plan.Frames.Length,
                                FailedPhase = DiagnosticPhase.Queued
                            }));
                        return;
                    }

                    // 已出队、尚未交接给 active：立即结束等待，不等应用帧写入完成。
                    service.CancelEnteringRequest(item);
                },
                (this, pending));
        }

        var raiseActive = false;
        var alreadyCancelled = false;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // 注册期间即被取消：不入队，直接返回已完成的失败结果。
            if (pending.IsFinished)
            {
                alreadyCancelled = true;
            }
            else
            {
                var becomeActive = queue.Count == 0 && active is null;
                queue.Enqueue(pending);

                if (becomeActive)
                {
                    // 首笔请求立即申请暂停应用发送，其耗时不计入业务计时（§5）。
                    enterModeTask ??= scheduler.EnterDiagnosticModeAsync();
                    raiseActive = true;
                }

                if (!loopActive)
                {
                    loopActive = true;
                    _ = ProcessQueueAsync();
                }
            }
        }

        if (alreadyCancelled)
        {
            pending.DisposeRegistration();
            return pending.Completion.Task;
        }

        if (raiseActive)
        {
            DiagnosticActivityChanged?.Invoke(true);
        }

        return pending.Completion.Task;
    }

    public void Dispose()
    {
        List<PendingTransaction> drained = [];
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            // 先结束“已出队、等待独占”的请求：释放不得依赖旧应用写入排空（F5 §7.5.5）。
            if (enteringPending is { } entering)
            {
                CompleteEnteringLocked(entering, DiagnosticFailureReason.Shutdown);
            }

            // 释放后不会再有等待者：立即失效未完成的独占申请，
            // 避免迟到的 EnterDiagnosticModeAsync 把调度器切入诊断独占。
            if (enterModeTask is { IsCompleted: false })
            {
                scheduler.CancelPendingEnterRequest();
            }

            if (active is { Completed: false } current)
            {
                CompleteLocked(current, DiagnosticFailureReason.Shutdown);
            }

            while (queue.Count > 0)
            {
                var pending = queue.Dequeue();
                if (pending.TryAbort())
                {
                    pending.Completion.TrySetResult(DiagnosticResult.Failed(
                        DiagnosticFailureReason.Shutdown,
                        null,
                        new DiagnosticProgress
                        {
                            TotalPduCount = pending.Plan.Frames.Length,
                            FailedPhase = DiagnosticPhase.Queued
                        }));
                    drained.Add(pending);
                }
            }

            deferredRegistrationDisposals.AddRange(drained.Select(item => item.Registration));
        }

        FlushDeferredRegistrations();
        inspectTimer.Dispose();
        channel.DiagnosticPacketReceived -= HandleDiagnosticPacket;
        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
        scheduler.DiagnosticReadAttemptStarting -= HandleDiagnosticReadAttemptStarting;
        scheduler.DiagnosticReadCommitted -= HandleDiagnosticReadCommitted;
        scheduler.DiagnosticReadAttemptFailed -= HandleDiagnosticReadAttemptFailed;
    }

    /// <summary>
    /// 计算多帧发送阶段预算（§4.3）：N × (WriteBudget + EchoBudget) + (N-1) × InterFrameGapBudget + SchedulingMargin。
    /// 仅给出预算构造方式，具体数值由协议确认。
    /// </summary>
    public static TimeSpan CalculateSendBudget(int pduCount, DiagnosticSendTiming send)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pduCount);

        var seconds = pduCount * (send.WriteBudget + send.EchoBudget).TotalSeconds
            + (pduCount - 1) * send.InterFrameGapBudget.TotalSeconds
            + send.SchedulingMargin.TotalSeconds;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(send), "发送预算超出可表示范围。");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task ProcessQueueAsync()
    {
        long myGeneration;
        lock (stateLock)
        {
            myGeneration = ++loopGeneration;
        }

        try
        {
            while (true)
            {
                PendingTransaction pending;
                Task? enterTask;
                lock (stateLock)
                {
                    // 先丢弃排队期间已取消的请求（出队跳过，不打断前序业务）。
                    while (queue.Count > 0 && queue.Peek().IsFinished)
                    {
                        deferredRegistrationDisposals.Add(queue.Dequeue().Registration);
                    }

                    if (disposed || queue.Count == 0)
                    {
                        break;
                    }

                    pending = queue.Dequeue();

                    // 出队与取消回调竞争“开始执行”；已取消则直接跳过。
                    if (!pending.TryBeginRun())
                    {
                        deferredRegistrationDisposals.Add(pending.Registration);
                        continue;
                    }

                    // 跨断线重连的旧请求：不进入独占等待，直接判开，不重放 3C。
                    if (pending.ConnectionGeneration != connectionGeneration)
                    {
                        CompleteEnteringLocked(pending, DiagnosticFailureReason.Disconnected);
                        deferredRegistrationDisposals.Add(pending.Registration);
                        continue;
                    }

                    // 出队、取得执行资格与登记进入态在同一临界区内完成：
                    // 此后断线/释放/取消都能通过 enteringPending 找到该请求，
                    // 不再存在“只存在于循环局部变量”的不可见窗口（F5 §7.5.4-1）。
                    enteringPending = pending;
                    enterTask = enterModeTask;
                }

                FlushDeferredRegistrations();

                // 等待独占：同时观察本笔终止信号，取消/断线/释放立即唤醒，
                // 不必等旧应用帧写入结束（F4/F5）。
                var entered = enterTask is null
                    || await WaitForExclusiveEntryAsync(enterTask, pending).ConfigureAwait(false);

                ActiveTransaction? transaction = null;
                if (entered)
                {
                    lock (stateLock)
                    {
                        // 等待结束后在同一临界区复核全部条件，并原子完成
                        // “清除 entering → 建立 active → 绑定 Owner”的交接；
                        // 不能先清除再放锁后建 active，否则重新打开同类窗口（F5 §7.5.4-5、-6）。
                        if (ReferenceEquals(enteringPending, pending) && !pending.IsFinished)
                        {
                            if (disposed)
                            {
                                CompleteEnteringLocked(pending, DiagnosticFailureReason.Shutdown);
                            }
                            else if (pending.ConnectionGeneration != connectionGeneration)
                            {
                                CompleteEnteringLocked(pending, DiagnosticFailureReason.Disconnected);
                            }
                            else
                            {
                                transaction = new ActiveTransaction(pending.Plan) { Owner = pending };
                                active = transaction;
                                transaction.CurrentPhase = DiagnosticPhase.EnteringDiagnostic;
                                enteringPending = null;
                            }
                        }
                        else if (!pending.IsFinished)
                        {
                            // 不变量兜底：归属已丢且未终结，补一次完成，避免调用者永久等待。
                            CompleteEnteringLocked(pending, DiagnosticFailureReason.QueueAborted);
                        }
                    }
                }

                if (transaction is null)
                {
                    // 本笔已在进入阶段被终结：不再创建 active、不发送任何 3C/3D。
                    // 按有效等待者规则撤销或保留共享申请，并让迟到申请不再切换模式。
                    AbortEnterRequestIfNoWaiters();
                    pending.DisposeRegistration();
                    continue;
                }

                DiagnosticResult result;
                try
                {
                    result = await RunTransactionAsync(pending, transaction).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 进入诊断模式或发送 3C 失败（多为断线）。
                    var reason = isConnected
                        ? DiagnosticFailureReason.RequestWriteFailed
                        : DiagnosticFailureReason.Disconnected;
                    result = DiagnosticResult.Failed(
                        reason,
                        null,
                        new DiagnosticProgress
                        {
                            TotalPduCount = pending.Plan.Frames.Length,
                            FailedPhase = DiagnosticPhase.EnteringDiagnostic
                        });
                    Debug.WriteLine(exception);
                }

                // 进入申请本身失败（断线/释放）时也要释放进入态归属，避免遗留指向已结束请求的引用
                //（F5 §7.5.4-4）。正常交接已在临界区内清除。
                lock (stateLock)
                {
                    if (ReferenceEquals(enteringPending, pending))
                    {
                        enteringPending = null;
                    }
                }

                pending.Completion.TrySetResult(result);
                pending.DisposeRegistration();
            }
        }
        finally
        {
            // 只有仍是"当前"循环时才清理共享状态并恢复应用发送；
            // 若收尾期间新请求入队并启动了新循环（代次已推进），旧循环不得清掉新事务。
            var isCurrentLoop = false;
            lock (stateLock)
            {
                if (loopGeneration == myGeneration)
                {
                    isCurrentLoop = true;
                    loopActive = false;

                    // 兜底：循环退出时不应再有未交接的 entering 请求；若因异常路径遗留，
                    // 必须完成它，避免调用者永久等待（F5 §7.5.4-2）。
                    if (enteringPending is { } leftover)
                    {
                        CompleteEnteringLocked(
                            leftover,
                            disposed
                                ? DiagnosticFailureReason.Shutdown
                                : DiagnosticFailureReason.QueueAborted);
                        deferredRegistrationDisposals.Add(leftover.Registration);
                    }

                    if (enterModeTask is { IsCompleted: false })
                    {
                        // 已无任何等待者的独占申请：撤销，防止迟到的模式切换把应用通信停在独占态（F4/R10）。
                        scheduler.CancelPendingEnterRequest();
                    }

                    ObserveAbandonedEnterTask(enterModeTask);
                    enterModeTask = null;
                    active = null;
                    cleanupPending = false;
                }
            }

            FlushDeferredRegistrations();

            if (isCurrentLoop)
            {
                try
                {
                    // 队列清空后恢复应用发送；断线时调度器不会恢复应用（§11.5）。
                    await scheduler.ExitDiagnosticModeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }

                bool stillIdle;
                lock (stateLock)
                {
                    // 收尾期间又有新诊断入队时，不发出"诊断结束"，避免界面提前解禁电机按钮。
                    stillIdle = queue.Count == 0 && active is null && !loopActive;
                }

                if (stillIdle)
                {
                    DiagnosticActivityChanged?.Invoke(false);
                }
            }
        }
    }

    /// <summary>
    /// 等待本批次的诊断独占申请；除申请完成外还观察本笔的终止信号，
    /// 调用者取消、断线、服务释放都能立即唤醒，不需要等旧应用帧写入结束（F4/F5）。
    /// 返回 false 表示等待期间已被终结，调用方不得创建 active 或发送任何帧（F5 §7.5.4-3、-4）。
    /// </summary>
    private static async Task<bool> WaitForExclusiveEntryAsync(Task enterTask, PendingTransaction pending)
    {
        if (pending.IsFinished)
        {
            return false;
        }

        if (!enterTask.IsCompleted)
        {
            // Completion 由进入阶段的统一完成入口写入；这里不额外引入轮询定时器。
            var completed = await Task.WhenAny(enterTask, pending.Completion.Task).ConfigureAwait(false);
            if (!ReferenceEquals(completed, enterTask) || pending.IsFinished)
            {
                // 终止先胜出：不 await enterTask，其迟到异常由撤销/收尾路径观察。
                return false;
            }
        }

        // 申请自身失败（断线、释放）按既有规则上抛，由调用方 catch 归类。
        await enterTask.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 进入阶段（已出队、尚未交接给 active）的唯一终止入口：冻结原因与零发送进度并完成等待者。
    /// 只能在 stateLock 内调用；已交接给 active 的请求由 CompleteLocked 负责，二者不得双写
    /// （F5 §7.5.4-2）。进入阶段没有真实发送，因此不触发整批中止，也不关闭跨事务门禁。
    /// </summary>
    private bool CompleteEnteringLocked(PendingTransaction pending, DiagnosticFailureReason reason)
    {
        if (ReferenceEquals(enteringPending, pending))
        {
            enteringPending = null;
        }

        // 单次完成门禁：已由其它路径终结时保留首个结果，不事后覆盖（F5 §7.5.5）。
        if (!pending.TryFinishEntering())
        {
            return false;
        }

        pending.Completion.TrySetResult(DiagnosticResult.Failed(
            reason,
            null,
            new DiagnosticProgress
            {
                TotalPduCount = pending.Plan.Frames.Length,
                FailedPhase = DiagnosticPhase.EnteringDiagnostic
            }));
        return true;
    }

    /// <summary>
    /// 调用者取消的进入阶段入口：仅当该请求仍处于“等待独占”时终结它。
    /// 已交接给 active 的请求由运行期取消注册负责，此处不做任何事。
    /// 结果竞争按“已释放 &gt; 连接失效 &gt; 调用者取消”的优先级判定（F5 §7.5.5）。
    /// </summary>
    private void CancelEnteringRequest(PendingTransaction pending)
    {
        lock (stateLock)
        {
            if (!ReferenceEquals(enteringPending, pending) || pending.IsFinished)
            {
                return;
            }

            var reason = disposed
                ? DiagnosticFailureReason.Shutdown
                : pending.ConnectionGeneration != connectionGeneration
                    ? DiagnosticFailureReason.Disconnected
                    : DiagnosticFailureReason.Cancelled;

            CompleteEnteringLocked(pending, reason);
        }
    }

    /// <summary>
    /// 撤销仍在等待的独占申请；仅当队列中已无有效等待者时执行（F4 §5.2 第 5～6 条）。
    /// 仍有等待者时保留申请，由后续请求继续复用。
    /// </summary>
    private void AbortEnterRequestIfNoWaiters()
    {
        lock (stateLock)
        {
            if (HasLiveWaiterLocked())
            {
                return;
            }

            if (enterModeTask is { IsCompleted: false })
            {
                scheduler.CancelPendingEnterRequest();
            }

            // 丢弃共享申请前观察其迟到异常：撤销后可能因断线/释放失败，
            // 未观察的 Task 异常会变成进程级 UnobservedTaskException（F4 §5.2-9）。
            ObserveAbandonedEnterTask(enterModeTask);
            enterModeTask = null;
        }
    }

    private static void ObserveAbandonedEnterTask(Task? task)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool HasLiveWaiterLocked()
    {
        foreach (var item in queue)
        {
            if (!item.IsFinished)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 运行已合法交接的事务。<paramref name="transaction"/> 由队列循环在 stateLock 内
    /// “清除 entering → 建立 active”时创建，本方法不再自行重建 active（F5 §7.5.4-7）。
    /// </summary>
    private async Task<DiagnosticResult> RunTransactionAsync(
        PendingTransaction pending,
        ActiveTransaction transaction)
    {
        using var cancellationRegistration = RegisterRuntimeCancellation(transaction, pending.CancellationToken);

        // 交接与运行期取消登记之间的窄窗口：已终止（释放/断线/取消）时不得再发送任何帧
        //（F5 §7.5.4-8）。取消本身由上面的注册同步补齐为 Cancelled。
        lock (stateLock)
        {
            if (transaction.Completed)
            {
                return transaction.PublishedResult!;
            }
        }

        DiagnosticResult? immediate;
        try
        {
            immediate = pending.Plan.IsMultiFrame
                ? await SendMultiFrameRequestAsync(transaction, pending.CancellationToken).ConfigureAwait(false)
                : await SendSingleFrameRequestAsync(transaction, pending.CancellationToken).ConfigureAwait(false);

            if (immediate is not null)
            {
                return immediate;
            }

            return await transaction.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Complete(transaction, DiagnosticFailureReason.Cancelled);
        }
        catch (Exception exception)
        {
            // 协调层内部异常（非取消、非写入失败）也必须有唯一结果，不能把业务留在未完成状态。
            Debug.WriteLine(exception);
            return Complete(
                transaction,
                isConnected
                    ? DiagnosticFailureReason.RequestWriteFailed
                    : DiagnosticFailureReason.Disconnected);
        }
        finally
        {
            // 统一收尾：不再提交新的 3D → 清理 TP 与计时 → 等待在途写入完成。
            scheduler.StopResponsePolling();
            SetInspectTimer(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            transportProtocol.Reset();
            await scheduler.WaitForSendIdleAsync().ConfigureAwait(false);

            lock (stateLock)
            {
                transaction.WriteInFlight = false;
                if (ReferenceEquals(active, transaction))
                {
                    active = null;
                }
            }
        }
    }

    /// <summary>SF 路径：保持“发送前启用 TP、回显与 3D 并行”的既有兼容行为（§4.3 末段）。</summary>
    private async Task<DiagnosticResult?> SendSingleFrameRequestAsync(
        ActiveTransaction transaction,
        CancellationToken cancellationToken)
    {
        var plan = transaction.Plan;
        var timing = plan.SfTiming!;
        var pdu = transaction.Frames[0];

        cancellationToken.ThrowIfCancellationRequested();

        lock (stateLock)
        {
            transaction.Phase = TransactionPhase.SendingRequest;
            transaction.CurrentPhase = DiagnosticPhase.SendingRequest;
            transaction.CurrentIndex = 0;
            transaction.WriteAttempted[0] = true;
            transaction.WriteAttemptCount = 1;
            transaction.WriteInFlight = true;
        }

        // 先登记临时截止再发送：回显可能早于写入任务返回（§4.2、§5）。
        // 未初始化的 0 截止会让快速回显被误判为超时，因此发送前必须给出有效值。
        var submitTime = timeProvider.GetTimestamp();
        lock (stateLock)
        {
            transaction.EchoDeadline = submitTime + ToTimestampUnits(timing.EchoWaitTimeout);
            transaction.ResponseDeadline = submitTime + ToTimestampUnits(timing.TotalDeadline);
        }

        // SF 保留原有行为：发送前启用接收上下文，回显与 3D 轮询并行。
        transportProtocol.BeginReceive(plan.Nad, timing.ConsecutiveFrameTimeout);

        FrameWriteReceipt receipt;
        try
        {
            receipt = await scheduler.SendDiagnosticRequestAsync(pdu, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Complete(transaction, DiagnosticFailureReason.Cancelled);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            lock (stateLock)
            {
                transaction.WriteInFlight = false;
            }

            return Complete(
                transaction,
                isConnected
                    ? DiagnosticFailureReason.RequestWriteFailed
                    : DiagnosticFailureReason.Disconnected);
        }

        var t0 = receipt.CompletedTimestamp;
        lock (stateLock)
        {
            transaction.WriteInFlight = false;
            transaction.SerialWriteCompletedCount = 1;
            transaction.UdsBytesWriteCompleted = plan.MessageLength;
            transaction.LastWriteComplete = t0;
            transaction.Phase = TransactionPhase.WaitingResponse;
            transaction.CurrentPhase = DiagnosticPhase.WaitingResponse;
            transaction.EchoDeadline = t0 + ToTimestampUnits(timing.EchoWaitTimeout);
            transaction.ResponseDeadline = t0 + ToTimestampUnits(timing.TotalDeadline);

            // 回显与响应都可能在写入任务返回前到达并已结束本笔；不得再启动轮询。
            if (transaction.Completed)
            {
                return transaction.PublishedResult;
            }
        }

        // SF：首拍沿用 ResponsePollPeriod，与既有行为一致。
        scheduler.StartResponsePolling(
            t0,
            timing.ResponsePollPeriod,
            timing.ResponsePollPeriod,
            transaction.ResponseDeadline,
            transaction);
        SetInspectTimer(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        return null;
    }

    /// <summary>多帧路径：逐片发送、逐片确认回显，隔离后才启用响应 TP（§4.2、§4.5、§4.7）。</summary>
    private async Task<DiagnosticResult?> SendMultiFrameRequestAsync(
        ActiveTransaction transaction,
        CancellationToken cancellationToken)
    {
        var plan = transaction.Plan;
        var options = plan.MultiFrame!;
        var send = options.Send;
        var response = options.Response;
        var frameCount = transaction.Frames.Length;

        var sendBudget = options.Send.SendBudgetOverride ?? CalculateSendBudget(frameCount, send);
        var sendStart = timeProvider.GetTimestamp();
        var sendDeadline = sendStart + ToTimestampUnits(sendBudget);

        lock (stateLock)
        {
            transaction.Phase = TransactionPhase.SendingRequest;
            transaction.CurrentPhase = DiagnosticPhase.SendingRequest;
            transaction.SendDeadline = sendDeadline;
        }

        for (var index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (transaction.Completed)
            {
                // 截止或取消已由定时器/接收路径判定，发送循环立即退出。
                return transaction.PublishedResult;
            }

            if (index > 0)
            {
                var gapFailure = await WaitInterFrameGapAsync(transaction, send, sendDeadline, cancellationToken)
                    .ConfigureAwait(false);
                if (gapFailure is not null)
                {
                    return gapFailure;
                }
            }

            var submitTime = timeProvider.GetTimestamp();
            if (submitTime >= sendDeadline)
            {
                return Complete(transaction, DiagnosticFailureReason.SendDeadlineExceeded);
            }

            var echoDeadline = submitTime + ToTimestampUnits(send.EchoBudget);
            var echoAwaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (stateLock)
            {
                transaction.CurrentIndex = index;
                transaction.WriteAttempted[index] = true;
                transaction.WriteAttemptCount++;
                transaction.WriteInFlight = true;
                transaction.EchoDeadline = echoDeadline;
                transaction.EchoAwaiter = echoAwaiter;
            }

            var sendTask = scheduler.SendDiagnosticRequestAsync(transaction.Frames[index], cancellationToken);
            var writeDeadline = Math.Min(
                submitTime + ToTimestampUnits(send.WriteBudget),
                sendDeadline);
            var writeOutcome = await WaitForSignalAsync(
                sendTask,
                writeDeadline,
                cancellationToken,
                transaction.Lifecycle.Task).ConfigureAwait(false);

            if (writeOutcome == WaitOutcome.Aborted)
            {
                return transaction.PublishedResult ?? Complete(
                    transaction,
                    DiagnosticFailureReason.SendDeadlineExceeded);
            }

            if (writeOutcome != WaitOutcome.Signaled)
            {
                var now = timeProvider.GetTimestamp();
                // 在途写入未取得明确结果：保留 WriteInFlight 供进度快照标注未知状态。
                return Complete(
                    transaction,
                    now >= sendDeadline
                        ? DiagnosticFailureReason.SendDeadlineExceeded
                        : DiagnosticFailureReason.RequestWriteTimeout);
            }

            FrameWriteReceipt receipt;
            try
            {
                receipt = await sendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Complete(transaction, DiagnosticFailureReason.Cancelled);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                lock (stateLock)
                {
                    transaction.WriteInFlight = false;
                }

                return Complete(
                    transaction,
                    isConnected
                        ? DiagnosticFailureReason.RequestWriteFailed
                        : DiagnosticFailureReason.Disconnected);
            }

            lock (stateLock)
            {
                transaction.WriteInFlight = false;
                transaction.SerialWriteCompletedCount++;
                transaction.UdsBytesWriteCompleted += GetPayloadBytes(plan, index);
                transaction.LastWriteComplete = receipt.CompletedTimestamp;
            }

            if (transaction.EchoConfirmed[index])
            {
                // 回显早于写入任务返回：已由接收路径记录，不重复等待。
                continue;
            }

            // 等待本片有效回显；同时受发送阶段总预算约束。
            var echoBoundary = Math.Min(echoDeadline, sendDeadline);
            var echoOutcome = await WaitForSignalAsync(
                echoAwaiter.Task,
                echoBoundary,
                cancellationToken,
                transaction.Lifecycle.Task).ConfigureAwait(false);

            if (echoOutcome == WaitOutcome.Aborted)
            {
                return transaction.PublishedResult ?? Complete(
                    transaction,
                    DiagnosticFailureReason.SendDeadlineExceeded);
            }

            lock (stateLock)
            {
                if (transaction.Completed)
                {
                    return transaction.PublishedResult;
                }
            }

            if (echoOutcome != WaitOutcome.Signaled)
            {
                var now = timeProvider.GetTimestamp();
                return Complete(
                    transaction,
                    now >= sendDeadline
                        ? DiagnosticFailureReason.SendDeadlineExceeded
                        : DiagnosticFailureReason.EchoTimeout);
            }
        }

        // 全部请求分片写入并确认：确定 t_last_complete 与两个截止（§4.3）。
        long lastComplete;
        lock (stateLock)
        {
            lastComplete = Math.Max(transaction.LastWriteComplete, transaction.LastEchoComplete);
            transaction.LastWriteComplete = lastComplete;
            transaction.DrainEnd = lastComplete + ToTimestampUnits(send.EchoDrainDuration);
            transaction.ResponseDeadline = lastComplete + ToTimestampUnits(response.ResponseBudget);
            transaction.EchoFilterEnd = lastComplete + ToTimestampUnits(send.EchoToleranceAfterLastComplete);
            transaction.Phase = TransactionPhase.EchoDrain;
            transaction.CurrentPhase = DiagnosticPhase.EchoDrain;
        }

        // 固定回显隔离：停止新 3C、尚未提交 3D，不被新回显延长（§4.5）。
        await DelayUntilAsync(transaction.DrainEnd, cancellationToken, transaction.Lifecycle.Task)
            .ConfigureAwait(false);

        lock (stateLock)
        {
            if (transaction.Completed)
            {
                return transaction.PublishedResult;
            }
        }

        if (timeProvider.GetTimestamp() >= transaction.ResponseDeadline)
        {
            return Complete(transaction, DiagnosticFailureReason.ResponseDeadlineExceeded);
        }

        // 阶段切换顺序（§4.7）：清理解析器残留 → Reset＋BeginReceive → 原子发布 WaitingResponse → 启动 3D。
        channel.DiscardPartialReceiveBuffer("诊断阶段切换丢弃发送阶段残留候选");
        transportProtocol.Reset();
        transportProtocol.BeginReceive(plan.Nad, response.ConsecutiveFrameTimeout);

        long firstPollTarget;
        lock (stateLock)
        {
            transaction.Phase = TransactionPhase.WaitingResponse;
            transaction.CurrentPhase = DiagnosticPhase.WaitingResponse;
            transaction.TpReceiveEnabled = true;
            firstPollTarget = Math.Max(
                transaction.DrainEnd,
                lastComplete + ToTimestampUnits(response.FirstResponsePollDelay));
        }

        var firstDelay = timeProvider.GetElapsedTime(lastComplete, firstPollTarget);
        if (firstDelay < TimeSpan.Zero)
        {
            firstDelay = TimeSpan.Zero;
        }

        scheduler.StartResponsePolling(
            lastComplete,
            firstDelay,
            response.ResponsePollPeriod,
            transaction.ResponseDeadline,
            transaction);
        SetInspectTimer(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        return null;
    }

    /// <summary>相邻分片间隔：既不能小于最小间隔，也不能超过最大允许间隔（不得集中补发）。</summary>
    private async Task<DiagnosticResult?> WaitInterFrameGapAsync(
        ActiveTransaction transaction,
        DiagnosticSendTiming send,
        long sendDeadline,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetTimestamp();
        var gap = timeProvider.GetElapsedTime(transaction.LastWriteComplete, now);
        if (gap > send.InterFrameGapBudget)
        {
            return Complete(transaction, DiagnosticFailureReason.InterFrameDeadlineExceeded);
        }

        var wait = send.MinimumInterFrameGap - gap;
        if (wait > TimeSpan.Zero)
        {
            await DelayAsync(wait, cancellationToken, transaction.Lifecycle.Task).ConfigureAwait(false);
        }

        if (transaction.Completed)
        {
            return transaction.PublishedResult;
        }

        now = timeProvider.GetTimestamp();
        if (now >= sendDeadline)
        {
            return Complete(transaction, DiagnosticFailureReason.SendDeadlineExceeded);
        }

        if (timeProvider.GetElapsedTime(transaction.LastWriteComplete, now) > send.InterFrameGapBudget)
        {
            return Complete(transaction, DiagnosticFailureReason.InterFrameDeadlineExceeded);
        }

        return null;
    }

    private void HandleDiagnosticPacket(DiagnosticPacket packet)
    {
        lock (stateLock)
        {
            var transaction = active;
            if (transaction is null || transaction.Completed)
            {
                // 无活动业务或业务已完成：诊断候选仍由帧通道记入报文窗口，此处不认领。
                return;
            }

            if (transaction.Plan.IsMultiFrame)
            {
                HandleMultiFramePacketLocked(transaction, packet);
                return;
            }

            HandleSingleFramePacketLocked(transaction, packet);
        }
    }

    /// <summary>SF 路径保持原有判定：总截止 → 回显 → 其余候选交给响应 TP。</summary>
    private void HandleSingleFramePacketLocked(ActiveTransaction transaction, DiagnosticPacket packet)
    {
        // 接收路径的硬截止检查：不依赖 10 ms 检查定时器，
        // 超过总截止/回显截止的数据即使完整也不判成功（§15）。
        var now = packet.ReceivedTimestamp;
        if (now >= transaction.ResponseDeadline)
        {
            CompleteLocked(
                transaction,
                DiagnosticFailureReason.DeadlineExceeded);
            return;
        }

        if (packet.Raw.AsSpan().SequenceEqual(transaction.EchoRaws[0]))
        {
            // 回显须在其截止窗口内到达才算有效（§7.3）。
            if (now >= transaction.EchoDeadline)
            {
                CompleteLocked(transaction, DiagnosticFailureReason.EchoTimeout);
                return;
            }

            if (transaction.EchoVerified)
            {
                return;
            }

            // 有效 3C 回显：原始字节与请求 PDU 及经典校验逐字节一致。
            transaction.EchoVerified = true;
            transaction.EchoConfirmedCount = 1;
            transaction.LastEchoComplete = now;
            if (transaction.ResponseVerified)
            {
                CompleteLocked(
                    transaction,
                    DiagnosticFailureReason.None,
                    transaction.VerifiedResponse!);
            }

            return;
        }

        // 其余校验通过的候选按响应分段交给 TP；校验失败与归属不明的候选只由入口记录。
        if (!packet.ChecksumValid)
        {
            return;
        }

        if (transaction.Phase != TransactionPhase.WaitingResponse)
        {
            return;
        }

        ApplyTpResult(transaction, transportProtocol.ProcessPdu(packet.Pdu, packet.ReceivedTimestamp));
    }

    /// <summary>多帧判定表（§4.5）：发送阶段只认回显，隔离期全部丢弃，响应阶段才允许进入 TP。</summary>
    private void HandleMultiFramePacketLocked(ActiveTransaction transaction, DiagnosticPacket packet)
    {
        var now = packet.ReceivedTimestamp;

        switch (transaction.Phase)
        {
            case TransactionPhase.SendingRequest:
                if (now >= transaction.SendDeadline)
                {
                    CompleteLocked(transaction, DiagnosticFailureReason.SendDeadlineExceeded);
                    return;
                }

                // F1：先与“当前待确认片”精确比较。重复载荷（如全 AA）在序号回绕后
                // 会与旧 CF 字节完全相同；若按索引正序查找，真实回显会被已确认的旧片遮挡，
                // 当前片永远等不到回显（审查方案 §2.2）。
                if (!MatchesCurrentUnconfirmedEcho(transaction, packet.Raw))
                {
                    // 不匹配当前片：即使命中历史分片，也只说明是重复/迟到回显，
                    // 不推进当前片、不刷新任何等待（§2.3）。
                    return;
                }

                if (now > transaction.EchoDeadline)
                {
                    CompleteLocked(transaction, DiagnosticFailureReason.EchoTimeout);
                    return;
                }

                transaction.EchoConfirmed[transaction.CurrentIndex] = true;
                transaction.EchoConfirmedCount++;
                transaction.LastEchoComplete = now;
                transaction.EchoAwaiter.TrySetResult(true);
                return;

            case TransactionPhase.EchoDrain:
                // 隔离期任意候选均丢弃，不延长窗口（§4.5）。
                return;

            case TransactionPhase.WaitingResponse:
                if (now >= transaction.ResponseDeadline)
                {
                    CompleteLocked(transaction, DiagnosticFailureReason.ResponseDeadlineExceeded);
                    return;
                }

                if (now < transaction.DrainEnd)
                {
                    // 到达于发送/隔离阶段的候选：即使解析结果此刻才分发，也不属于响应阶段。
                    // 覆盖“解析器已生成、阶段切换后才回调”的在途结果（§3.3-8）。
                    return;
                }

                if (MatchesAnyAttemptedEcho(transaction, packet.Raw))
                {
                    if (now <= transaction.EchoFilterEnd)
                    {
                        // 容忍窗口内的迟到/重复回显：丢弃，不刷新窗口或 N_Cr。
                        return;
                    }

                    // 超出承诺窗口的历史匹配：归属不明，不能当作响应投入 TP。
                    CompleteLocked(transaction, DiagnosticFailureReason.ProtocolAttributionError);
                    return;
                }

                if (!packet.ChecksumValid || !transaction.TpReceiveEnabled)
                {
                    return;
                }

                if (!transaction.FirstReadCommitted)
                {
                    // 尚未取得本笔首个 3D 写入成功回执：候选不得进入 TP。
                    // 首读在途时有界暂存，等待回执后按原接收时间重放；首读尚未开始则丢弃（F2）。
                    BufferPreCommitCandidateLocked(transaction, packet);
                    return;
                }

                ApplyTpResult(transaction, transportProtocol.ProcessPdu(packet.Pdu, now));
                return;
        }
    }

    /// <summary>只用于发送推进：当前索引已尝试且未确认，且原始回显逐字节相同。</summary>
    private static bool MatchesCurrentUnconfirmedEcho(ActiveTransaction transaction, byte[] raw)
    {
        var index = transaction.CurrentIndex;
        return index >= 0
            && index < transaction.EchoRaws.Length
            && transaction.WriteAttempted[index]
            && !transaction.EchoConfirmed[index]
            && raw.AsSpan().SequenceEqual(transaction.EchoRaws[index]);
    }

    /// <summary>用于旧回显过滤与响应阶段历史匹配：任一已尝试分片的回显字节相同即可。</summary>
    private static bool MatchesAnyAttemptedEcho(ActiveTransaction transaction, byte[] raw)
    {
        for (var index = 0; index < transaction.EchoRaws.Length; index++)
        {
            if (transaction.WriteAttempted[index]
                && raw.AsSpan().SequenceEqual(transaction.EchoRaws[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 首个 3D 提交前的候选处理（审查方案 §3.3-2～4）：
    /// 尚未开始读取时不存在合法来源，只记录不参与业务；读取在途时按接收顺序有界暂存。
    /// </summary>
    private void BufferPreCommitCandidateLocked(ActiveTransaction transaction, DiagnosticPacket packet)
    {
        if (!transaction.ReadAttemptPending)
        {
            return;
        }

        transaction.PendingReadCandidates ??= [];
        if (transaction.PendingReadCandidates.Count >= MaxPendingReadCandidates)
        {
            CompleteLocked(transaction, DiagnosticFailureReason.ProtocolAttributionError);
            return;
        }

        // 保留原始接收时间：重放时 N_Cr 与截止仍按真实到达时间计算（§3.3-6）。
        transaction.PendingReadCandidates.Add(packet);
    }

    /// <summary>3D 即将开始提交：此后的候选可暂存，等成功回执再决定是否交付 TP。</summary>
    private void HandleDiagnosticReadAttemptStarting(object? ownerToken)
    {
        lock (stateLock)
        {
            var transaction = active;
            if (transaction is null
                || !ReferenceEquals(ownerToken, transaction)
                || transaction.Completed
                || !transaction.Plan.IsMultiFrame
                || transaction.Phase != TransactionPhase.WaitingResponse
                || !transaction.TpReceiveEnabled)
            {
                return;
            }

            transaction.ReadAttemptPending = true;
        }
    }

    /// <summary>
    /// 3D 写入取得成功回执：开放本笔响应认领，并按原始接收顺序重放暂存候选。
    /// 身份校验保证旧事务/旧连接的迟到通知不能打开新事务（§3.3-1、§3.3-7）。
    /// </summary>
    private void HandleDiagnosticReadCommitted(object? ownerToken)
    {
        lock (stateLock)
        {
            var transaction = active;
            if (transaction is null
                || !ReferenceEquals(ownerToken, transaction)
                || transaction.Completed
                || !transaction.TpReceiveEnabled)
            {
                return;
            }

            transaction.ReadAttemptPending = false;
            transaction.FirstReadCommitted = true;

            var buffered = transaction.PendingReadCandidates;
            transaction.PendingReadCandidates = null;
            if (buffered is null)
            {
                return;
            }

            if (transaction.Phase != TransactionPhase.WaitingResponse
                || timeProvider.GetTimestamp() >= transaction.ResponseDeadline)
            {
                // 重放前复核阶段与总截止：写入耗时过长时不得把暂存候选补判为成功（§3.3-6）。
                if (!transaction.Completed)
                {
                    CompleteLocked(transaction, DiagnosticFailureReason.ResponseDeadlineExceeded);
                }

                return;
            }

            foreach (var candidate in buffered)
            {
                if (transaction.Completed)
                {
                    return;
                }

                // 复用同一判定表：截止、回显过滤、校验与事务存活均按重放时的真实状态复核。
                HandleMultiFramePacketLocked(transaction, candidate);
            }
        }
    }

    /// <summary>3D 写入失败：本次未提交上下文的暂存候选不参与业务（§3.3-5）。</summary>
    private void HandleDiagnosticReadAttemptFailed(object? ownerToken)
    {
        lock (stateLock)
        {
            var transaction = active;
            if (transaction is null || !ReferenceEquals(ownerToken, transaction))
            {
                return;
            }

            transaction.ReadAttemptPending = false;
            transaction.PendingReadCandidates = null;
        }
    }

    private void ApplyTpResult(ActiveTransaction transaction, TpReceiveResult tpResult)
    {
        switch (tpResult.Outcome)
        {
            case TpOutcome.ContinueWaiting:
                return;

            case TpOutcome.ReassemblyAbandoned:
                // 保留首次放弃原因，供业务失败结果内部参考（§7.4）。
                transaction.TpAbandonReason ??= tpResult.AbandonReason;
                return;

            case TpOutcome.MessageComplete:
                var message = tpResult.Message!;
                var prefix = transaction.Plan.ExpectedResponsePrefix;
                if (message.Length < prefix.Length
                    || !message.AsSpan(0, prefix.Length).SequenceEqual(prefix))
                {
                    // 字段不匹配（含未解析的 7F 负响应）：结束本笔，但不关闭跨事务门禁（§4.4.2）。
                    CompleteLocked(transaction, DiagnosticFailureReason.ResponseMismatch);
                    return;
                }

                transaction.ResponseVerified = true;
                transaction.VerifiedResponse = message;

                if (transaction.EchoVerified || transaction.Plan.IsMultiFrame)
                {
                    CompleteLocked(
                        transaction,
                        DiagnosticFailureReason.None,
                        message);
                    return;
                }

                // SF：完整响应先到、回显仍待核对，停止新的 3D，在原回显窗口内继续等（§15.2）。
                scheduler.StopResponsePolling();
                return;
        }
    }

    private void InspectTimerCallback(object? state)
    {
        lock (stateLock)
        {
            var transaction = active;
            if (transaction is null || transaction.Completed)
            {
                return;
            }

            var now = timeProvider.GetTimestamp();
            if (transaction.Plan.IsMultiFrame)
            {
                if (transaction.Phase == TransactionPhase.SendingRequest)
                {
                    if (now >= transaction.SendDeadline)
                    {
                        CompleteLocked(transaction, DiagnosticFailureReason.SendDeadlineExceeded);
                    }

                    return;
                }

                if (transaction.Phase != TransactionPhase.WaitingResponse)
                {
                    return;
                }
            }

            var tpResult = transportProtocol.CheckTimeout(now);
            if (tpResult.Outcome == TpOutcome.ReassemblyAbandoned)
            {
                transaction.TpAbandonReason ??= tpResult.AbandonReason;
            }

            if (!transaction.Plan.IsMultiFrame
                && !transaction.EchoVerified
                && now >= transaction.EchoDeadline)
            {
                // 回显缺失时不因已取得版本数据绕过回显要求（§15.2）。
                CompleteLocked(transaction, DiagnosticFailureReason.EchoTimeout);
                return;
            }

            if (now >= transaction.ResponseDeadline)
            {
                CompleteLocked(
                    transaction,
                    transaction.Plan.IsMultiFrame
                        ? DiagnosticFailureReason.ResponseDeadlineExceeded
                        : DiagnosticFailureReason.DeadlineExceeded);
            }
        }
    }

    private void HandleConnectionStateChanged(bool connected)
    {
        lock (stateLock)
        {
            isConnected = connected;
            if (connected)
            {
                return;
            }

            // 断线提升连接代次：入队时绑定的旧代次请求在出队后/进入独占前会被判为断开，
            // 杜绝重连后旧请求重放 3C。
            connectionGeneration++;

            // 等待独占的请求既不在 queue 也不在 active：必须在此单独结束，
            // 断线后立即返回，不依赖旧应用写入结束（F5 §7.5.5）。旧连接的独占申请由调度器自行失效。
            if (enteringPending is { } entering)
            {
                CompleteEnteringLocked(entering, DiagnosticFailureReason.Disconnected);
            }

            if (active is { Completed: false } transaction)
            {
                CompleteLocked(transaction, DiagnosticFailureReason.Disconnected);
            }

            // 断线清空排队诊断，不重放（§11.5）。
            DrainQueueLocked(DiagnosticFailureReason.Disconnected);
        }
    }

    /// <summary>单笔完成：唯一发布点，负责进度快照、队列策略与门禁（§4.4、§4.9）。</summary>
    private DiagnosticResult CompleteLocked(
        ActiveTransaction transaction,
        DiagnosticFailureReason reason,
        byte[]? response = null)
    {
        if (transaction.Completed)
        {
            return transaction.PublishedResult!;
        }

        transaction.TerminalPhase = response is null
            ? transaction.CurrentPhase
            : null;
        var progress = BuildProgress(transaction);
        var result = response is not null
            ? DiagnosticResult.Succeeded(response, progress, transaction.TpAbandonReason)
            : DiagnosticResult.Failed(reason, transaction.TpAbandonReason, progress);

        // 先发布结果再标记完成，避免无锁读取看到“已完成但结果为 null”。
        transaction.PublishedResult = result;
        transaction.Completed = true;
        // 完成即失效读取上下文与暂存候选：迟到通知与重放不得再改变已冻结的结果（F2 §3.3-7）。
        transaction.ReadAttemptPending = false;
        transaction.PendingReadCandidates = null;
        scheduler.StopResponsePolling();
        transaction.EchoAwaiter.TrySetResult(false);
        transaction.Lifecycle.TrySetResult(true);
        ApplyBatchPolicyLocked(transaction, reason);
        transaction.Completion.TrySetResult(result);
        // F3：结果冻结即完成调用方等待，不等发送出口排空。
        // 调用者得到结果不等于资源释放：排空与恢复仍由收尾路径继续执行。
        transaction.Owner?.Completion.TrySetResult(result);
        return result;
    }

    private DiagnosticResult Complete(ActiveTransaction transaction, DiagnosticFailureReason reason)
    {
        lock (stateLock)
        {
            return CompleteLocked(transaction, reason);
        }
    }

    private void ApplyBatchPolicyLocked(ActiveTransaction transaction, DiagnosticFailureReason reason)
    {
        if (reason is DiagnosticFailureReason.None or DiagnosticFailureReason.ResponseMismatch)
        {
            // 完整响应但不匹配（含 7F）：这是"设备拒绝"，不是"来路不明"，继续 FIFO。
            return;
        }

        if (reason is DiagnosticFailureReason.Shutdown
            or DiagnosticFailureReason.InvalidRequest
            or DiagnosticFailureReason.QueueAborted
            or DiagnosticFailureReason.DiagnosticGateClosed)
        {
            return;
        }

        if (reason == DiagnosticFailureReason.Disconnected)
        {
            if (transaction.WriteAttemptCount > 0 && !transaction.ResponseVerified)
            {
                CloseGateLocked();
            }

            DrainQueueLocked(DiagnosticFailureReason.Disconnected);
            return;
        }

        var abortBatch = reason switch
        {
            DiagnosticFailureReason.RequestWriteFailed
                or DiagnosticFailureReason.RequestWriteTimeout
                or DiagnosticFailureReason.EchoTimeout
                or DiagnosticFailureReason.SendDeadlineExceeded
                or DiagnosticFailureReason.InterFrameDeadlineExceeded => transaction.Plan.IsMultiFrame,
            // 超时只结束本笔（含清空同批排队），但不关闭跨事务门禁：
            // 设备无应答时迟到响应本就极少出现，锁死整轮的代价远高于它挡下的风险。
            DiagnosticFailureReason.ResponseDeadlineExceeded
                or DiagnosticFailureReason.DeadlineExceeded
                or DiagnosticFailureReason.ProtocolAttributionError => true,
            DiagnosticFailureReason.Cancelled => transaction.WriteAttemptCount > 0,
            _ => false
        };

        // 只有"确实看见不该出现的字节"才关闭准入：归属异常、响应阶段取消、带在途请求断线。
        // 单纯超时不再关锁，失败后可直接重试（实施计划 §4.4.2 修订）。
        if (reason == DiagnosticFailureReason.ProtocolAttributionError)
        {
            CloseGateLocked();
        }

        if (reason == DiagnosticFailureReason.Cancelled
            && transaction.Phase is TransactionPhase.EchoDrain or TransactionPhase.WaitingResponse)
        {
            CloseGateLocked();
        }

        if (abortBatch)
        {
            // 中止整批：清空后续排队，收尾期间新提交同样返回 QueueAborted（§4.4.1）。
            cleanupPending = true;
            DrainQueueLocked(DiagnosticFailureReason.QueueAborted);
        }
    }

    private void CloseGateLocked()
    {
        diagnosticGateClosed = true;
    }

    private void DrainQueueLocked(DiagnosticFailureReason reason)
    {
        while (queue.Count > 0)
        {
            var pending = queue.Dequeue();
            if (pending.TryAbort())
            {
                pending.Completion.TrySetResult(DiagnosticResult.Failed(
                    reason,
                    null,
                    new DiagnosticProgress
                    {
                        TotalPduCount = pending.Plan.Frames.Length,
                        FailedPhase = DiagnosticPhase.Queued
                    }));
            }

            deferredRegistrationDisposals.Add(pending.Registration);
        }
    }

    private CancellationTokenRegistration RegisterRuntimeCancellation(
        ActiveTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return default;
        }

        return cancellationToken.Register(
            static state =>
            {
                var (service, transaction) = ((DiagnosticTransactionService, ActiveTransaction))state!;
                lock (service.stateLock)
                {
                    if (!transaction.Completed)
                    {
                        service.CompleteLocked(transaction, DiagnosticFailureReason.Cancelled);
                    }
                }
            },
            (this, transaction));
    }

    private DiagnosticProgress BuildProgress(ActiveTransaction transaction)
    {
        return new DiagnosticProgress
        {
            TotalPduCount = transaction.Frames.Length,
            WriteAttemptCount = transaction.WriteAttemptCount,
            SerialWriteCompletedCount = transaction.SerialWriteCompletedCount,
            EchoConfirmedCount = transaction.EchoConfirmedCount,
            UdsBytesWriteCompleted = transaction.UdsBytesWriteCompleted,
            CurrentPduIndex = transaction.CurrentIndex >= 0 ? transaction.CurrentIndex : null,
            FailedPhase = transaction.TerminalPhase,
            InFlightWriteAtCompletion = transaction.WriteInFlight
        };
    }

    private static int GetPayloadBytes(ExecutionPlan plan, int index)
    {
        if (!plan.IsMultiFrame)
        {
            return plan.MessageLength;
        }

        if (index == 0)
        {
            return Math.Min(5, plan.MessageLength);
        }

        var offset = 5 + ((index - 1) * 6);
        return Math.Clamp(plan.MessageLength - offset, 0, 6);
    }

    private void FlushDeferredRegistrations()
    {
        List<CancellationTokenRegistration>? pending = null;
        lock (stateLock)
        {
            if (deferredRegistrationDisposals.Count == 0)
            {
                return;
            }

            pending = [.. deferredRegistrationDisposals];
            deferredRegistrationDisposals.Clear();
        }

        foreach (var registration in pending)
        {
            registration.Dispose();
        }
    }

    private enum WaitOutcome
    {
        Signaled,
        TimedOut,
        Aborted,
        Cancelled
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken, Task? abort = null)
    {
        if (delay <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            completion,
            delay,
            Timeout.InfiniteTimeSpan);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            completion);

        if (abort is not null)
        {
            var completed = await Task.WhenAny(completion.Task, abort).ConfigureAwait(false);
            if (completed != completion.Task && !cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        await completion.Task.ConfigureAwait(false);
    }

    private async Task DelayUntilAsync(long deadline, CancellationToken cancellationToken, Task? abort = null)
    {
        var delay = timeProvider.GetElapsedTime(timeProvider.GetTimestamp(), deadline);
        if (delay > TimeSpan.Zero)
        {
            await DelayAsync(delay, cancellationToken, abort).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 等待信号、截止、事务中止或取消（§4.3）。信号先到返回 Signaled，截止先到返回 TimedOut；
    /// 事务已由其他路径结束时返回 Aborted，避免发送循环在失败后继续提交下一片。
    /// </summary>
    private async Task<WaitOutcome> WaitForSignalAsync(
        Task signal,
        long deadline,
        CancellationToken cancellationToken,
        Task? abort = null)
    {
        if (abort is { IsCompleted: true })
        {
            return WaitOutcome.Aborted;
        }

        if (signal.IsCompleted)
        {
            return WaitOutcome.Signaled;
        }

        var remaining = timeProvider.GetElapsedTime(timeProvider.GetTimestamp(), deadline);
        if (remaining <= TimeSpan.Zero)
        {
            return WaitOutcome.TimedOut;
        }

        var timeout = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            timeout,
            remaining,
            Timeout.InfiniteTimeSpan);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            cancelled);

        var candidates = new List<Task>(4) { signal, timeout.Task, cancelled.Task };
        if (abort is not null)
        {
            candidates.Add(abort);
        }

        var completed = await Task.WhenAny(candidates).ConfigureAwait(false);
        if (completed == cancelled.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WaitOutcome.Cancelled;
        }

        if (abort is not null && completed == abort)
        {
            return WaitOutcome.Aborted;
        }

        return completed == signal ? WaitOutcome.Signaled : WaitOutcome.TimedOut;
    }

    /// <summary>释放与运行期收尾可能并发；已释放的定时器不再变更，避免收尾被异常打断。</summary>
    private void SetInspectTimer(TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            inspectTimer.Change(dueTime, period);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private long ToTimestampUnits(TimeSpan value)
    {
        return (long)(value.TotalSeconds * timeProvider.TimestampFrequency);
    }

    /// <summary>入队前校验并生成不可变执行计划（§4.10 硬性配置隔离）。</summary>
    private static bool TryCreatePlan(
        DiagnosticRequest request,
        out ExecutionPlan plan,
        out string? error)
    {
        plan = null!;
        error = null;

        if (request.UdsRequest is null || request.UdsRequest.Length == 0)
        {
            error = "UDS 请求不能为空";
            return false;
        }

        if (request.ExpectedResponsePrefix is null)
        {
            error = "预期响应前缀不能为空";
            return false;
        }

        var length = request.UdsRequest.Length;
        if (length > DiagnosticMultiFrameOptions.EncodingLengthLimit)
        {
            error = $"请求长度 {length} 超过编码上限 {DiagnosticMultiFrameOptions.EncodingLengthLimit}";
            return false;
        }

        // 入队前复制请求与响应规则，避免调用者在排队期间修改数组（§4.1）。
        var udsRequest = request.UdsRequest.ToArray();
        var prefix = request.ExpectedResponsePrefix.ToArray();

        if (length <= 6)
        {
            if (!TryValidateSingleFrameTiming(request.Timing, out error))
            {
                return false;
            }

            plan = new ExecutionPlan
            {
                Nad = request.Nad,
                UdsRequest = udsRequest,
                ExpectedResponsePrefix = prefix,
                Frames = LinTransportProtocol.EncodeRequestFrames(request.Nad, udsRequest),
                IsMultiFrame = false,
                SfTiming = request.Timing
            };
            return true;
        }

        // L > 6：必须显式提供 MultiFrameOptions，且完全不读取 request.Timing。
        var options = request.MultiFrameOptions;
        if (options is null)
        {
            error = "长度大于 6 字节的请求必须提供 MultiFrameOptions";
            return false;
        }

        if (!TryValidateMultiFrameOptions(length, options, out error))
        {
            return false;
        }

        byte[][] frames;
        try
        {
            frames = LinTransportProtocol.EncodeRequestFrames(request.Nad, udsRequest, options.MaxRequestLength);
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        plan = new ExecutionPlan
        {
            Nad = request.Nad,
            UdsRequest = udsRequest,
            ExpectedResponsePrefix = prefix,
            Frames = frames,
            IsMultiFrame = true,
            MultiFrame = options
        };
        return true;
    }

    private static bool TryValidateSingleFrameTiming(DiagnosticTiming timing, out string? error)
    {
        error = null;
        if (timing is null)
        {
            error = "缺少 SF 时间配置";
            return false;
        }

        if (timing.FirstResponsePollDelay < TimeSpan.Zero
            || !IsPositive(timing.ResponsePollPeriod)
            || !IsPositive(timing.TotalDeadline)
            || !IsPositive(timing.EchoWaitTimeout)
            || !IsPositive(timing.ConsecutiveFrameTimeout))
        {
            error = "SF 时间配置必须为正数";
            return false;
        }

        return true;
    }

    private static bool TryValidateMultiFrameOptions(
        int messageLength,
        DiagnosticMultiFrameOptions options,
        out string? error)
    {
        error = null;

        if (options.MaxRequestLength is < 1 or > DiagnosticMultiFrameOptions.EncodingLengthLimit)
        {
            error = $"最大请求长度必须为 1～{DiagnosticMultiFrameOptions.EncodingLengthLimit}";
            return false;
        }

        if (messageLength > options.MaxRequestLength)
        {
            error = $"请求长度 {messageLength} 超过配置上限 {options.MaxRequestLength}";
            return false;
        }

        var send = options.Send;
        var response = options.Response;
        if (send is null || response is null)
        {
            error = "缺少发送或响应时间配置";
            return false;
        }

        if (!IsPositive(send.WriteBudget)
            || !IsPositive(send.EchoBudget)
            || send.MinimumInterFrameGap < TimeSpan.Zero
            || !IsPositive(send.InterFrameGapBudget)
            || send.SchedulingMargin < TimeSpan.Zero
            || send.EchoDrainDuration < TimeSpan.Zero
            || send.EchoToleranceAfterLastComplete < TimeSpan.Zero)
        {
            error = "多帧发送时间配置非法";
            return false;
        }

        if (send.InterFrameGapBudget < send.MinimumInterFrameGap)
        {
            error = "最大相邻间隔不得小于最小发送间隔";
            return false;
        }

        if (send.EchoToleranceAfterLastComplete < send.EchoDrainDuration)
        {
            error = "回显容忍窗口不得小于固定隔离长度";
            return false;
        }

        if (!IsPositive(response.ResponseBudget)
            || response.FirstResponsePollDelay < TimeSpan.Zero
            || !IsPositive(response.ResponsePollPeriod)
            || !IsPositive(response.ConsecutiveFrameTimeout))
        {
            error = "多帧响应时间配置非法";
            return false;
        }

        if (response.ResponseBudget <= send.EchoDrainDuration)
        {
            error = "响应预算必须覆盖固定回显隔离窗口";
            return false;
        }

        var pduCount = LinTransportProtocol.CalculatePduCount(messageLength);
        TimeSpan worstCase;
        try
        {
            worstCase = CalculateSendBudget(pduCount, send);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "发送预算超出可表示范围";
            return false;
        }

        if (send.SendBudgetOverride is { } budgetOverride)
        {
            if (budgetOverride <= TimeSpan.Zero)
            {
                error = "显式发送上限必须为正数";
                return false;
            }

            if (budgetOverride < worstCase)
            {
                error = "显式发送上限不足以覆盖声明长度的最坏发送预算";
                return false;
            }
        }

        return true;
    }

    private static bool IsPositive(TimeSpan value) => value > TimeSpan.Zero;
}
