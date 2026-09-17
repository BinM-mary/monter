using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
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

    public ProtocolLogWindowViewModel(ICommunicationLogService communicationLog)
    {
        ArgumentNullException.ThrowIfNull(communicationLog);

        this.communicationLog = communicationLog;
        filteredEntries = new ListCollectionView((IList)communicationLog.Entries);
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

    public ObservableCollection<string> CommandFilterOptions { get; } = [AllFilterText];

    public ObservableCollection<string> SubCommandFilterOptions { get; } = [AllFilterText];

    [ObservableProperty]
    private string? selectedDirectionFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedTypeFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedCommandFilter = AllFilterText;

    [ObservableProperty]
    private string? selectedSubCommandFilter = AllFilterText;

    [ObservableProperty]
    private LogRetentionOption selectedRetentionOption = retentionOptions[0];

    public event EventHandler? FilteredEntriesChanged;

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

    partial void OnSelectedCommandFilterChanged(string? value)
    {
        HandleFilterSelectionChanged();
    }

    partial void OnSelectedSubCommandFilterChanged(string? value)
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

    [RelayCommand]
    private void ResetFilters()
    {
        SelectedDirectionFilter = AllFilterText;
        SelectedTypeFilter = AllFilterText;
        SelectedCommandFilter = AllFilterText;
        SelectedSubCommandFilter = AllFilterText;
        RefreshFilteredEntries();
    }

    private void CommunicationLogEntries_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                {
                    foreach (var entry in e.NewItems.OfType<CommunicationLogEntry>())
                    {
                        AddFilterValues(entry);
                    }
                }

                RefreshFilterOptions();
                NotifyFilteredEntriesChanged();
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                {
                    foreach (var entry in e.OldItems.OfType<CommunicationLogEntry>())
                    {
                        RemoveFilterValues(entry);
                    }
                }

                RefreshFilterOptions();
                break;
            default:
                RebuildFilterValueCounts();
                RefreshFilterOptions();
                NotifyFilteredEntriesChanged();
                break;
        }
    }

    private void HandleFilterSelectionChanged()
    {
        if (updatingFilterOptions)
        {
            return;
        }

        RefreshFilteredEntries();
        RefreshFilterOptions();
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
                var commandOptions = GetCandidateFilterValues(FilterDimension.Command);
                var subCommandOptions = GetCandidateFilterValues(FilterDimension.SubCommand);

                var selectionChanged = EnsureSelections(
                    directionOptions,
                    typeOptions,
                    commandOptions,
                    subCommandOptions);
                selectionWasChanged |= selectionChanged;

                // 先校正选择值，再增量调整选项，避免清空集合时 WPF 暂时产生 null。
                ReplaceOptions(DirectionFilterOptions, directionOptions);
                ReplaceOptions(TypeFilterOptions, typeOptions);
                ReplaceOptions(CommandFilterOptions, commandOptions);
                ReplaceOptions(SubCommandFilterOptions, subCommandOptions);

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
                FilterDimension.Command => pair.Key.Command,
                FilterDimension.SubCommand => pair.Key.SubCommand,
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
            && (excludedDimension == FilterDimension.Command
                || MatchesFilter(SelectedCommandFilter, key.Command))
            && (excludedDimension == FilterDimension.SubCommand
                || MatchesFilter(SelectedSubCommandFilter, key.SubCommand));
    }

    private bool EnsureSelections(
        IEnumerable<string> directionOptions,
        IEnumerable<string> typeOptions,
        IEnumerable<string> commandOptions,
        IEnumerable<string> subCommandOptions)
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
            SelectedCommandFilter,
            commandOptions,
            value => SelectedCommandFilter = value);
        selectionChanged |= EnsureSelection(
            SelectedSubCommandFilter,
            subCommandOptions,
            value => SelectedSubCommandFilter = value);
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
            && MatchesFilter(SelectedCommandFilter, entry.CommandText)
            && MatchesFilter(SelectedSubCommandFilter, entry.SubCommandText);
    }

    private void RefreshFilteredEntries()
    {
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
            entry.CommandText,
            entry.SubCommandText));
    }

    private void RemoveFilterValues(CommunicationLogEntry entry)
    {
        DecrementCount(filterValueCounts, new FilterValueKey(
            entry.DirectionText,
            entry.FrameTypeText,
            entry.CommandText,
            entry.SubCommandText));
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
        Command,
        SubCommand
    }

    private readonly record struct FilterValueKey(
        string Direction,
        string Type,
        string Command,
        string SubCommand);

    public sealed record LogRetentionOption(string DisplayText, int Limit);
}
