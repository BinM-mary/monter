using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 汇总通信层产生的完整收发报文，供报文输出窗口显示。
/// </summary>
public sealed class CommunicationLogService : ICommunicationLogService
{
    public const int DefaultRetentionLimit = 5_000;

    private readonly Dispatcher dispatcher;
    private readonly BoundedLogEntryCollection entries = new();

    public CommunicationLogService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public ObservableCollection<CommunicationLogEntry> Entries => entries;

    public int RetentionLimit { get; private set; } = DefaultRetentionLimit;

    public void RecordSent(ReadOnlySpan<byte> data)
    {
        Add(CommunicationLogDirection.Sent, data);
    }

    public void RecordReceived(ReadOnlySpan<byte> data)
    {
        Add(CommunicationLogDirection.Received, data);
    }

    public void SetRetentionLimit(int retentionLimit)
    {
        if (retentionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionLimit));
        }

        RunOnDispatcher(() =>
        {
            RetentionLimit = retentionLimit;
            entries.TrimTo(retentionLimit);
        });
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

        RunOnDispatcher(() =>
        {
            entries.AddWithinLimit(entry, RetentionLimit);
        });
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

    private sealed class BoundedLogEntryCollection : ObservableCollection<CommunicationLogEntry>
    {
        public void AddWithinLimit(CommunicationLogEntry entry, int maximumCount)
        {
            while (Count >= maximumCount)
            {
                // 用标准 Remove/Add 事件通知集合视图，避免达到上限后每条报文都触发 Reset 全量刷新。
                RemoveAt(0);
            }

            Add(entry);
        }

        public void TrimTo(int maximumCount)
        {
            var removeCount = Count - maximumCount;
            if (removeCount <= 0)
            {
                return;
            }

            RemoveOldestItems(removeCount);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void RemoveOldestItems(int removeCount)
        {
            if (Items is List<CommunicationLogEntry> itemList)
            {
                itemList.RemoveRange(0, removeCount);
                return;
            }

            for (var index = 0; index < removeCount; index++)
            {
                Items.RemoveAt(0);
            }
        }
    }
}
