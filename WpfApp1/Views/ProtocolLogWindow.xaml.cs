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
    // 记录用户拖动后的横向偏移；自动滚动到底时保持它，否则每批新行都会把
    // 横向滚动条重置回最左侧（ScrollToEnd/虚拟化测量会重置 HorizontalOffset）。
    private double userHorizontalOffset;

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
            viewModel.ResumeUpdates();
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
        viewModel.SuspendUpdates();
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

        // 用户横向拖动时记住位置；后续自动滚动到底时恢复它。
        if (e.HorizontalChange != 0)
        {
            userHorizontalOffset = logScrollViewer.HorizontalOffset;
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
                    // ScrollToEnd 会同时把横向偏移推到最右，且虚拟化测量期间
                    // 横向偏移也可能被清零；统一恢复到用户选定的横向位置。
                    logScrollViewer?.ScrollToHorizontalOffset(userHorizontalOffset);
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
