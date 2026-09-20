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

    private static readonly TimeSpan UiBatchInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxEntriesPerUiBatch = 200;

    private readonly Dispatcher dispatcher;
    private readonly BoundedLogEntryCollection entries = new();
    private readonly object pendingEntriesLock = new();
    private readonly Queue<CommunicationLogEntry> pendingEntries = new();
    private readonly DispatcherTimer flushTimer;
    private int retentionLimit = DefaultRetentionLimit;

    public CommunicationLogService(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        this.dispatcher = dispatcher;
        flushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = UiBatchInterval
        };
        flushTimer.Tick += FlushTimer_Tick;
        flushTimer.Start();
    }

    public ObservableCollection<CommunicationLogEntry> Entries => entries;

    public int RetentionLimit
    {
        get
        {
            lock (pendingEntriesLock)
            {
                return retentionLimit;
            }
        }
    }

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
            lock (pendingEntriesLock)
            {
                this.retentionLimit = retentionLimit;
                TrimPendingEntriesToLimit();
            }

            entries.TrimTo(retentionLimit);
        });
    }

    public void Clear()
    {
        RunOnDispatcher(() =>
        {
            lock (pendingEntriesLock)
            {
                pendingEntries.Clear();
            }

            entries.Clear();
        });
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

        lock (pendingEntriesLock)
        {
            pendingEntries.Enqueue(entry);
            TrimPendingEntriesToLimit();
        }
    }

    private void FlushTimer_Tick(object? sender, EventArgs e)
    {
        List<CommunicationLogEntry> batch;
        lock (pendingEntriesLock)
        {
            if (pendingEntries.Count == 0)
            {
                return;
            }

            var batchSize = Math.Min(MaxEntriesPerUiBatch, pendingEntries.Count);
            batch = new List<CommunicationLogEntry>(batchSize);
            for (var index = 0; index < batchSize; index++)
            {
                batch.Add(pendingEntries.Dequeue());
            }
        }

        entries.AddBatchWithinLimit(batch, RetentionLimit);
    }

    private void TrimPendingEntriesToLimit()
    {
        while (pendingEntries.Count > retentionLimit)
        {
            pendingEntries.Dequeue();
        }
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
        public void AddBatchWithinLimit(
            IReadOnlyList<CommunicationLogEntry> newEntries,
            int maximumCount)
        {
            if (newEntries.Count == 0)
            {
                return;
            }

            var itemsToAdd = newEntries.Count <= maximumCount
                ? newEntries.ToList()
                : newEntries
                    .Skip(newEntries.Count - maximumCount)
                    .ToList();
            var removeCount = Math.Max(0, Count + itemsToAdd.Count - maximumCount);
            if (removeCount > 0)
            {
                RemoveOldestItems(removeCount);
            }

            foreach (var entry in itemsToAdd)
            {
                Items.Add(entry);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));

            // WPF 的 ListCollectionView 不稳定支持包含多个元素的 Add 范围事件，
            // 统一使用一次 Reset，避免批量报文进入日志窗口时抛异常。
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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
