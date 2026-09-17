using WpfApp1.Models;

namespace WpfApp1.Services;

public static class ProtocolFrameCodec
{
    public const byte RequestHeaderFirstByte = 0xAA;
    public const byte RequestHeaderSecondByte = 0x55;
    public const byte ResponseHeaderFirstByte = 0x55;
    public const byte ResponseHeaderSecondByte = 0xAA;
    public const int FixedFrameLength = 7;

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

        var dataLength = (rawFrame[4] << 8) | rawFrame[5];
        if (rawFrame.Length != FixedFrameLength + dataLength
            || CalculateChecksum(rawFrame.Slice(0, 6 + dataLength)) != rawFrame[^1])
        {
            return false;
        }

        frame = new ProtocolFrame(rawFrame[2], rawFrame[3], rawFrame.Slice(6, dataLength).ToArray());
        return true;
    }

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
        var frame = new byte[FixedFrameLength + data.Length];
        frame[0] = headerFirstByte;
        frame[1] = headerSecondByte;
        frame[2] = command;
        frame[3] = subCommand;
        frame[4] = (byte)(data.Length >> 8);
        frame[5] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(6));
        frame[^1] = CalculateChecksum(frame.AsSpan(0, 6 + data.Length));
        return frame;
    }
}
