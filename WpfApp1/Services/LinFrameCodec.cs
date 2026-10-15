using WpfApp1.Models;

namespace WpfApp1.Services;

public static class LinFrameCodec
{
    public const byte BreakFirstByte = 0x55;
    public const byte BreakSecondByte = 0xAA;
    public const byte SyncByte = 0x55;
    public const int HeaderLength = 4;
    public const int FixedFrameLength = HeaderLength + 1;
    public const int ReadRequestLength = HeaderLength + 1;
    public const byte ReadRequestFlag = 0x00;
    public const byte WriteRequestFlag = 0x08;

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

    /// <summary>构造读取请求：55 AA 55 PID 00。末尾 00 为双方串口接口约定的读取标识，不是校验值。</summary>
    public static byte[] CreateReadRequest(byte id)
    {
        if (id > 0x3D)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "0x3E/0x3F 为保留 ID。");
        }

        var raw = new byte[ReadRequestLength];
        CreateHeaderBytes(id).CopyTo(raw, 0);
        raw[HeaderLength] = ReadRequestFlag;
        return raw;
    }

    /// <summary>
    /// 构造写入应用帧：55 AA 55 PID 08 DATA CS。
    /// 08 为双方串口接口约定的写入标识（不参与校验）；
    /// Checksum 与 LIN 增强型校验一致：以 PID（由原始 ID 计算）为种子累加全部 DATA，
    /// 进位折叠后取反，与中间件 Lin_CheckSum(id, pData, len) 结果相同。
    /// </summary>
    public static byte[] CreateWriteRequest(byte id, ReadOnlySpan<byte> data)
    {
        if (id > 0x3D)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "0x3E/0x3F 为保留 ID。");
        }

        var raw = new byte[HeaderLength + 1 + data.Length + 1];
        CreateHeaderBytes(id).CopyTo(raw, 0);
        raw[HeaderLength] = WriteRequestFlag;
        data.CopyTo(raw.AsSpan(HeaderLength + 1));
        raw[^1] = CalculateWriteChecksum(id, data);
        return raw;
    }

    /// <summary>
    /// 写入帧 Checksum：与标准增强型校验相同，以 PID（由原始 6 位 ID 计算得到）为种子，
    /// 只累加全部 DATA（不含写入标识 08），每步进位折叠（&gt;0xFF 减 0xFF），最后取反。
    /// 中间件 Lin_CheckSum 内部同样以 Lin_CheckPID(id) 为种子，结果一致。
    /// </summary>
    public static byte CalculateWriteChecksum(byte id, ReadOnlySpan<byte> data)
    {
        if (id > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "帧 ID 为 6 位，不能传入 PID。");
        }

        var sum = (int)CalculateProtectedId(id);
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

    /// <summary>
    /// 从"DATA + Checksum"回复中反算 PID。算法为增强型校验的逆运算：
    /// foldedSum = (~CS) &amp; 0xFF；candidatePid = 非负模 255(foldedSum - dataSum)。
    /// 结果需另行通过 P0/P1 奇偶自洽与帧表匹配才可接受。
    /// </summary>
    public static bool TryRecoverProtectedId(ReadOnlySpan<byte> rawReply, out byte protectedId)
    {
        protectedId = 0;
        if (rawReply.Length < 2)
        {
            return false;
        }

        var checksum = rawReply[^1];
        var data = rawReply[..^1];
        var foldedSum = ~checksum & 0xFF;
        int dataSum = 0;
        foreach (var value in data)
        {
            dataSum += value;
        }

        var candidate = ((foldedSum - dataSum) % 255 + 255) % 255;
        protectedId = (byte)candidate;
        return true;
    }

    public static byte[] CreateFrame(LinFrameDefinition definition, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (data.Length != definition.DataLength)
        {
            throw new ArgumentException($"帧 0x{definition.Id:X2} 必须包含 {definition.DataLength} 个数据字节。", nameof(data));
        }

        var rawFrame = new byte[FixedFrameLength + data.Length];
        CreateHeaderBytes(definition.Id).CopyTo(rawFrame, 0);
        data.CopyTo(rawFrame.AsSpan(HeaderLength));
        rawFrame[^1] = CalculateChecksum(rawFrame[3], data, definition.ChecksumType);
        return rawFrame;
    }

    private static byte[] CreateHeaderBytes(byte id)
    {
        if (id > 0x3D)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "0x3E/0x3F 为保留 ID。");
        }

        return [BreakFirstByte, BreakSecondByte, SyncByte, CalculateProtectedId(id)];
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
