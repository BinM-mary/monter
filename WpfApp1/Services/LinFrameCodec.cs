using WpfApp1.Models;

namespace WpfApp1.Services;

public static class LinFrameCodec
{
    public const byte BreakFirstByte = 0x55;
    public const byte BreakSecondByte = 0xAA;
    public const byte SyncByte = 0x55;
    public const int HeaderLength = 4;
    public const int FixedFrameLength = HeaderLength + 1;

    public static byte CalculateProtectedId(byte id)
    {
        if (id > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "帧 ID 为 6 位，不能传入 PID。");
        }

        var p0 = (id ^ (id >> 1) ^ (id >> 2) ^ (id >> 4)) & 1;
        var p1 = (~((id >> 1) ^ (id >> 3) ^ (id >> 4) ^ (id >> 5))) & 1;
        return (byte)(id | (p0 << 6) | (p1 << 7));
    }

    public static bool IsProtectedIdValid(byte protectedId)
    {
        return CalculateProtectedId((byte)(protectedId & 0x3F)) == protectedId;
    }

    public static byte[] CreateHeader(byte id)
    {
        if (id > 0x3D)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "0x3E/0x3F 为保留 ID。");
        }

        return [BreakFirstByte, BreakSecondByte, SyncByte, CalculateProtectedId(id)];
    }

    public static byte[] CreateFrame(LinFrameDefinition definition, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (data.Length != definition.DataLength)
        {
            throw new ArgumentException($"帧 0x{definition.Id:X2} 必须包含 {definition.DataLength} 个数据字节。", nameof(data));
        }

        var rawFrame = new byte[FixedFrameLength + data.Length];
        CreateHeader(definition.Id).CopyTo(rawFrame, 0);
        data.CopyTo(rawFrame.AsSpan(HeaderLength));
        rawFrame[^1] = CalculateChecksum(rawFrame[3], data, definition.ChecksumType);
        return rawFrame;
    }

    public static byte CalculateChecksum(
        byte protectedId,
        ReadOnlySpan<byte> data,
        LinChecksumType checksumType)
    {
        if (!Enum.IsDefined(checksumType))
        {
            throw new ArgumentOutOfRangeException(nameof(checksumType));
        }

        if ((protectedId & 0x3F) >= 0x3C && checksumType != LinChecksumType.Classic)
        {
            throw new ArgumentException("诊断帧必须使用 classic checksum。", nameof(checksumType));
        }

        var sum = checksumType == LinChecksumType.Enhanced ? protectedId : 0;
        foreach (var value in data)
        {
            sum += value;
            if (sum > 0xFF)
            {
                sum -= 0xFF;
            }
        }

        return (byte)(~sum & 0xFF);
    }

    public static bool TryParse(
        ReadOnlySpan<byte> rawFrame,
        LinFrameDefinition definition,
        out LinFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(definition);
        frame = null;
        if (rawFrame.Length != FixedFrameLength + definition.DataLength
            || rawFrame[0] != BreakFirstByte
            || rawFrame[1] != BreakSecondByte
            || rawFrame[2] != SyncByte
            || rawFrame[3] != CalculateProtectedId(definition.Id))
        {
            return false;
        }

        var data = rawFrame.Slice(HeaderLength, definition.DataLength);
        if (CalculateChecksum(rawFrame[3], data, definition.ChecksumType) != rawFrame[^1])
        {
            return false;
        }

        frame = new LinFrame(definition, rawFrame[3], data);
        return true;
    }
}
