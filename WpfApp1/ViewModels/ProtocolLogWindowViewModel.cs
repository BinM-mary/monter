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

    private readonly ICommunicationLogService communicationLog;
    private readonly ICollectionView filteredEntries;

    public ProtocolLogWindowViewModel(ICommunicationLogService communicationLog)
    {
        ArgumentNullException.ThrowIfNull(communicationLog);

        this.communicationLog = communicationLog;
        filteredEntries = new ListCollectionView((IList)communicationLog.Entries);
        filteredEntries.Filter = FilterLogEntry;

        communicationLog.Entries.CollectionChanged += CommunicationLogEntries_CollectionChanged;
        UpdateFilterOptions();
        filteredEntries.Refresh();
    }

    public ICommunicationLogService CommunicationLog => communicationLog;

    public ICollectionView FilteredEntries => filteredEntries;

    public ObservableCollection<string> DirectionFilterOptions { get; } =
    [
        AllFilterText,
        "TX",
        "RX"
    ];

    public ObservableCollection<string> TypeFilterOptions { get; } =
    [
        AllFilterText,
        "请求",
        "响应",
        "错误",
        "未知"
    ];

    public ObservableCollection<string> CommandFilterOptions { get; } = [AllFilterText];

    public ObservableCollection<string> SubCommandFilterOptions { get; } = [AllFilterText];

    [ObservableProperty]
    private string selectedDirectionFilter = AllFilterText;

    [ObservableProperty]
    private string selectedTypeFilter = AllFilterText;

    [ObservableProperty]
    private string selectedCommandFilter = AllFilterText;

    [ObservableProperty]
    private string selectedSubCommandFilter = AllFilterText;

    public event EventHandler? FilteredEntriesChanged;

    public void Dispose()
    {
        communicationLog.Entries.CollectionChanged -= CommunicationLogEntries_CollectionChanged;
        filteredEntries.Filter = null;
    }

    partial void OnSelectedDirectionFilterChanged(string value)
    {
        RefreshFilteredEntries();
    }

    partial void OnSelectedTypeFilterChanged(string value)
    {
        RefreshFilteredEntries();
    }

    partial void OnSelectedCommandFilterChanged(string value)
    {
        UpdateSubCommandFilterOptions();
        RefreshFilteredEntries();
    }

    partial void OnSelectedSubCommandFilterChanged(string value)
    {
        RefreshFilteredEntries();
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
        UpdateFilterOptions();
        RefreshFilteredEntries();
    }

    private void UpdateFilterOptions()
    {
        var commandValues = communicationLog.Entries
            .Select(entry => entry.CommandText)
            .Where(IsFilterValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReplaceOptions(CommandFilterOptions, commandValues);
        if (!ContainsFilterValue(CommandFilterOptions, SelectedCommandFilter))
        {
            SelectedCommandFilter = AllFilterText;
        }

        UpdateSubCommandFilterOptions();
    }

    private void UpdateSubCommandFilterOptions()
    {
        IEnumerable<CommunicationLogEntry> entries = communicationLog.Entries;
        if (!IsAllFilter(SelectedCommandFilter))
        {
            entries = entries.Where(entry =>
                string.Equals(entry.CommandText, SelectedCommandFilter, StringComparison.OrdinalIgnoreCase));
        }

        var subCommandValues = entries
            .Select(entry => entry.SubCommandText)
            .Where(IsFilterValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReplaceOptions(SubCommandFilterOptions, subCommandValues);
        if (!ContainsFilterValue(SubCommandFilterOptions, SelectedSubCommandFilter))
        {
            SelectedSubCommandFilter = AllFilterText;
        }
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

        options.Clear();
        foreach (var option in desiredOptions)
        {
            options.Add(option);
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
        FilteredEntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool MatchesFilter(string selectedValue, string actualValue)
    {
        return IsAllFilter(selectedValue)
            || string.Equals(selectedValue, actualValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllFilter(string value)
    {
        return string.Equals(value, AllFilterText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFilterValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value != "—";
    }

    private static bool ContainsFilterValue(
        IEnumerable<string> options,
        string value)
    {
        return options.Any(option =>
            string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
    }
}
