using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MahApps.Metro.Controls;
using WpfApp1.Services;
using WpfApp1.ViewModels;

namespace WpfApp1.Views;

public partial class ProtocolLogWindow : MetroWindow
{
    private readonly ICommunicationLogService communicationLog;
    private readonly ProtocolLogWindowViewModel viewModel;
    private ScrollViewer? logScrollViewer;
    private bool followLatest = true;
    private bool autoScrolling;
    private bool scrollToLatestPending;

    public ProtocolLogWindow(ICommunicationLogService communicationLog)
    {
        ArgumentNullException.ThrowIfNull(communicationLog);

        InitializeComponent();
        this.communicationLog = communicationLog;
        viewModel = new ProtocolLogWindowViewModel(communicationLog);
        DataContext = viewModel;

        communicationLog.Entries.CollectionChanged += CommunicationLogEntries_CollectionChanged;
        viewModel.FilteredEntriesChanged += ViewModel_FilteredEntriesChanged;
        Loaded += ProtocolLogWindow_Loaded;
        Closed += ProtocolLogWindow_Closed;
    }

    private void ProtocolLogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (logScrollViewer is null)
        {
            logScrollViewer = FindVisualChild<ScrollViewer>(LogListView);
            if (logScrollViewer is not null)
            {
                logScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;
            }
        }

        followLatest = true;
        ScheduleScrollToLatest();
    }

    private void ProtocolLogWindow_Closed(object? sender, EventArgs e)
    {
        communicationLog.Entries.CollectionChanged -= CommunicationLogEntries_CollectionChanged;
        viewModel.FilteredEntriesChanged -= ViewModel_FilteredEntriesChanged;
        viewModel.Dispose();

        if (logScrollViewer is not null)
        {
            logScrollViewer.ScrollChanged -= LogScrollViewer_ScrollChanged;
        }
    }

    private void ViewModel_FilteredEntriesChanged(object? sender, EventArgs e)
    {
        ScheduleScrollToLatest();
    }

    private void CommunicationLogEntries_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScheduleScrollToLatest();
        }
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (autoScrolling || scrollToLatestPending || logScrollViewer is null)
        {
            return;
        }

        // ExtentHeightChange only indicates that a new row was laid out; it
        // should not cancel following when the user was already at the bottom.
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0)
        {
            return;
        }

        followLatest = IsAtBottom(logScrollViewer);
    }

    private void ScheduleScrollToLatest()
    {
        if (!followLatest || scrollToLatestPending || !IsLoaded)
        {
            return;
        }

        scrollToLatestPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                scrollToLatestPending = false;

                if (!followLatest || LogListView.Items.Count == 0)
                {
                    return;
                }

                autoScrolling = true;
                try
                {
                    LogListView.UpdateLayout();
                    LogListView.ScrollIntoView(LogListView.Items[LogListView.Items.Count - 1]);
                    logScrollViewer?.ScrollToEnd();
                }
                finally
                {
                    autoScrolling = false;
                }
            }));
    }

    private static bool IsAtBottom(ScrollViewer scrollViewer)
    {
        return scrollViewer.VerticalOffset + scrollViewer.ViewportHeight
            >= scrollViewer.ExtentHeight - 1;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T matchingChild)
            {
                return matchingChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
