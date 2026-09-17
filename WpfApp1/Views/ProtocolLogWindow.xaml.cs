using System.Collections.Specialized;
using System.ComponentModel;
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
    private bool closeRequestedByApplication;

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
        IsVisibleChanged += ProtocolLogWindow_IsVisibleChanged;
        Closing += ProtocolLogWindow_Closing;
        Closed += ProtocolLogWindow_Closed;
    }

    public void CloseForApplicationExit()
    {
        closeRequestedByApplication = true;
        Close();
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
        IsVisibleChanged -= ProtocolLogWindow_IsVisibleChanged;

        if (logScrollViewer is not null)
        {
            logScrollViewer.ScrollChanged -= LogScrollViewer_ScrollChanged;
        }
    }

    private void ProtocolLogWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            followLatest = true;
            ScheduleScrollToLatest();
        }
    }

    private void ProtocolLogWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (closeRequestedByApplication)
        {
            return;
        }

        // 保留窗口和 ViewModel，下一次打开时直接复用，避免重新加载全部历史报文。
        e.Cancel = true;
        Hide();
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
        if (!followLatest || scrollToLatestPending || !IsLoaded || !IsVisible)
        {
            return;
        }

        scrollToLatestPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                scrollToLatestPending = false;

                if (!followLatest || !IsVisible || LogListView.Items.Count == 0)
                {
                    return;
                }

                autoScrolling = true;
                try
                {
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
