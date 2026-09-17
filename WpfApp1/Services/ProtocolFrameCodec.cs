using WpfApp1.Models;

namespace WpfApp1.Services;

public static class ProtocolFrameCodec
{
    public const byte RequestHeaderFirstByte = 0xAA;
    public const byte RequestHeaderSecondByte = 0x55;
    public const byte ResponseHeaderFirstByte = 0x55;
    public const byte ResponseHeaderSecondByte = 0xAA;
    // 固定字段：Break(2) + Cmd(1) + SubCmd(1) + Length(1) + Checksum(1)。
    // Data 为空时，整帧长度就是 6 字节，不附加任何占位字节。
    public const int FixedFrameLength = 6;

    public static byte[] CreateRequest(byte command, byte subCommand, ReadOnlySpan<byte> data)
    {
        return CreateFrame(RequestHeaderFirstByte, RequestHeaderSecondByte, command, subCommand, data);
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> rawFrame, out ProtocolFrame? frame)
    {
        frame = null;
        if (rawFrame.Length < FixedFrameLength
            || rawFrame[0] != ResponseHeaderFirstByte
            || rawFrame[1] != ResponseHeaderSecondByte)
        {
            return false;
        }

        var dataLength = rawFrame[4];
        if (rawFrame.Length != FixedFrameLength + dataLength
            || CalculateChecksum(rawFrame.Slice(2, 3 + dataLength)) != rawFrame[^1])
        {
            return false;
        }

        frame = new ProtocolFrame(rawFrame[2], rawFrame[3], rawFrame.Slice(5, dataLength).ToArray());
        return true;
    }

    // commandToData 从 CMD 开始，覆盖 CMD、SubCmd、Length 和 Data，不包含 Break。
    public static byte CalculateChecksum(ReadOnlySpan<byte> commandToData)
    {
        byte checksum = 0;
        foreach (var value in commandToData)
        {
            checksum ^= value;
        }

        return checksum;
    }

    private static byte[] CreateFrame(
        byte headerFirstByte,
        byte headerSecondByte,
        byte command,
        byte subCommand,
        ReadOnlySpan<byte> data)
    {
        if (data.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                "数据区长度不能超过 255 字节。"
            );
        }

        var frame = new byte[FixedFrameLength + data.Length];
        frame[0] = headerFirstByte;
        frame[1] = headerSecondByte;
        frame[2] = command;
        frame[3] = subCommand;
        frame[4] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(5));
        frame[^1] = CalculateChecksum(frame.AsSpan(2, 3 + data.Length));
        return frame;
    }
}
