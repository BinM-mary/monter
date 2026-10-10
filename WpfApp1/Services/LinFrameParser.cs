using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 解析完整的 55 AA + Sync + PID + Data + Checksum 串口表示。
/// 有效候选帧按登记长度收齐，Data 中的 55 AA 不作为分隔符。
/// </summary>
public sealed class LinFrameParser
{
    private readonly LinFrameCatalog catalog;
    private readonly List<byte> bufferedBytes = [];
    private long lastAppendTimestamp;

    public LinFrameParser(LinFrameCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public event Action<LinFrame, byte[]>? FrameReceived;
    public event Action<byte[], LinFrameDefinition?, string>? FrameRejected;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var results = new List<ParseResult>();
        lock (bufferedBytes)
        {
            lastAppendTimestamp = Stopwatch.GetTimestamp();
            bufferedBytes.AddRange(bytes.ToArray());
            while (bufferedBytes.Count > 0)
            {
                var headerIndex = FindHeader();
                if (headerIndex < 0)
                {
                    var keepCount = GetPossibleHeaderPrefixLength();
                    RejectPrefix(bufferedBytes.Count - keepCount, "无法识别 LIN 帧头", results);
                    break;
                }

                if (headerIndex > 0)
                {
                    RejectPrefix(headerIndex, "帧头前的无效字节", results);
                }

                if (bufferedBytes.Count < LinFrameCodec.HeaderLength)
                {
                    break;
                }

                var protectedId = bufferedBytes[3];
                if (!LinFrameCodec.IsProtectedIdValid(protectedId))
                {
                    results.Add(new ParseResult(bufferedBytes.GetRange(0, 4).ToArray(), null, null, "PID 校验失败"));
                    bufferedBytes.RemoveAt(0);
                    continue;
                }

                var id = (byte)(protectedId & 0x3F);
                if (!catalog.TryGet(id, out var definition))
                {
                    results.Add(new ParseResult(bufferedBytes.GetRange(0, 4).ToArray(), null, null, $"未登记帧 ID 0x{id:X2}，不能确定数据长度"));
                    bufferedBytes.RemoveAt(0);
                    continue;
                }

                var frameLength = LinFrameCodec.FixedFrameLength + definition!.DataLength;
                if (bufferedBytes.Count < frameLength)
                {
                    break;
                }

                var rawFrame = bufferedBytes.GetRange(0, frameLength).ToArray();
                if (LinFrameCodec.TryParse(rawFrame, definition, out var frame))
                {
                    results.Add(new ParseResult(rawFrame, frame, definition, null));
                    bufferedBytes.RemoveRange(0, frameLength);
                }
                else
                {
                    results.Add(new ParseResult(rawFrame, null, definition, "LIN checksum 校验失败"));
                    // 错误长度/丢字节可能使候选帧覆盖下一帧；只移动一个字节再搜索，以便恢复。
                    bufferedBytes.RemoveAt(0);
                }
            }
        }

        foreach (var result in results)
        {
            if (result.Frame is not null)
            {
                FrameReceived?.Invoke(result.Frame, result.Raw);
            }
            else
            {
                FrameRejected?.Invoke(result.Raw, result.Definition, result.Error!);
            }
        }
    }

    public void ExpirePartialFrame(TimeSpan inactivityTimeout)
    {
        if (inactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        }

        byte[] raw;
        lock (bufferedBytes)
        {
            if (bufferedBytes.Count == 0
                || Stopwatch.GetElapsedTime(lastAppendTimestamp) < inactivityTimeout)
            {
                return;
            }

            raw = bufferedBytes.ToArray();
            bufferedBytes.Clear();
        }

        FrameRejected?.Invoke(raw, null, "串口帧接收不完整，等待后续字节超时");
    }

    public void Reset()
    {
        lock (bufferedBytes)
        {
            bufferedBytes.Clear();
            lastAppendTimestamp = 0;
        }
    }

    private int FindHeader()
    {
        for (var index = 0; index < bufferedBytes.Count - 2; index++)
        {
            if (bufferedBytes[index] == LinFrameCodec.BreakFirstByte
                && bufferedBytes[index + 1] == LinFrameCodec.BreakSecondByte
                && bufferedBytes[index + 2] == LinFrameCodec.SyncByte)
            {
                return index;
            }
        }

        return -1;
    }

    private int GetPossibleHeaderPrefixLength()
    {
        if (bufferedBytes.Count >= 2
            && bufferedBytes[^2] == LinFrameCodec.BreakFirstByte
            && bufferedBytes[^1] == LinFrameCodec.BreakSecondByte)
        {
            return 2;
        }

        return bufferedBytes[^1] == LinFrameCodec.BreakFirstByte ? 1 : 0;
    }

    private void RejectPrefix(int count, string error, List<ParseResult> results)
    {
        if (count <= 0)
        {
            return;
        }

        results.Add(new ParseResult(bufferedBytes.GetRange(0, count).ToArray(), null, null, error));
        bufferedBytes.RemoveRange(0, count);
    }

    private sealed record ParseResult(byte[] Raw, LinFrame? Frame, LinFrameDefinition? Definition, string? Error);
}
