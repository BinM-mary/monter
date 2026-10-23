using System.Globalization;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 固件十六进制视图的文本构建（服务层，与界面无关）：
/// 每行 16 字节，输出 偏移（8 位十六进制）+ 字节序列 + ASCII（不可打印字符显示“.”）。
/// 预览行数有上限，避免超大文件一次性生成过多 UI 条目。
/// </summary>
internal static class FirmwareHexFormatter
{
    /// <summary>每行展示的字节数。</summary>
    public const int BytesPerRow = 16;

    /// <summary>预览上限（字节）：超出的部分不生成预览行，由调用方记录提示。</summary>
    public const long MaxPreviewBytes = 1024 * 1024;

    public static List<HexPreviewLine> BuildLines(ReadOnlySpan<byte> data, long baseAddress)
    {
        var previewLength = (int)Math.Min(data.Length, MaxPreviewBytes);
        var rows = (previewLength + BytesPerRow - 1) / BytesPerRow;
        var lines = new List<HexPreviewLine>(rows);

        for (var offset = 0; offset < previewLength; offset += BytesPerRow)
        {
            var row = data.Slice(offset, Math.Min(BytesPerRow, previewLength - offset));
            lines.Add(new HexPreviewLine(
                baseAddress + offset,
                (baseAddress + offset).ToString("X8", CultureInfo.InvariantCulture),
                FormatHexBytes(row),
                FormatAscii(row)));
        }

        return lines;
    }

    /// <summary>文件/映像大小显示：B / KB / MB（两位小数）。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.00} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.00} MB")
    };

    /// <summary>地址显示：8 位大写十六进制（0x 前缀）。</summary>
    public static string FormatAddress(long address) =>
        "0x" + address.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>把整段数据格式化为纯十六进制字节流文本（“AA BB CC …”，大写）。</summary>
    public static string FormatHexStream(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        // 每字节 3 字符（两位 + 空格），末尾不留空格。
        var builder = new char[data.Length * 3 - 1];
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            builder[i * 3] = HexChar(value >> 4);
            builder[i * 3 + 1] = HexChar(value & 0x0F);
            if (i < data.Length - 1)
            {
                builder[i * 3 + 2] = ' ';
            }
        }

        return new string(builder);
    }

    /// <summary>预览行集合转可复制文本（每行：偏移  十六进制  ASCII）。</summary>
    public static string BuildCopyText(IReadOnlyList<HexPreviewLine> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(lines.Count * 72);
        foreach (var line in lines)
        {
            builder.Append(line.OffsetText).Append("  ").Append(line.HexText).Append("  ")
                .Append(line.AsciiText).Append('\n');
        }

        return builder.ToString(0, builder.Length - 1);
    }

    private static string FormatHexBytes(ReadOnlySpan<byte> row)
    {
        // 16 字节固定宽度：两位大写 + 空格，不足 16 字节的行按实际内容输出（等宽字体下自然对齐）。
        var builder = new char[row.Length * 3 - 1];
        for (var i = 0; i < row.Length; i++)
        {
            var value = row[i];
            builder[i * 3] = HexChar(value >> 4);
            builder[i * 3 + 1] = HexChar(value & 0x0F);
            if (i < row.Length - 1)
            {
                builder[i * 3 + 2] = ' ';
            }
        }

        return new string(builder);
    }

    private static string FormatAscii(ReadOnlySpan<byte> row)
    {
        var chars = new char[row.Length];
        for (var i = 0; i < row.Length; i++)
        {
            var value = row[i];
            chars[i] = value is >= 0x20 and <= 0x7E ? (char)value : '.';
        }

        return new string(chars);
    }

    private static char HexChar(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);
}
