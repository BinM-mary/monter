using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 共用的 LIN 单帧通道。应用帧与诊断均复用串口队列和接收分发（架构/WpfApp1诊断架构讨论记录.md §12.4）。
/// 应用读取请求为 5 字节（55 AA 55 PID 00）；应用回复只有 DATA + Checksum，
/// 由 LinReadReplyParser 反算 PID/ID 后分发；诊断候选为 8 字节 PDU + 经典校验，
/// 3C/3D 归属、NAD 检查与重组由上层业务上下文完成，入口不虚构诊断 PID。
/// 所有接收数据（含未识别字节）均进入报文窗口，且只记录一次。
/// Send 完成仅代表串口写入完成，不代表中间件总线发送或电机动作完成。
/// </summary>
public sealed class LinFrameChannel : IDisposable
{
    private readonly ISerialPortConnectionService connectionService;
    private readonly ICommunicationLogService communicationLog;
    private readonly LinReadReplyParser parser;
    private readonly TimeProvider timeProvider;
    private readonly object stateLock = new();
    private readonly ITimer receiveTimer;
    private readonly TimeSpan frameAssemblyTimeout;
    private bool isConnected;
    private bool disposed;

    public LinFrameChannel(
        ISerialPortConnectionService connectionService,
        ICommunicationLogService communicationLog,
        LinFrameCatalog catalog,
        TimeSpan? frameAssemblyTimeout = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        this.communicationLog = communicationLog ?? throw new ArgumentNullException(nameof(communicationLog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.frameAssemblyTimeout = frameAssemblyTimeout ?? TimeSpan.FromMilliseconds(50);
        if (this.frameAssemblyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameAssemblyTimeout));
        }

        parser = new LinReadReplyParser(catalog, this.timeProvider);
        parser.FrameReceived += HandleFrameReceived;
        parser.FrameRejected += HandleFrameRejected;
        parser.DiagnosticFrameReceived += HandleDiagnosticFrameReceived;
        isConnected = connectionService.ConnectedPortName is not null;
        connectionService.BytesReceived += HandleBytesReceived;
        connectionService.ConnectionStateChanged += HandleConnectionStateChanged;
        // 10ms 检查一次：缓存非空且超过 50ms 未收到新字节即丢弃残缺回复。
        receiveTimer = this.timeProvider.CreateTimer(
            ExpirePartialFrame,
            null,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));
    }

    public LinFrameCatalog Catalog { get; }
    public event Action<LinFrame>? FrameReceived;
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// 诊断候选事件：8 字节 PDU + 经典校验，不含 3C/3D 归属判定。
    /// 由诊断事务协调层按当前业务上下文认领（回显或响应分段）。
    /// </summary>
    public event Action<DiagnosticPacket>? DiagnosticPacketReceived;

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

    /// <summary>
    /// 发送 3C 诊断请求（55 AA 55 3C 08 + 8 字节 PDU + 经典校验，共 14 字节）。
    /// 返回写入回执（完成与否及软件观察到的完成单调时间），供协调层作为 t0 等计时锚点。
    /// </summary>
    public Task<FrameWriteReceipt> SendDiagnosticRequestAsync(
        ReadOnlyMemory<byte> pdu,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDiagnosticDefinition(LinFrameCodec.DiagnosticRequestId, LinPublisher.Master);
        var payload = pdu.Span;
        if (payload.Length != definition.DataLength)
        {
            throw new ArgumentException($"诊断请求 PDU 必须为 {definition.DataLength} 字节。", nameof(pdu));
        }

        return WriteAsync(LinFrameCodec.CreateDiagnosticRequest(payload), definition, cancellationToken);
    }

    /// <summary>发送 3D 读取请求（55 AA 55 7D 00，5 字节，无 PDU）。</summary>
    public Task<FrameWriteReceipt> SendDiagnosticReadRequestAsync(CancellationToken cancellationToken = default)
    {
        var definition = GetDiagnosticDefinition(LinFrameCodec.DiagnosticResponseId, LinPublisher.Slave);
        return WriteAsync(LinFrameCodec.CreateReadRequest(definition.Id), definition, cancellationToken);
    }

    /// <summary>
    /// 等待传输层真实写入排空：以发送工作线程的实际完成信号为准。
    /// 外层 Send 等待被取消不代表物理写入已结束（审查方案 §4.3，F3/R8）。
    /// </summary>
    public Task WaitForTransmitIdleAsync()
    {
        return connectionService.WaitForTransmitIdleAsync();
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
        }

        ConnectionStateChanged?.Invoke(false);
        connectionService.BytesReceived -= HandleBytesReceived;
        connectionService.ConnectionStateChanged -= HandleConnectionStateChanged;
        receiveTimer.Dispose();
    }

    private LinFrameDefinition GetDiagnosticDefinition(byte id, LinPublisher publisher)
    {
        var definition = Catalog.Get(id);
        if (!definition.IsDiagnostic || definition.Publisher != publisher)
        {
            throw new InvalidOperationException($"帧 0x{id:X2} 不是该方向上的诊断帧。");
        }

        return definition;
    }

    private async Task<FrameWriteReceipt> WriteAsync(byte[] raw, LinFrameDefinition definition, CancellationToken cancellationToken)
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
        // 完成时刻在串口写入返回后、记录报文前取得，作为软件观察到的写入完成时间。
        var completedTimestamp = timeProvider.GetTimestamp();
        communicationLog.RecordSent(raw, definition);
        return new FrameWriteReceipt(completedTimestamp);
    }

    /// <summary>
    /// 解析器边界：丢弃共享缓存中的不完整候选并保留拒绝日志（实施计划 §4.5）。
    /// 供协调层在诊断阶段切换前调用，避免旧阶段残缺字节被当成新阶段响应。
    /// </summary>
    public void DiscardPartialReceiveBuffer(string reason)
    {
        lock (stateLock)
        {
            if (disposed || !isConnected)
            {
                return;
            }

            parser.DiscardPartialCandidate(reason);
        }
    }

    private void HandleBytesReceived(byte[] bytes)
    {
        // 不在通道锁内解析/分发：parser.Append 会触发 FrameReceived / DiagnosticFrameReceived
        // 业务回调，回调可能申请协调层锁与调度器锁；若此处持通道锁，会与发送路径的
        // "调度器锁 → 通道锁"构成锁环（见修复说明）。parser 自身有 bufferedBytes 锁保证线程安全。
        // 仅在锁内做轻量状态读取，连接切换由 HandleConnectionStateChanged 统一收口。
        var shouldProcess = false;
        lock (stateLock)
        {
            shouldProcess = isConnected && !disposed;
        }

        if (shouldProcess)
        {
            parser.Append(bytes);
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
        }

        // 连接状态事件同样在锁外触发，避免订阅方（协调层/调度器）在回调内再申请本通道锁。
        ConnectionStateChanged?.Invoke(connected);
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

    private void HandleDiagnosticFrameReceived(byte[] raw, byte[] pdu, bool checksumValid, long receivedTimestamp)
    {
        var packet = new DiagnosticPacket(raw, pdu, checksumValid, receivedTimestamp);
        communicationLog.RecordReceived(raw, GetDiagnosticLogDefinition());
        if (DiagnosticPacketReceived is { } handlers)
        {
            foreach (Action<DiagnosticPacket> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(packet);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }
        }
    }

    /// <summary>报文窗口显示用：诊断回包无帧头，不显示反算的 PID/ID（§13.6）。</summary>
    private LinFrameDefinition GetDiagnosticLogDefinition()
    {
        return Catalog.TryGet(LinFrameCodec.DiagnosticResponseId, out var definition) && definition is not null
            ? definition
            : Catalog.Get(LinFrameCodec.DiagnosticResponseId);
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
