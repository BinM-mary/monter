using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// Intel HEX 文本解析（服务层，与界面无关）。
/// 支持记录类型：00 数据、01 文件结束、02 扩展段地址、04 扩展线性地址、05 起始线性地址；
/// 逐行校验（冒号、字符、长度字段、校验和），失败抛 <see cref="FirmwareFileFormatException"/> 并带行号。
/// 输出归一化映像：最低数据地址为基地址，段间空洞填 0xFF（FLASH 默认值）。
/// </summary>
internal static class IntelHexParser
{
    /// <summary>解析结果：映像、基地址、摘要与空洞填充字节数（日志提示用）。</summary>
    public sealed record ParseResult(
        byte[] Image,
        long BaseAddress,
        IntelHexSummary Summary,
        long GapBytes);

    public static ParseResult Parse(ReadOnlySpan<char> text)
    {
        long baseAddress = 0;
        long? startAddress = null;
        var records = 0;
        var dataRecords = 0;
        var dataBytes = 0L;
        var lowest = -1L;
        var highest = -1L;
        var segments = new List<(long Address, byte[] Data)>();
        var endOfFileReached = false;

        var lineNumber = 0;
        foreach (var rawLine in ReadLines(text))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (endOfFileReached)
            {
                // 规范上 EOF 之后不应再有记录；冗余内容忽略，不中断解析。
                continue;
            }

            if (line[0] != ':')
            {
                throw new FirmwareFileFormatException($"第 {lineNumber} 行缺少起始冒号“:”。");
            }

            var payload = line[1..];
            if (payload.Length % 2 != 0)
            {
                throw new FirmwareFileFormatException($"第 {lineNumber} 行字符数为奇数，无法按字节解析。");
            }

            var bytes = new byte[payload.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!TryParseHexByte(payload.AsSpan(i * 2, 2), out var value))
                {
                    throw new FirmwareFileFormatException($"第 {lineNumber} 行包含非法十六进制字符。");
                }

                bytes[i] = value;
            }

            // Intel HEX 校验：整行全部字节（长度、地址、类型、数据、校验）求和应为 0。
            var sum = 0;
            foreach (var value in bytes)
            {
                sum += value;
            }

            if ((sum & 0xFF) != 0)
            {
                throw new FirmwareFileFormatException($"第 {lineNumber} 行校验和不匹配。");
            }

            var dataLength = bytes[0];
            if (bytes.Length != dataLength + 5)
            {
                throw new FirmwareFileFormatException($"第 {lineNumber} 行长度字段与实际字节数不一致。");
            }

            var offset = (bytes[1] << 8) | bytes[2];
            var recordType = bytes[3];
            var data = bytes[4..(4 + dataLength)];
            records++;

            switch (recordType)
            {
                case 0x00:
                {
                    var address = baseAddress + offset;
                    if (dataLength > 0)
                    {
                        segments.Add((address, data));
                        dataRecords++;
                        dataBytes += dataLength;
                        if (lowest < 0 || address < lowest)
                        {
                            lowest = address;
                        }

                        highest = Math.Max(highest, address + dataLength - 1);
                    }

                    break;
                }

                case 0x01:
                    endOfFileReached = true;
                    break;

                // 扩展段地址：基地址 = 值 × 16，后续数据地址 = 基地址 + 行内偏移。
                case 0x02 when dataLength == 2:
                    baseAddress = ((long)data[0] << 12) | ((long)data[1] << 4);
                    break;

                // 扩展线性地址：基地址 = 值 × 65536，后续数据地址 = 基地址 + 行内偏移。
                case 0x04 when dataLength == 2:
                    baseAddress = ((long)data[0] << 24) | ((long)data[1] << 16);
                    break;

                // 起始线性地址（程序入口，仅记录不参与映像）。
                case 0x05 when dataLength == 4:
                    startAddress = ((long)data[0] << 24) | ((long)data[1] << 16)
                        | ((long)data[2] << 8) | data[3];
                    break;

                default:
                    // 未知记录类型：行校验已通过，按常见烧录工具的做法忽略以保持前向兼容。
                    break;
            }
        }

        if (dataRecords == 0)
        {
            throw new FirmwareFileFormatException("文件不包含任何数据记录，不是有效的 Intel HEX 固件。");
        }

        var image = BuildImage(segments, lowest, highest, out var gapBytes);
        var summary = new IntelHexSummary(records, dataRecords, dataBytes, lowest, highest, startAddress);
        return new ParseResult(image, lowest, summary, gapBytes);
    }

    /// <summary>按地址拼接数据段：空洞填 0xFF 并统计填充量；同一地址多次写入时以最后一条为准。</summary>
    private static byte[] BuildImage(
        List<(long Address, byte[] Data)> segments,
        long lowest,
        long highest,
        out long gapBytes)
    {
        var length = (int)(highest - lowest + 1);
        var image = new byte[length];
        Array.Fill(image, (byte)0xFF);

        var written = new bool[length];
        foreach (var (address, data) in segments)
        {
            var start = (int)(address - lowest);
            for (var i = 0; i < data.Length; i++)
            {
                image[start + i] = data[i];
                written[start + i] = true;
            }
        }

        gapBytes = 0L;
        for (var i = 0; i < length; i++)
        {
            if (!written[i])
            {
                gapBytes++;
            }
        }

        return image;
    }

    /// <summary>按换行拆分文本为行（不带迭代器：ref struct 局部变量不能跨 yield 边界）。</summary>
    private static List<string> ReadLines(ReadOnlySpan<char> text)
    {
        var lines = new List<string>();
        var remaining = text;
        while (!remaining.IsEmpty)
        {
            var index = remaining.IndexOf('\n');
            if (index < 0)
            {
                lines.Add(new string(remaining));
                break;
            }

            lines.Add(new string(remaining[..index]));
            remaining = remaining[(index + 1)..];
        }

        return lines;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> chars, out byte value)
    {
        value = 0;
        var high = HexDigit(chars[0]);
        var low = HexDigit(chars[1]);
        if (high < 0 || low < 0)
        {
            return false;
        }

        value = (byte)((high << 4) | low);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };
}
