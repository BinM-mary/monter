using WpfApp1.Models;

namespace WpfApp1.Services;

public sealed class DeviceCommunicationService
{
    private readonly ISerialPortConnectionService connectionService;
    private readonly ICommunicationLogService communicationLog;
    private readonly ProtocolFrameParser responseParser = new();
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private readonly object pendingRequestLock = new();
    private PendingRequest? pendingRequest;

    public DeviceCommunicationService(
        ISerialPortConnectionService connectionService,
        ICommunicationLogService communicationLog)
    {
        this.connectionService = connectionService;
        this.communicationLog = communicationLog;
        connectionService.BytesReceived += bytes => responseParser.Append(bytes);
        connectionService.ConnectionStateChanged += HandleConnectionStateChanged;
        responseParser.FrameReceived += HandleFrameReceived;
        responseParser.FrameRejected += HandleFrameRejected;
    }

    public event Action<ProtocolFrame>? FrameReceived;

    public async Task SendAsync(
        byte command,
        byte subCommand,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var rawFrame = ProtocolFrameCodec.CreateRequest(command, subCommand, data.Span);
        await connectionService.SendAsync(rawFrame, cancellationToken);
        communicationLog.RecordSent(rawFrame);
    }

    public async Task<ProtocolFrame> SendRequestAsync(
        byte command,
        byte subCommand,
        ReadOnlyMemory<byte> data,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        await requestLock.WaitAsync(cancellationToken);
        var request = new PendingRequest(command, (byte)(subCommand + 0x40));
        try
        {
            lock (pendingRequestLock)
            {
                pendingRequest = request;
            }

            await SendAsync(command, subCommand, data, cancellationToken);
            return await request.Response.Task.WaitAsync(responseTimeout, cancellationToken);
        }
        finally
        {
            lock (pendingRequestLock)
            {
                if (ReferenceEquals(pendingRequest, request))
                {
                    pendingRequest = null;
                }
            }

            requestLock.Release();
        }
    }

    private void HandleConnectionStateChanged(bool isConnected)
    {
        if (!isConnected)
        {
            responseParser.Reset();
            CompletePendingRequest(new InvalidOperationException("串口已断开。"));
        }
    }

    private void HandleFrameReceived(ProtocolFrame frame, byte[] rawFrame)
    {
        communicationLog.RecordReceived(rawFrame);

        PendingRequest? request;
        lock (pendingRequestLock)
        {
            request = pendingRequest;
        }

        if (request is not null
            && frame.Command == request.Command
            && (frame.SubCommand == request.SuccessSubCommand || frame.SubCommand == 0xDF))
        {
            request.Response.TrySetResult(frame);
        }

        FrameReceived?.Invoke(frame);
    }

    private void HandleFrameRejected(byte[] rawFrame)
    {
        // 错误帧不进入命令处理或响应匹配，但要保留在日志中供诊断。
        communicationLog.RecordReceived(rawFrame);
    }

    private void CompletePendingRequest(Exception exception)
    {
        PendingRequest? request;
        lock (pendingRequestLock)
        {
            request = pendingRequest;
        }

        request?.Response.TrySetException(exception);
    }

    private sealed class PendingRequest
    {
        public PendingRequest(byte command, byte successSubCommand)
        {
            Command = command;
            SuccessSubCommand = successSubCommand;
        }

        public byte Command { get; }

        public byte SuccessSubCommand { get; }

        public TaskCompletionSource<ProtocolFrame> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
