using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 组装"DATA + Checksum"格式的应用帧回复（读取与写入请求均会有回复）。
/// 回复没有 Break、Sync、PID 或长度字段，以已登记应用帧的 DataLength + 1
/// 作为候选长度；反算出候选 PID 并通过 P0/P1 奇偶自洽、帧表匹配后即视为
/// 解析出 ID，交由订阅方处理——有无对应业务回调由订阅方自行决定，
/// 无人认领的帧仅记录报文日志。候选长度全部失败且缓存已超过最长候选时，
/// 逐字节丢弃重同步。
/// </summary>
public sealed class LinReadReplyParser
{
    private readonly LinFrameCatalog catalog;
    private readonly List<byte> bufferedBytes = [];
    private readonly int[] candidateLengths;
    private long lastAppendTimestamp;

    public LinReadReplyParser(LinFrameCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        candidateLengths = catalog.Definitions
            .Where(definition => !definition.IsDiagnostic)
            .Select(definition => definition.DataLength + 1)
            .Distinct()
            .OrderBy(length => length)
            .ToArray();
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
            if (candidateLengths.Length == 0)
            {
                if (bufferedBytes.Count > 0)
                {
                    RejectAll(results, "未登记任何从节点应用帧，无法解析回复");
                }

                FlushResults(results);
                return;
            }

            var shortestLength = candidateLengths[0];
            var longestLength = candidateLengths[^1];
            while (bufferedBytes.Count >= shortestLength)
            {
                var consumed = TryParseFromBuffer();
                if (consumed > 0)
                {
                    continue;
                }

                if (bufferedBytes.Count >= longestLength)
                {
                    // 已超过最长候选仍无法匹配：噪声或失步，丢弃 1 字节后重试，保留后续对齐机会。
                    results.Add(new ParseResult([bufferedBytes[0]], null, null, "回复无法匹配已登记帧（反算 PID 无效或 ID 未登记）"));
                    bufferedBytes.RemoveAt(0);
                    continue;
                }

                // 可能是更长回复的前缀，等待更多字节。
                break;
            }
        }

        FlushResults(results);
        return;

        int TryParseFromBuffer()
        {
            foreach (var length in candidateLengths)
            {
                if (bufferedBytes.Count < length)
                {
                    break;
                }

                var raw = bufferedBytes.GetRange(0, length).ToArray();
                if (TryParse(raw, out var frame))
                {
                    results.Add(new ParseResult(raw, frame, frame.Definition, null));
                    bufferedBytes.RemoveRange(0, length);
                    return length;
                }
            }

            return 0;
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

        FrameRejected?.Invoke(raw, null, "串口回复接收不完整，等待后续字节超时");
    }

    public void Reset()
    {
        lock (bufferedBytes)
        {
            bufferedBytes.Clear();
            lastAppendTimestamp = 0;
        }
    }

    private bool TryParse(byte[] raw, out LinFrame frame)
    {
        frame = null!;
        if (!LinFrameCodec.TryRecoverProtectedId(raw, out var protectedId))
        {
            return false;
        }

        // P0/P1 奇偶自洽：数据损坏时大概率在此被过滤。
        if (!LinFrameCodec.IsProtectedIdValid(protectedId))
        {
            return false;
        }

        var id = (byte)(protectedId & 0x3F);
        // 查帧表即"查找相关 ID"：已登记即接受（读取与写入帧均可能有回复），
        // 是否有业务回调由订阅方决定，无人认领仅记日志。
        if (!catalog.TryGet(id, out var definition) || definition!.IsDiagnostic)
        {
            return false;
        }

        // 用候选 PID 复算 Checksum 作为一致性防护（覆盖模 255 归一化的边界情况）。
        var data = raw.AsSpan(0, raw.Length - 1);
        if (LinFrameCodec.CalculateChecksum(protectedId, data, definition.ChecksumType) != raw[^1])
        {
            return false;
        }

        frame = new LinFrame(definition, protectedId, data);
        return true;
    }

    private void RejectAll(List<ParseResult> results, string error)
    {
        if (bufferedBytes.Count == 0)
        {
            return;
        }

        results.Add(new ParseResult(bufferedBytes.ToArray(), null, null, error));
        bufferedBytes.Clear();
    }

    private void FlushResults(List<ParseResult> results)
    {
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

    private sealed record ParseResult(byte[] Raw, LinFrame? Frame, LinFrameDefinition? Definition, string? Error);
}
