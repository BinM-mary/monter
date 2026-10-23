using System.IO;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>固件文件加载失败（格式不支持、内容非法、IO 错误）时抛出，消息面向用户可直接展示。</summary>
public sealed class FirmwareFileFormatException : Exception
{
    public FirmwareFileFormatException(string message)
        : base(message)
    {
    }

    public FirmwareFileFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 固件文件加载与解析（与界面解耦：界面只传入路径，消费 <see cref="FirmwareImage"/>）。
/// BIN：整文件即映像（基地址 0），CRC32 按文件内容；
/// HEX：Intel HEX 记录解析为归一化映像（空洞填 0xFF），CRC32 按映像内容。
/// 读取与解析在后台线程执行，不阻塞调用方 UI 线程。
/// </summary>
public sealed class FirmwareFileService
{
    /// <summary>支持的固件扩展名（小写，含点）。</summary>
    public static readonly string[] SupportedExtensions = [".bin", ".hex"];

    /// <summary>文件对话框筛选字符串（供界面层使用，与服务内部判定保持一致）。</summary>
    public const string OpenFileDialogFilter =
        "固件文件 (*.bin;*.hex)|*.bin;*.hex|二进制固件 (*.bin)|*.bin|Intel HEX (*.hex)|*.hex";

    public async Task<FirmwareImage> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FirmwareFileFormatException("未选择固件文件。");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(SupportedExtensions, extension) < 0)
        {
            throw new FirmwareFileFormatException($"文件格式不支持（{extension}），请选择 .bin 或 .hex 固件。");
        }

        if (!File.Exists(path))
        {
            throw new FirmwareFileFormatException($"文件不存在或已被移动：{path}");
        }

        var info = new FileInfo(path);
        var format = extension == ".hex" ? FirmwareFileFormat.IntelHex : FirmwareFileFormat.Binary;

        try
        {
            if (format == FirmwareFileFormat.Binary)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                return new FirmwareImage(
                    format,
                    info.Name,
                    path,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTime),
                    bytes,
                    BaseAddress: 0,
                    FirmwareCrc32.Compute(bytes),
                    HexSummary: null);
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            // 解析为纯 CPU 工作，放后台线程避免大文件卡 UI。
            var parsed = await Task.Run(() => IntelHexParser.Parse(text), cancellationToken).ConfigureAwait(false);
            return new FirmwareImage(
                format,
                info.Name,
                path,
                info.Length,
                new DateTimeOffset(info.LastWriteTime),
                parsed.Image,
                parsed.BaseAddress,
                FirmwareCrc32.Compute(parsed.Image),
                parsed.Summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FirmwareFileFormatException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new FirmwareFileFormatException($"读取固件文件失败：{exception.Message}", exception);
        }
    }
}
