using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public sealed partial class ProtocolLogWindowViewModel : ObservableObject, IDisposable
{
    private const string AllFilterText = "全部";
    private static readonly IReadOnlyList<LogRetentionOption> retentionOptions =
    [
        new("5000 条", 5_000),
        new("1 万条", 10_000)
    ];

    private readonly ICommunicationLogService communicationLog;
    private readonly ICollectionView filteredEntries;
    private readonly Dictionary<FilterValueKey, int> filterValueCounts = new();
    private bool updatingFilterOptions;
    private bool updatesSuspended;
    private bool filterOptionsRefreshPending;

    // 暂停显示期间新条目进入缓冲，显示集合保持不变；恢复时一次性补入。
    private readonly ObservableCollection<CommunicationLogEntry> displayedEntries = [];
    private readonly List<CommunicationLogEntry> pausedPendingEntries = [];

    public ProtocolLogWindowViewModel(ICommunicationLogService communicationLog)
    {
        ArgumentNullException.ThrowIfNull(communicationLog);

        this.communicationLog = communicationLog;
        // 视图绑定镜像集合而非服务集合本身：否则暂停显示时 ListView 仍会随
        // 源集合的 Add 事件自动插入新行，无法真正冻结画面。
        foreach (var entry in communicationLog.Entries)
        {
            displayedEntries.Add(entry);
        }

        filteredEntries = new ListCollectionView(displayedEntries);
        filteredEntries.Filter = FilterLogEntry;
        selectedRetentionOption = retentionOptions.FirstOrDefault(option =>
            option.Limit == communicationLog.RetentionLimit)
            ?? retentionOptions[0];

        communicationLog.Entries.CollectionChanged += CommunicationLogEntries_CollectionChanged;
        RebuildFilterValueCounts();
        RefreshFilterOptions();
        filteredEntries.Refresh();
    }

    public ICommunicationLogService CommunicationLog => communicationLog;

    public ICollectionView FilteredEntries => filteredEntries;

    public IReadOnlyList<LogRetentionOption> RetentionOptions => retentionOptions;

    public ObservableCollection<string> DirectionFilterOptions { get; } =
    [
        AllFilterText
    ];

    public ObservableCollection<string> TypeFilterOptions { get; } =
    [
        AllFilterText
    ];

    public ObservableCollection<string> FrameIdFilterOptions { get; } = [AllFilterText];

    public ObservableCollection<string> ProtectedIdFilterOptions { get; } = [AllFilterText];

    [ObservableProperty]
    private string? selectedDirectionFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedTypeFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedFrameIdFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedProtectedIdFilter = AllFilterText;

    [ObservableProperty]
    private LogRetentionOption selectedRetentionOption = retentionOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool isPaused;

    public string PauseButtonText => IsPaused ? "继续显示" : "暂停显示";

    public event EventHandler? FilteredEntriesChanged;

    public void SuspendUpdates()
    {
        if (updatesSuspended)
        {
            return;
        }

        updatesSuspended = true;
    }

    public void ResumeUpdates()
    {
        if (!updatesSuspended)
        {
            return;
        }

        try
        {
            // 隐藏期间日志仍然保存在通信日志集合中；重新显示时重建筛选状态并主动刷新一次视图。
            // 同时把镜像集合重新同步到源集合（隐藏期间的 Add 事件被挂起丢弃了）。
            pausedPendingEntries.Clear();
            displayedEntries.Clear();
            foreach (var entry in communicationLog.Entries)
            {
                displayedEntries.Add(entry);
            }

            RebuildFilterValueCounts();
            RefreshFilterOptions();
        }
        finally
        {
            updatesSuspended = false;
        }

        RefreshFilteredEntries();

        NotifyFilteredEntriesChanged();
    }

    public void Dispose()
    {
        communicationLog.Entries.CollectionChanged -= CommunicationLogEntries_CollectionChanged;
        filteredEntries.Filter = null;
    }

    partial void OnSelectedDirectionFilterChanged(string? value)
    {
        HandleFilterSelectionChanged();
    }

    partial void OnSelectedTypeFilterChanged(string? value)
    {
        HandleFilterSelectionChanged();
    }

    partial void OnSelectedFrameIdFilterChanged(string? value)
    {
        HandleFilterSelectionChanged();
    }

    partial void OnSelectedProtectedIdFilterChanged(string? value)
    {
        HandleFilterSelectionChanged();
    }

    partial void OnSelectedRetentionOptionChanged(LogRetentionOption value)
    {
        communicationLog.SetRetentionLimit(value.Limit);
    }

    [RelayCommand]
    private void Clear()
    {
        communicationLog.Clear();
    }

    /// <summary>
    /// 暂停/继续显示：暂停时新报文只进缓冲，列表冻结；恢复时补入缓冲条目并刷新。
    /// 后台日志记录不受暂停影响。
    /// </summary>
    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
        {
            foreach (var entry in pausedPendingEntries)
            {
                displayedEntries.Add(entry);
            }

            pausedPendingEntries.Clear();
            RefreshFilteredEntries();
        }
    }

    [RelayCommand]
    private void ResetFilters()
    {
        SelectedDirectionFilter = AllFilterText;
        SelectedTypeFilter = AllFilterText;
        SelectedFrameIdFilter = AllFilterText;
        SelectedProtectedIdFilter = AllFilterText;
        RefreshFilteredEntries();
    }

    private void CommunicationLogEntries_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (updatesSuspended)
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                {
                    foreach (var entry in e.NewItems.OfType<CommunicationLogEntry>())
                    {
                        AddFilterValues(entry);
                        if (IsPaused)
                        {
                            pausedPendingEntries.Add(entry);
                        }
                        else
                        {
                            displayedEntries.Add(entry);
                        }
                    }
                }

                ScheduleFilterOptionsRefresh();
                if (!IsPaused)
                {
                    NotifyFilteredEntriesChanged();
                }

                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                {
                    foreach (var entry in e.OldItems.OfType<CommunicationLogEntry>())
                    {
                        RemoveFilterValues(entry);
                        // 保留条数裁剪掉的最旧条目可能在显示集合，也可能还在暂停缓冲里。
                        displayedEntries.Remove(entry);
                        pausedPendingEntries.Remove(entry);
                    }
                }

                ScheduleFilterOptionsRefresh();
                break;
            default:
                // Reset（清空或保留条数整体裁剪）：镜像集合与缓冲一并重建。
                pausedPendingEntries.Clear();
                displayedEntries.Clear();
                foreach (var entry in communicationLog.Entries)
                {
                    displayedEntries.Add(entry);
                }

                RebuildFilterValueCounts();
                RefreshFilterOptions();
                NotifyFilteredEntriesChanged();
                break;
        }
    }

    private void HandleFilterSelectionChanged()
    {
        if (updatingFilterOptions || updatesSuspended)
        {
            return;
        }

        RefreshFilteredEntries();
        RefreshFilterOptions();
    }

    /// <summary>
    /// 合并多个条目变更触发的筛选选项刷新：同一调度周期内只重建一次。
    /// 逐条 Add/Remove 通知下避免错误风暴等突发批量时重复执行四维级联重建。
    /// </summary>
    private void ScheduleFilterOptionsRefresh()
    {
        if (updatesSuspended || filterOptionsRefreshPending)
        {
            return;
        }

        filterOptionsRefreshPending = true;
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                filterOptionsRefreshPending = false;
                if (updatesSuspended)
                {
                    return;
                }

                RefreshFilterOptions();
            }));
    }

    private void RefreshFilterOptions()
    {
        if (updatingFilterOptions)
        {
            return;
        }

        updatingFilterOptions = true;
        var selectionWasChanged = false;
        try
        {
            // 其他筛选条件改变后，当前选择可能失效；最多四轮即可收敛。
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var directionOptions = GetCandidateFilterValues(FilterDimension.Direction);
                var typeOptions = GetCandidateFilterValues(FilterDimension.Type);
                var frameIdOptions = GetCandidateFilterValues(FilterDimension.FrameId);
                var protectedIdOptions = GetCandidateFilterValues(FilterDimension.ProtectedId);

                var selectionChanged = EnsureSelections(
                    directionOptions,
                    typeOptions,
                    frameIdOptions,
                    protectedIdOptions);
                selectionWasChanged |= selectionChanged;

                // 先校正选择值，再增量调整选项，避免清空集合时 WPF 暂时产生 null。
                ReplaceOptions(DirectionFilterOptions, directionOptions);
                ReplaceOptions(TypeFilterOptions, typeOptions);
                ReplaceOptions(FrameIdFilterOptions, frameIdOptions);
                ReplaceOptions(ProtectedIdFilterOptions, protectedIdOptions);

                if (!selectionChanged)
                {
                    break;
                }
            }
        }
        finally
        {
            updatingFilterOptions = false;
        }

        if (selectionWasChanged)
        {
            // 自动纠正无效选项时，属性变更被保护标志暂时抑制，需要补做一次视图刷新。
            RefreshFilteredEntries();
        }
    }

    private List<string> GetCandidateFilterValues(FilterDimension dimension)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in filterValueCounts)
        {
            if (pair.Value <= 0 || !MatchesOtherFilters(pair.Key, dimension))
            {
                continue;
            }

            var value = dimension switch
            {
                FilterDimension.Direction => pair.Key.Direction,
                FilterDimension.Type => pair.Key.Type,
                FilterDimension.FrameId => pair.Key.FrameId,
                FilterDimension.ProtectedId => pair.Key.ProtectedId,
                _ => "—"
            };

            if (IsFilterValue(value))
            {
                values.Add(value);
            }
        }

        return values
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool MatchesOtherFilters(FilterValueKey key, FilterDimension excludedDimension)
    {
        return (excludedDimension == FilterDimension.Direction
                || MatchesFilter(SelectedDirectionFilter, key.Direction))
            && (excludedDimension == FilterDimension.Type
                || MatchesFilter(SelectedTypeFilter, key.Type))
            && (excludedDimension == FilterDimension.FrameId
                || MatchesFilter(SelectedFrameIdFilter, key.FrameId))
            && (excludedDimension == FilterDimension.ProtectedId
                || MatchesFilter(SelectedProtectedIdFilter, key.ProtectedId));
    }

    private bool EnsureSelections(
        IEnumerable<string> directionOptions,
        IEnumerable<string> typeOptions,
        IEnumerable<string> frameIdOptions,
        IEnumerable<string> protectedIdOptions)
    {
        var selectionChanged = false;
        selectionChanged |= EnsureSelection(
            SelectedDirectionFilter,
            directionOptions,
            value => SelectedDirectionFilter = value);
        selectionChanged |= EnsureSelection(
            SelectedTypeFilter,
            typeOptions,
            value => SelectedTypeFilter = value);
        selectionChanged |= EnsureSelection(
            SelectedFrameIdFilter,
            frameIdOptions,
            value => SelectedFrameIdFilter = value);
        selectionChanged |= EnsureSelection(
            SelectedProtectedIdFilter,
            protectedIdOptions,
            value => SelectedProtectedIdFilter = value);
        return selectionChanged;
    }

    private static bool EnsureSelection(
        string? selectedValue,
        IEnumerable<string> options,
        Action<string> setSelection)
    {
        if (selectedValue is not null
            && (IsAllFilter(selectedValue) || ContainsFilterValue(options, selectedValue)))
        {
            return false;
        }

        setSelection(AllFilterText);
        return true;
    }

    private static void ReplaceOptions(
        ObservableCollection<string> options,
        IEnumerable<string> values)
    {
        var desiredOptions = new[] { AllFilterText }.Concat(values).ToList();
        if (options.SequenceEqual(desiredOptions, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var desiredSet = desiredOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = options.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(options[index]))
            {
                options.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredOptions.Count; index++)
        {
            var desiredOption = desiredOptions[index];
            if (index < options.Count
                && string.Equals(options[index], desiredOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingIndex = -1;
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                if (string.Equals(
                        options[optionIndex],
                        desiredOption,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = optionIndex;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                options.Move(existingIndex, index);
            }
            else
            {
                options.Insert(index, desiredOption);
            }
        }
    }

    private bool FilterLogEntry(object item)
    {
        if (item is not CommunicationLogEntry entry)
        {
            return false;
        }

        return MatchesFilter(SelectedDirectionFilter, entry.DirectionText)
            && MatchesFilter(SelectedTypeFilter, entry.FrameTypeText)
            && MatchesFilter(SelectedFrameIdFilter, entry.FrameIdText)
            && MatchesFilter(SelectedProtectedIdFilter, entry.ProtectedIdText);
    }

    private void RefreshFilteredEntries()
    {
        if (updatesSuspended)
        {
            return;
        }

        filteredEntries.Refresh();
        NotifyFilteredEntriesChanged();
    }

    private void NotifyFilteredEntriesChanged()
    {
        FilteredEntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool MatchesFilter(string? selectedValue, string actualValue)
    {
        return IsAllFilter(selectedValue)
            || string.Equals(selectedValue, actualValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value, AllFilterText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFilterValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value != "—";
    }

    private void RebuildFilterValueCounts()
    {
        filterValueCounts.Clear();

        foreach (var entry in communicationLog.Entries)
        {
            AddFilterValues(entry);
        }
    }

    private void AddFilterValues(CommunicationLogEntry entry)
    {
        IncrementCount(filterValueCounts, new FilterValueKey(
            entry.DirectionText,
            entry.FrameTypeText,
            entry.FrameIdText,
            entry.ProtectedIdText));
    }

    private void RemoveFilterValues(CommunicationLogEntry entry)
    {
        DecrementCount(filterValueCounts, new FilterValueKey(
            entry.DirectionText,
            entry.FrameTypeText,
            entry.FrameIdText,
            entry.ProtectedIdText));
    }

    private static void IncrementCount(
        IDictionary<FilterValueKey, int> counts,
        FilterValueKey value)
    {
        counts[value] = counts.TryGetValue(value, out var count)
            ? count + 1
            : 1;
    }

    private static void DecrementCount(
        IDictionary<FilterValueKey, int> counts,
        FilterValueKey value)
    {
        if (!counts.TryGetValue(value, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            counts.Remove(value);
        }
        else
        {
            counts[value] = count - 1;
        }
    }

    private static bool ContainsFilterValue(
        IEnumerable<string> options,
        string? value)
    {
        return options.Any(option =>
            string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
    }

    private enum FilterDimension
    {
        Direction,
        Type,
        FrameId,
        ProtectedId
    }

    private readonly record struct FilterValueKey(
        string Direction,
        string Type,
        string FrameId,
        string ProtectedId);

    public sealed record LogRetentionOption(string DisplayText, int Limit);
}
