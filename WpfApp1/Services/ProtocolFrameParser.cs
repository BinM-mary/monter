using WpfApp1.Models;

namespace WpfApp1.Services;

public sealed class ProtocolFrameParser
{
    private readonly List<byte> bufferedBytes = [];

    public event Action<ProtocolFrame, byte[]>? FrameReceived;

    public event Action<byte[]>? FrameRejected;

    public void Append(ReadOnlySpan<byte> receivedBytes)
    {
        if (receivedBytes.IsEmpty)
        {
            return;
        }

        var parsedFrames = new List<(ProtocolFrame? Frame, byte[] RawFrame)>();
        lock (bufferedBytes)
        {
            bufferedBytes.AddRange(receivedBytes.ToArray());

            while (TryTakeRawFrame(out var rawFrame))
            {
                if (ProtocolFrameCodec.TryParseResponse(rawFrame, out var frame))
                {
                    parsedFrames.Add((frame, rawFrame));
                }
                else
                {
                    // 无效帧不参与协议处理，但保留原始字节供日志诊断。
                    parsedFrames.Add((null, rawFrame));
                }
            }
        }

        foreach (var parsedFrame in parsedFrames)
        {
            if (parsedFrame.Frame is not null)
            {
                FrameReceived?.Invoke(parsedFrame.Frame, parsedFrame.RawFrame);
            }
            else
            {
                FrameRejected?.Invoke(parsedFrame.RawFrame);
            }
        }
    }

    public void Reset()
    {
        lock (bufferedBytes)
        {
            bufferedBytes.Clear();
        }
    }

    private bool TryTakeRawFrame(out byte[] rawFrame)
    {
        rawFrame = [];

        var headerIndex = FindResponseHeader();
        if (headerIndex < 0)
        {
            KeepPossibleHeaderPrefix();
            return false;
        }

        if (headerIndex > 0)
        {
            bufferedBytes.RemoveRange(0, headerIndex);
        }

        if (bufferedBytes.Count < ProtocolFrameCodec.FixedFrameLength)
        {
            return false;
        }

        var dataLength = bufferedBytes[4];
        var frameLength = ProtocolFrameCodec.FixedFrameLength + dataLength;
        if (bufferedBytes.Count < frameLength)
        {
            return false;
        }

        rawFrame = bufferedBytes.GetRange(0, frameLength).ToArray();
        bufferedBytes.RemoveRange(0, frameLength);
        return true;
    }

    private int FindResponseHeader()
    {
        for (var index = 0; index < bufferedBytes.Count - 1; index++)
        {
            if (bufferedBytes[index] == ProtocolFrameCodec.ResponseHeaderFirstByte
                && bufferedBytes[index + 1] == ProtocolFrameCodec.ResponseHeaderSecondByte)
            {
                return index;
            }
        }

        return -1;
    }

    private void KeepPossibleHeaderPrefix()
    {
        if (bufferedBytes.Count > 0
            && bufferedBytes[^1] == ProtocolFrameCodec.ResponseHeaderFirstByte)
        {
            bufferedBytes.RemoveRange(0, bufferedBytes.Count - 1);
            return;
        }

        bufferedBytes.Clear();
    }
}
