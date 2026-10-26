using System.Windows;
using System.Windows.Threading;
using WpfApp1.Services;

namespace WpfApp1;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private int unhandledExceptionCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// 界面线程未处理异常：写日志后吞掉，保持进程存活。
    /// 只有内存耗尽这类无法继续的异常才放行，让系统按正常流程结束进程。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OutOfMemoryException)
        {
            return;
        }

        var logPath = CrashLog.Write("界面线程", e.Exception);
        e.Handled = true;

        // 只提示一次：同一个异常反复触发时避免弹窗风暴，后续仅追加日志。
        if (Interlocked.Increment(ref unhandledExceptionCount) > 1)
        {
            return;
        }

        try
        {
            var detail = string.IsNullOrEmpty(logPath)
                ? "日志文件写入失败，无法记录异常详情。"
                : $"异常详情已记录到：{logPath}";
            var message = $"程序捕获到一个未处理异常，已跳过本次操作，界面可以继续使用。\n\n{detail}";
            var owner = Current?.MainWindow;
            if (owner is not null && owner.IsLoaded)
            {
                MessageBox.Show(owner, message, "运行异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, "运行异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch
        {
            // 弹窗失败不能再抛异常，日志已经落盘。
        }
    }

    /// <summary>非界面线程的未处理异常：无法阻止进程结束，至少把堆栈写进日志。</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashLog.Write("后台线程", exception);
        }
    }

    /// <summary>未观察的任务异常：记录后标记已观察，避免进程退出时升级为未处理异常。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (e.Exception is not null)
        {
            CrashLog.Write("任务", e.Exception);
        }
    }
}
