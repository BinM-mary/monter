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

    public void RecordSent(ReadOnlySpan<byte> data, LinFrameDefinition? definition = null)
    {
        Add(CommunicationLogDirection.Sent, data, definition, null);
    }

    public void RecordReceived(ReadOnlySpan<byte> data, LinFrameDefinition? definition = null, string? error = null)
    {
        Add(CommunicationLogDirection.Received, data, definition, error);
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

    private void Add(CommunicationLogDirection direction, ReadOnlySpan<byte> data, LinFrameDefinition? definition, string? error)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var entry = new CommunicationLogEntry(
            DateTimeOffset.Now,
            direction,
            Convert.ToHexString(data),
            definition,
            error);

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

            // 逐条 Add：ListCollectionView 对单元素事件做增量更新，
            // 只过滤新条目、不重建视图，虚拟化和滚动状态保持不变。
            // 此前每批统一发一次 Reset，视图会对全部条目（最多 5000/10000 条）
            // 重新执行筛选并整表重建，100 ms 轮询下每 50 ms 一次，
            // 报文窗口打开时明显卡顿，窗口隐藏时也在后台全量刷新。
            foreach (var entry in itemsToAdd)
            {
                Add(entry);
            }
        }

        public void TrimTo(int maximumCount)
        {
            var removeCount = Count - maximumCount;
            if (removeCount <= 0)
            {
                return;
            }

            // 保留条数调整是罕见操作，直接批量移除后整体 Reset 一次即可。
            if (Items is List<CommunicationLogEntry> itemList)
            {
                itemList.RemoveRange(0, removeCount);
            }
            else
            {
                for (var index = 0; index < removeCount; index++)
                {
                    Items.RemoveAt(0);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void RemoveOldestItems(int removeCount)
        {
            // 逐条 RemoveAt(0)：每条发出单元素 Remove 事件，视图增量更新。
            // 批量最多 200 条，列表头移除为内存搬移，成本可忽略。
            for (var index = 0; index < removeCount; index++)
            {
                RemoveAt(0);
            }
        }
    }
}
