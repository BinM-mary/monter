using System.IO;
using System.Text;

namespace WpfApp1.Services;

/// <summary>
/// 未处理异常落盘。界面线程、后台任务与 AppDomain 三处未处理异常共用同一入口，
/// 日志写到 <c>%LOCALAPPDATA%\HolyChipMotorStudio\logs\crash-yyyyMMdd.log</c>，
/// 便于现场把异常详情带回来定位。
/// </summary>
/// <remarks>
/// 写日志自身全部兜底：磁盘不可写、路径非法等情况一律吞掉，绝不在异常处理路径上再抛一次异常。
/// </remarks>
public static class CrashLog
{
    private static readonly object SyncRoot = new();
    private static string? lastWrittenPath;

    /// <summary>最近一次成功写入的日志文件路径；从未写成时为 <see langword="null"/>。</summary>
    public static string? LastWrittenPath => lastWrittenPath;

    /// <summary>
    /// 追加一条异常记录并返回日志文件路径（写失败时返回上次成功路径或 <see langword="null"/>）。
    /// </summary>
    /// <param name="source">异常来源说明，例如“界面线程”“后台任务”。</param>
    /// <param name="exception">异常对象，记录其完整 <c>ToString()</c>（含内部异常与堆栈）。</param>
    public static string? Write(string source, Exception? exception)
    {
        if (exception is null)
        {
            return lastWrittenPath;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HolyChipMotorStudio",
                "logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var entry = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .AppendLine(source)
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            lock (SyncRoot)
            {
                File.AppendAllText(path, entry, Encoding.UTF8);
                lastWrittenPath = path;
            }

            return path;
        }
        catch
        {
            return lastWrittenPath;
        }
    }
}
