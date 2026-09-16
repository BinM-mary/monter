using System.Collections.ObjectModel;
using System.Windows.Threading;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 汇总通信层产生的完整收发报文，供报文输出窗口显示。
/// </summary>
public sealed class CommunicationLogService : ICommunicationLogService
{
    private readonly Dispatcher dispatcher;

    public CommunicationLogService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public ObservableCollection<CommunicationLogEntry> Entries { get; } = new();

    public void RecordSent(ReadOnlySpan<byte> data)
    {
        Add(CommunicationLogDirection.Sent, data);
    }

    public void RecordReceived(ReadOnlySpan<byte> data)
    {
        Add(CommunicationLogDirection.Received, data);
    }

    public void Clear()
    {
        RunOnDispatcher(Entries.Clear);
    }

    private void Add(CommunicationLogDirection direction, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var entry = new CommunicationLogEntry(
            DateTimeOffset.Now,
            direction,
            Convert.ToHexString(data));

        RunOnDispatcher(() => Entries.Add(entry));
    }

    private void RunOnDispatcher(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }
}
