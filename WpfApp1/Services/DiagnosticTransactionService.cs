using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 诊断事务协调层（依据 WpfApp1诊断架构讨论记录.md §7.2、§7.3、§11、§15）：
/// - 同一总线上的诊断串行执行：一个全局 FIFO，多个排队诊断之间不穿插应用帧；
/// - 3C 回显先登记再发送，按原始字节核对；回显只结束回显等待，不单独结束业务；
/// - 成功需"已收到有效 3C 回显"且"完整业务响应验证通过"；两者都满足才算成功并结束；
/// - 总截止 t0 + 5 s 按每笔独立计算，不以所有排队请求的时间总和计算；
/// - TP 以普通返回值报告处理结果，放弃原因由本层保存，与报文窗口显示分开；
/// - 断线结束当前业务并清空排队诊断，不重放；应用退出/释放时同样终止等待者。
/// </summary>
public sealed class DiagnosticTransactionService : IDisposable
{
    private sealed class PendingTransaction
    {
        public PendingTransaction(DiagnosticRequest request, long connectionGeneration)
        {
            Request = request;
            ConnectionGeneration = connectionGeneration;
        }

        public DiagnosticRequest Request { get; }

        /// <summary>入队时的连接代次；执行前校验，跨断线重连的旧请求不再发送。</summary>
        public long ConnectionGeneration { get; }

        public TaskCompletionSource<DiagnosticResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActiveTransaction
    {
        public ActiveTransaction(DiagnosticRequest request, byte[] expectedEchoRaw)
        {
            Request = request;
            ExpectedEchoRaw = expectedEchoRaw;
        }

        public DiagnosticRequest Request { get; }

        /// <summary>9 字节：8 字节请求 PDU + 经典校验。</summary>
        public byte[] ExpectedEchoRaw { get; }

        public long EchoDeadline { get; set; }

        public long TotalDeadline { get; set; }

        public bool EchoVerified { get; set; }

        public bool ResponseVerified { get; set; }

        public byte[]? VerifiedResponse { get; set; }

        public string? TpAbandonReason { get; set; }

        public bool Completed { get; set; }

        public TaskCompletionSource<DiagnosticResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly LinFrameChannel channel;
    private readonly LinBusScheduler scheduler;
    private readonly LinTransportProtocol transportProtocol = new();
    private readonly object stateLock = new();
    private readonly Queue<PendingTransaction> queue = new();
    private readonly Timer inspectTimer;

    private ActiveTransaction? active;
    private Task? enterModeTask;
    private bool loopActive;
    private long loopGeneration;
    private long connectionGeneration;
    private bool isConnected;
    private bool disposed;

    public DiagnosticTransactionService(LinFrameChannel channel, LinBusScheduler scheduler)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        isConnected = scheduler.IsConnected;
        inspectTimer = new Timer(InspectTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        channel.DiagnosticPacketReceived += HandleDiagnosticPacket;
        channel.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    /// <summary>
    /// 诊断活动状态变化：入队（true）到队列清空（false）之间，界面禁用电机按钮并保留 0x33 显示。
    /// 排队等待也算诊断进行中（§6）。
    /// </summary>
    public event Action<bool>? DiagnosticActivityChanged;

    /// <summary>提交一笔诊断业务；按提交顺序排队执行，等待期间不发送应用帧。</summary>
    public Task<DiagnosticResult> ExecuteAsync(DiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long generation;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            generation = connectionGeneration;
        }

        var pending = new PendingTransaction(request, generation);
        var raiseActive = false;
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var becomeActive = queue.Count == 0 && active is null;
            queue.Enqueue(pending);

            if (becomeActive)
            {
                // 首笔请求立即申请暂停应用发送，其耗时不计入 t0 之后的业务时间（§5）。
                enterModeTask ??= scheduler.EnterDiagnosticModeAsync();
                raiseActive = true;
            }

            if (!loopActive)
            {
                loopActive = true;
                _ = ProcessQueueAsync();
            }
        }

        if (raiseActive)
        {
            DiagnosticActivityChanged?.Invoke(true);
        }

        return pending.Completion.Task;
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
            if (active is { Completed: false } current)
            {
                CompleteLocked(current, DiagnosticResult.Failed(DiagnosticFailureReason.Shutdown));
            }

            while (queue.Count > 0)
            {
                queue.Dequeue().Completion.TrySetResult(
                    DiagnosticResult.Failed(DiagnosticFailureReason.Shutdown));
            }
        }

        inspectTimer.Dispose();
        channel.DiagnosticPacketReceived -= HandleDiagnosticPacket;
        channel.ConnectionStateChanged -= HandleConnectionStateChanged;
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
                    if (disposed || queue.Count == 0)
                    {
                        break;
                    }

                    pending = queue.Dequeue();
                    enterTask = enterModeTask;
                }

                DiagnosticResult result;
                try
                {
                    // 出队后、执行前校验连接代次：跨断线重连的旧请求直接判断开，不重放 3C。
                    if (pending.ConnectionGeneration != connectionGeneration)
                    {
                        result = DiagnosticResult.Failed(DiagnosticFailureReason.Disconnected);
                        pending.Completion.TrySetResult(result);
                        continue;
                    }

                    if (enterTask is not null)
                    {
                        await enterTask.ConfigureAwait(false);
                    }

                    // 等待进入独占期间可能发生断线；进入前再校验一次连接代次。
                    if (pending.ConnectionGeneration != connectionGeneration)
                    {
                        result = DiagnosticResult.Failed(DiagnosticFailureReason.Disconnected);
                        pending.Completion.TrySetResult(result);
                        continue;
                    }

                    result = await RunTransactionAsync(pending.Request).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 进入诊断模式或发送 3C 失败（多为断线）。
                    var reason = isConnected
                        ? DiagnosticFailureReason.RequestWriteFailed
                        : DiagnosticFailureReason.Disconnected;
                    result = DiagnosticResult.Failed(reason);
                    Debug.WriteLine(exception);
                }

                pending.Completion.TrySetResult(result);
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
                    enterModeTask = null;
                    active = null;
                }
            }

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

    private async Task<DiagnosticResult> RunTransactionAsync(DiagnosticRequest request)
    {
        var timing = request.Timing;
        var pdu = LinTransportProtocol.EncodeSingleFrame(request.Nad, request.UdsRequest);

        // 先登记回显预期再发送，避免回显早于登记到达时漏掉核对（§11.2）。
        var echoRaw = new byte[pdu.Length + 1];
        pdu.CopyTo(echoRaw, 0);
        echoRaw[^1] = LinFrameCodec.CalculateClassicChecksum(pdu);

        var transaction = new ActiveTransaction(request, echoRaw);
        lock (stateLock)
        {
            active = transaction;
        }

        transportProtocol.BeginReceive(request.Nad, timing.ConsecutiveFrameTimeout);

        long t0;
        try
        {
            var receipt = await scheduler.SendDiagnosticRequestAsync(pdu).ConfigureAwait(false);
            t0 = receipt.CompletedTimestamp;
        }
        catch (Exception exception)
        {
            lock (stateLock)
            {
                active = null;
            }

            transportProtocol.Reset();
            Debug.WriteLine(exception);
            return DiagnosticResult.Failed(isConnected
                ? DiagnosticFailureReason.RequestWriteFailed
                : DiagnosticFailureReason.Disconnected);
        }

        lock (stateLock)
        {
            transaction.EchoDeadline = t0 + ToTimestampUnits(timing.EchoWaitTimeout);
            transaction.TotalDeadline = t0 + ToTimestampUnits(timing.TotalDeadline);
        }

        scheduler.StartResponsePolling(t0, timing.ResponsePollPeriod, transaction.TotalDeadline);
        inspectTimer.Change(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));

        var result = await transaction.Completion.Task.ConfigureAwait(false);

        // 统一收尾：不再提交新的 3D → 清理 TP 与计时 → 等待在途写入完成。
        scheduler.StopResponsePolling();
        inspectTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        transportProtocol.Reset();
        await scheduler.WaitForSendIdleAsync().ConfigureAwait(false);

        lock (stateLock)
        {
            active = null;
        }

        return result;
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

            // 接收路径的硬截止检查：不依赖 10 ms 检查定时器，
            // 超过总截止/回显截止的数据即使完整也不判成功（§15）。
            var now = packet.ReceivedTimestamp;
            if (now >= transaction.TotalDeadline)
            {
                CompleteLocked(
                    transaction,
                    DiagnosticResult.Failed(
                        DiagnosticFailureReason.DeadlineExceeded,
                        transaction.TpAbandonReason));
                return;
            }

            if (packet.Raw.AsSpan().SequenceEqual(transaction.ExpectedEchoRaw))
            {
                // 回显须在其截止窗口内到达才算有效（§7.3）。
                if (now >= transaction.EchoDeadline)
                {
                    CompleteLocked(
                        transaction,
                        DiagnosticResult.Failed(
                            DiagnosticFailureReason.EchoTimeout,
                            transaction.TpAbandonReason));
                    return;
                }

                // 有效 3C 回显：原始字节与请求 PDU 及经典校验逐字节一致。
                transaction.EchoVerified = true;
                if (transaction.ResponseVerified)
                {
                    CompleteLocked(
                        transaction,
                        DiagnosticResult.Succeeded(
                            transaction.VerifiedResponse!,
                            transaction.TpAbandonReason));
                }

                return;
            }

            // 其余校验通过的候选按响应分段交给 TP；校验失败与归属不明的候选只由入口记录。
            if (!packet.ChecksumValid)
            {
                return;
            }

            ApplyTpResult(transaction, transportProtocol.ProcessPdu(packet.Pdu, packet.ReceivedTimestamp));
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
                var prefix = transaction.Request.ExpectedResponsePrefix;
                if (message.Length < prefix.Length
                    || !message.AsSpan(0, prefix.Length).SequenceEqual(prefix))
                {
                    // 字段不匹配：立即结束本笔业务，不再等待总截止（§15.1）。
                    CompleteLocked(
                        transaction,
                        DiagnosticResult.Failed(
                            DiagnosticFailureReason.ResponseMismatch,
                            transaction.TpAbandonReason));
                    return;
                }

                transaction.ResponseVerified = true;
                transaction.VerifiedResponse = message;

                if (transaction.EchoVerified)
                {
                    CompleteLocked(
                        transaction,
                        DiagnosticResult.Succeeded(message, transaction.TpAbandonReason));
                    return;
                }

                // 完整响应先到、回显仍待核对：停止新的 3D，业务保持运行并在原回显窗口内继续等（§15.2）。
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

            var now = Stopwatch.GetTimestamp();
            var tpResult = transportProtocol.CheckTimeout(now);
            if (tpResult.Outcome == TpOutcome.ReassemblyAbandoned)
            {
                transaction.TpAbandonReason ??= tpResult.AbandonReason;
            }

            // 回显缺失时不因已取得版本数据绕过回显要求（§15.2）。
            if (!transaction.EchoVerified && now >= transaction.EchoDeadline)
            {
                CompleteLocked(
                    transaction,
                    DiagnosticResult.Failed(
                        DiagnosticFailureReason.EchoTimeout,
                        transaction.TpAbandonReason));
                return;
            }

            if (now >= transaction.TotalDeadline)
            {
                CompleteLocked(
                    transaction,
                    DiagnosticResult.Failed(
                        DiagnosticFailureReason.DeadlineExceeded,
                        transaction.TpAbandonReason));
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

            if (active is { Completed: false } transaction)
            {
                CompleteLocked(
                    transaction,
                    DiagnosticResult.Failed(
                        DiagnosticFailureReason.Disconnected,
                        transaction.TpAbandonReason));
            }

            // 断线清空排队诊断，不重放（§11.5）。
            while (queue.Count > 0)
            {
                queue.Dequeue().Completion.TrySetResult(
                    DiagnosticResult.Failed(DiagnosticFailureReason.Disconnected));
            }
        }
    }

    private void CompleteLocked(ActiveTransaction transaction, DiagnosticResult result)
    {
        if (transaction.Completed)
        {
            return;
        }

        transaction.Completed = true;
        // 业务结束即停止本笔新的 3D；在途写入由协调层收尾统一等待。
        scheduler.StopResponsePolling();
        transaction.Completion.TrySetResult(result);
    }

    private static long ToTimestampUnits(TimeSpan value)
    {
        return (long)(value.TotalSeconds * Stopwatch.Frequency);
    }
}
