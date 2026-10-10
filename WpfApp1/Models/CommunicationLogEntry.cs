using WpfApp1.Services;

namespace WpfApp1.Models;

public enum CommunicationLogDirection
{
    Sent,
    Received
}

/// <summary>LIN 串口帧/帧头或接收错误记录；不再解释旧 CMD/SubCmd/LEN/XOR 协议。</summary>
public sealed class CommunicationLogEntry
{
    public CommunicationLogEntry(
        DateTimeOffset timestamp,
        CommunicationLogDirection direction,
        string data,
        LinFrameDefinition? definition = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Timestamp = timestamp;
        Direction = direction;
        Data = data;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(data);
        }
        catch (FormatException)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = error ?? "十六进制格式错误";
            RawDataText = data;
            return;
        }

        RawDataText = FormatBytes(bytes);
        BreakText = bytes.Length >= 2 ? FormatBytes(bytes.AsSpan(0, 2)) : "—";
        SyncText = bytes.Length >= 3 ? FormatByte(bytes[2]) : "—";
        ProtectedIdText = bytes.Length >= 4 ? FormatByte(bytes[3]) : "—";
        FrameIdText = bytes.Length >= 4 ? FormatByte((byte)(bytes[3] & 0x3F)) : "—";
        ChecksumTypeText = definition?.ChecksumType.ToString() ?? "—";

        var hasHeader = bytes.Length >= LinFrameCodec.HeaderLength
            && bytes[0] == LinFrameCodec.BreakFirstByte
            && bytes[1] == LinFrameCodec.BreakSecondByte
            && bytes[2] == LinFrameCodec.SyncByte;
        var validPid = hasHeader && LinFrameCodec.IsProtectedIdValid(bytes[3])
            && (bytes[3] & 0x3F) <= 0x3D;
        var isHeaderOnly = bytes.Length == LinFrameCodec.HeaderLength;
        if (!isHeaderOnly && bytes.Length >= LinFrameCodec.FixedFrameLength)
        {
            var dataLength = bytes.Length - LinFrameCodec.FixedFrameLength;
            DataLengthText = $"{dataLength} bytes";
            PayloadText = FormatBytes(bytes.AsSpan(LinFrameCodec.HeaderLength, dataLength));
            ChecksumText = FormatByte(bytes[^1]);
        }

        if (error is not null)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = error;
        }
        else if (!hasHeader)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = "帧头/Sync 错误或不完整";
        }
        else if (!validPid)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = "PID 错误或保留 ID";
        }
        else if (isHeaderOnly)
        {
            FrameTypeText = "帧头";
            ChecksumStatusText = "仅帧头，无数据校验";
        }
        else if (definition is null)
        {
            FrameTypeText = "未配置";
            ChecksumStatusText = "未登记帧定义";
        }
        else if (LinFrameCodec.TryParse(bytes, definition, out _))
        {
            FrameTypeText = definition.IsDiagnostic ? "诊断帧" : "应用帧";
            ChecksumStatusText = "通过";
        }
        else
        {
            FrameTypeText = "错误";
            ChecksumStatusText = bytes.Length != LinFrameCodec.FixedFrameLength + definition.DataLength
                ? "长度错误" : "LIN checksum 失败";
        }
    }

    public DateTimeOffset Timestamp { get; }
    public CommunicationLogDirection Direction { get; }
    public string Data { get; }
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string DirectionText => Direction == CommunicationLogDirection.Sent ? "TX" : "RX";
    public string FrameTypeText { get; } = "错误";
    public string BreakText { get; } = "—";
    public string SyncText { get; } = "—";
    public string FrameIdText { get; } = "—";
    public string ProtectedIdText { get; } = "—";
    public string DataLengthText { get; } = "—";
    public string PayloadText { get; } = "—";
    public string ChecksumText { get; } = "—";
    public string ChecksumTypeText { get; } = "—";
    public string ChecksumStatusText { get; } = "—";
    public string RawDataText { get; }

    private static string FormatByte(byte value) => $"0x{value:X2}";

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        return bytes.IsEmpty ? "—" : string.Join(" ", bytes.ToArray().Select(value => $"{value:X2}"));
    }
}
