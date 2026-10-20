using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// 组装"DATA + Checksum"格式的回复（应用与诊断共用一个字节缓存，WpfApp1诊断架构讨论记录.md §13）。
/// 回复没有 Break、Sync、PID 或长度字段，以已登记帧的 DataLength + 1 作为候选长度：
/// 应用回复先尝试增强校验逆运算（反算候选 PID → P0/P1 自洽 → 帧表匹配 → 复算校验）；
/// 失败后若为诊断候选长度（8 字节 PDU + 1 字节经典校验）且经典校验通过，
/// 输出为诊断候选（3C/3D 归属由业务上下文判定，入口不虚构诊断 PID）。
/// 两种校验可区分：经典校验帧经增强逆运算恒得 PID 0x00 且奇偶自洽失败；
/// 增强校验帧的经典校验值包含 PID 种子贡献，二者不会互相误判。
/// 经典校验也失败的字节组仍按错误/未知数据记录并逐字节重同步。
/// 所有时间戳使用与诊断模块同源的 <see cref="TimeProvider"/>。
/// </summary>
public sealed class LinReadReplyParser
{
    private readonly LinFrameCatalog catalog;
    private readonly TimeProvider timeProvider;
    private readonly List<byte> bufferedBytes = [];
    private readonly int[] candidateLengths;
    private readonly int[] diagnosticCandidateLengths;
    private long lastAppendTimestamp;

    public LinReadReplyParser(LinFrameCatalog catalog, TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        candidateLengths = catalog.Definitions
            .Select(definition => definition.DataLength + 1)
            .Distinct()
            .OrderBy(length => length)
            .ToArray();
        diagnosticCandidateLengths = catalog.Definitions
            .Where(definition => definition.IsDiagnostic)
            .Select(definition => definition.DataLength + 1)
            .Distinct()
            .ToArray();
    }

    public event Action<LinFrame, byte[]>? FrameReceived;
    public event Action<byte[], LinFrameDefinition?, string>? FrameRejected;

    /// <summary>诊断候选：原始 9 字节、8 字节 PDU、经典校验是否通过、软件接收单调时间。</summary>
    public event Action<byte[], byte[], bool, long>? DiagnosticFrameReceived;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var results = new List<ParseResult>();
        lock (bufferedBytes)
        {
            lastAppendTimestamp = timeProvider.GetTimestamp();
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
                    results.Add(new ParseResult([bufferedBytes[0]], null, null, "回复无法匹配已登记帧（反算 PID 无效或 ID 未登记）", null));
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
                    results.Add(new ParseResult(raw, frame, frame.Definition, null, null));
                    bufferedBytes.RemoveRange(0, length);
                    return length;
                }

                // 增强解析失败后，尝试诊断候选：长度匹配且经典校验通过。
                // 经典校验失败的字节组仍走"无法匹配"重同步路径并保留错误记录；
                // 受损回显无法可靠归属，由协调层的回显等待超时处理（§13.3）。
                if (Array.IndexOf(diagnosticCandidateLengths, length) >= 0
                    && LinFrameCodec.CalculateClassicChecksum(raw.AsSpan(0, length - 1)) == raw[length - 1])
                {
                    results.Add(new ParseResult(raw, null, null, null, raw[..(length - 1)]));
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
                || timeProvider.GetElapsedTime(lastAppendTimestamp) < inactivityTimeout)
            {
                return;
            }

            raw = bufferedBytes.ToArray();
            bufferedBytes.Clear();
        }

        FrameRejected?.Invoke(raw, null, "串口回复接收不完整，等待后续字节超时");
    }

    /// <summary>
    /// 解析器边界：丢弃当前不完整的共享缓存，并把残留字节作为一条拒绝记录交给日志
    /// （实施计划 §4.5）。用于诊断阶段切换，避免旧阶段的残缺候选被当成新阶段的响应。
    /// 与 <see cref="Append"/> 共用同一缓冲锁，保证不静默丢失数据。
    /// </summary>
    public void DiscardPartialCandidate(string reason)
    {
        byte[] raw;
        lock (bufferedBytes)
        {
            if (bufferedBytes.Count == 0)
            {
                return;
            }

            raw = bufferedBytes.ToArray();
            bufferedBytes.Clear();
            lastAppendTimestamp = 0;
        }

        FrameRejected?.Invoke(raw, null, reason);
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

        results.Add(new ParseResult(bufferedBytes.ToArray(), null, null, error, null));
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
            else if (result.DiagnosticPdu is not null)
            {
                DiagnosticFrameReceived?.Invoke(result.Raw, result.DiagnosticPdu, true, timeProvider.GetTimestamp());
            }
            else
            {
                FrameRejected?.Invoke(result.Raw, result.Definition, result.Error!);
            }
        }
    }

    private sealed record ParseResult(
        byte[] Raw,
        LinFrame? Frame,
        LinFrameDefinition? Definition,
        string? Error,
        byte[]? DiagnosticPdu);
}
