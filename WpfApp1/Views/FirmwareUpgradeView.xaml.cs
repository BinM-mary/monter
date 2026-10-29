using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApp1.ViewModels;

namespace WpfApp1.Views;

/// <summary>
/// 固件升级页。除 XAML 初始化外只负责"日志信息"面板的滚动行为，日志内容与业务
/// 判定全部在 <see cref="FirmwareUpgradeViewModel"/>：
/// <list type="bullet">
/// <item>默认跟随最新行：新日志追加后滚到最后一行；</item>
/// <item>用户操作把视口带离底部（滚轮、拖滚动条、PageUp、触控板）即暂停跟随，
/// 暂停期间只冻结视口——日志照常写入、条数照常增加、500 条上限照常裁剪；</item>
/// <item>用户滚回底部自动恢复跟随；</item>
/// <item>打开新固件、开始新一轮 OTA 时由 <see cref="FirmwareUpgradeViewModel.LogFollowRequested"/>
/// 强制回到跟随态；切页回来是新建实例，同样从跟随态开始。</item>
/// </list>
/// 判定方式与 <see cref="ProtocolLogWindow"/> 一致：1 px 容差判底、程序滚动期间用标志
/// 屏蔽自身引发的 ScrollChanged、滚动延后到 ContextIdle（布局完成后 ExtentHeight 才是最终值）。
/// </summary>
public partial class FirmwareUpgradeView : UserControl
{
    private FirmwareUpgradeViewModel? viewModel;
    private MouseWheelEventHandler? wheelHandler;

    /// <summary>是否跟随最新行。用户离开底部置 false，滚回底部置 true。</summary>
    private bool followLatest = true;

    /// <summary>程序正在滚动：用于屏蔽 ScrollToEnd 自身引发的 ScrollChanged。</summary>
    private bool autoScrolling;

    /// <summary>已排队一次滚动，合并同一批连续追加（OTA 分步日志会在同一拍密集到达）。</summary>
    private bool scrollToLatestPending;

    public FirmwareUpgradeView()
    {
        InitializeComponent();
        Loaded += FirmwareUpgradeView_Loaded;
        Unloaded += FirmwareUpgradeView_Unloaded;
    }

    private void FirmwareUpgradeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is not null || DataContext is not FirmwareUpgradeViewModel current)
        {
            return;
        }

        viewModel = current;
        viewModel.FirmwareLogs.CollectionChanged += FirmwareLogs_CollectionChanged;
        viewModel.LogFollowRequested += ViewModel_LogFollowRequested;
        LogScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;

        // handledEventsToo: true —— ScrollViewer 自己处理滚轮后会标记 handled，
        // 这里在其后仍把事件标记为已处理，滚轮到达日志区边界时不再冒泡给整页
        // ScrollViewer（否则内层不动、整页跟着滚，看起来像日志面板在乱跳）。
        wheelHandler = LogScrollViewer_MouseWheel;
        LogScrollViewer.AddHandler(MouseWheelEvent, wheelHandler, true);

        // 页面每次切入都是新实例（ContentControl + DataTemplate），一律从跟随态开始。
        ResumeFollowing();
    }

    private void FirmwareUpgradeView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.FirmwareLogs.CollectionChanged -= FirmwareLogs_CollectionChanged;
        viewModel.LogFollowRequested -= ViewModel_LogFollowRequested;
        LogScrollViewer.ScrollChanged -= LogScrollViewer_ScrollChanged;

        if (wheelHandler is not null)
        {
            LogScrollViewer.RemoveHandler(MouseWheelEvent, wheelHandler);
            wheelHandler = null;
        }

        viewModel = null;
    }

    private void FirmwareLogs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScheduleScrollToLatest();
            return;
        }

        // 清空日志会让集合 Reset，此时回到跟随态：随后"已清空"那条也应当可见。
        // Remove（超过 500 条裁掉最旧）不触发滚动，也不改动跟随标记：视口由用户掌控。
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResumeFollowing();
        }
    }

    private void ViewModel_LogFollowRequested(object? sender, EventArgs e)
    {
        ResumeFollowing();
    }

    private void LogScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 日志面板自身已按需滚动；这里只负责截断冒泡链路。
        e.Handled = true;
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (autoScrolling || scrollToLatestPending)
        {
            return;
        }

        // 只有 ExtentHeightChange（新行排版让内容变长）而偏移与视口都没变时，
        // 不是用户滚动。若不排除，每追加一行都会先把状态判成"离开底部"，
        // 跟随会在第一条新日志之后静默失效。
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0)
        {
            return;
        }

        followLatest = IsAtBottom(LogScrollViewer);
    }

    private void ResumeFollowing()
    {
        followLatest = true;
        ScheduleScrollToLatest();
    }

    private void ScheduleScrollToLatest()
    {
        if (!followLatest || scrollToLatestPending || !IsLoaded)
        {
            return;
        }

        scrollToLatestPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                scrollToLatestPending = false;

                if (!followLatest)
                {
                    return;
                }

                autoScrolling = true;
                try
                {
                    // 延后到布局完成再滚：Add 刚返回时 ExtentHeight 还是旧值，
                    // 立即滚会停在"上一版的底部"，最后一行仍在视野外。
                    LogScrollViewer.ScrollToEnd();
                }
                finally
                {
                    autoScrolling = false;
                }
            }));
    }

    /// <summary>1 px 容差：DPI 缩放与换行后的行高取整会让偏移差出不足 1 px。</summary>
    private static bool IsAtBottom(ScrollViewer scrollViewer)
    {
        return scrollViewer.VerticalOffset + scrollViewer.ViewportHeight
            >= scrollViewer.ExtentHeight - 1;
    }
}
