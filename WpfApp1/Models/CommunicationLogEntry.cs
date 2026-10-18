using WpfApp1.Services;

namespace WpfApp1.Models;

public enum CommunicationLogDirection
{
    Sent,
    Received
}

/// <summary>
/// 报文记录。区分四种 LIN 串口形态：读取请求（55 AA 55 PID 00）、
/// 读取回复（DATA + Checksum，PID/ID 为反算恢复值，显示时不加标注）、
/// 写入帧（55 AA 55 PID 08 DATA CS，Checksum 以原始 ID 为种子累加 08+DATA）、
/// 主节点发布完整帧（55 AA 55 PID DATA CS）；无法识别的按错误条目显示原始字节。
/// </summary>
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
        var hasHeader = bytes.Length >= LinFrameCodec.HeaderLength
            && bytes[0] == LinFrameCodec.BreakFirstByte
            && bytes[1] == LinFrameCodec.BreakSecondByte
            && bytes[2] == LinFrameCodec.SyncByte;

        if (error is not null)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = error;
            return;
        }

        // 发送的读取请求：55 AA 55 PID 00。末尾 00 是读取标识，不是校验值，不显示为 Checksum。
        if (direction == CommunicationLogDirection.Sent
            && hasHeader
            && bytes.Length == LinFrameCodec.ReadRequestLength
            && bytes[4] == LinFrameCodec.ReadRequestFlag)
        {
            BreakText = FormatBytes(bytes.AsSpan(0, 2));
            SyncText = FormatByte(bytes[2]);
            ProtectedIdText = FormatByte(bytes[3]);
            FrameIdText = FormatByte((byte)(bytes[3] & 0x3F));
            FrameTypeText = "读取请求";
            DataLengthText = "0 bytes";
            ChecksumStatusText = "读取标识 00，无校验";
            return;
        }

        // 发送的写入帧：55 AA 55 PID 08 DATA CS。08 是写入标识；
        // Checksum 为标准增强型：以 PID（由原始 ID 计算）为种子，只累加 DATA（不含 08）。
        if (direction == CommunicationLogDirection.Sent
            && hasHeader
            && bytes.Length >= LinFrameCodec.HeaderLength + 2
            && bytes[4] == LinFrameCodec.WriteRequestFlag)
        {
            BreakText = FormatBytes(bytes.AsSpan(0, 2));
            SyncText = FormatByte(bytes[2]);
            ProtectedIdText = FormatByte(bytes[3]);
            FrameIdText = FormatByte((byte)(bytes[3] & 0x3F));
            FrameTypeText = "写入帧";
            var dataLength = bytes.Length - LinFrameCodec.HeaderLength - 2;
            DataLengthText = $"{dataLength} bytes";
            var payload = bytes.AsSpan(LinFrameCodec.HeaderLength + 1, dataLength);
            PayloadText = FormatBytes(payload);

            // 诊断请求（3C）：Checksum 为经典校验，只累加 PDU，不以 PID 为种子（§7.1）。
            if (definition is { IsDiagnostic: true })
            {
                FrameTypeText = "诊断请求";
                ChecksumTypeText = "经典";
                ChecksumText = FormatByte(bytes[^1]);
                ChecksumStatusText = LinFrameCodec.CalculateClassicChecksum(payload) == bytes[^1]
                    ? "通过"
                    : "校验失败";
                return;
            }

            ChecksumText = FormatByte(bytes[^1]);
            ChecksumTypeText = "增强";
            ChecksumStatusText = LinFrameCodec.CalculateWriteChecksum(
                (byte)(bytes[3] & 0x3F),
                bytes.AsSpan(LinFrameCodec.HeaderLength + 1, dataLength)) == bytes[^1]
                ? "通过"
                : "校验失败";
            return;
        }

        // 诊断回包：8 字节 PDU + 经典校验，无帧头，不能反算 PID；
        // 3C/3D 归属与 NAD 检查由业务上下文完成，此处不填入假定的诊断 ID/PID（§13.6）。
        if (!hasHeader && definition is { IsDiagnostic: true } && bytes.Length == LinFrameCodec.DiagnosticWireLength)
        {
            var pdu = bytes.AsSpan(0, LinFrameCodec.DiagnosticPduLength);
            FrameTypeText = "诊断数据";
            DataLengthText = $"{LinFrameCodec.DiagnosticPduLength} bytes";
            PayloadText = FormatBytes(pdu);
            ChecksumText = FormatByte(bytes[^1]);
            ChecksumTypeText = "经典";
            ChecksumStatusText = LinFrameCodec.CalculateClassicChecksum(pdu) == bytes[^1]
                ? "通过"
                : "校验失败";
            return;
        }

        // 接收的读取回复：只有 DATA + Checksum。PID 与 ID 是反算恢复值。
        if (!hasHeader && definition is not null && bytes.Length >= 2)
        {
            if (LinFrameCodec.TryRecoverProtectedId(bytes, out var protectedId)
                && LinFrameCodec.CalculateChecksum(
                    protectedId,
                    bytes.AsSpan(0, bytes.Length - 1),
                    definition.ChecksumType) == bytes[^1])
            {
                FrameTypeText = "回复帧";
                ProtectedIdText = FormatByte(protectedId);
                FrameIdText = FormatByte((byte)(protectedId & 0x3F));
                DataLengthText = $"{bytes.Length - 1} bytes";
                PayloadText = FormatBytes(bytes.AsSpan(0, bytes.Length - 1));
                ChecksumText = FormatByte(bytes[^1]);
                ChecksumTypeText = definition.ChecksumType.ToString();
                ChecksumStatusText = "通过";
                return;
            }

            FrameTypeText = "错误";
            ChecksumStatusText = "无法从 DATA/Checksum 恢复有效 PID";
            return;
        }

        if (!hasHeader)
        {
            FrameTypeText = "错误";
            ChecksumStatusText = "无 LIN 帧头，且不构成有效回复";
            return;
        }

        // 主节点发布的完整帧：55 AA 55 PID DATA CS。
        BreakText = FormatBytes(bytes.AsSpan(0, 2));
        SyncText = FormatByte(bytes[2]);
        ProtectedIdText = FormatByte(bytes[3]);
        FrameIdText = FormatByte((byte)(bytes[3] & 0x3F));
        ChecksumTypeText = definition?.ChecksumType.ToString() ?? "—";

        var validPid = LinFrameCodec.IsProtectedIdValid(bytes[3])
            && (bytes[3] & 0x3F) <= 0x3D;
        var isHeaderOnly = bytes.Length == LinFrameCodec.HeaderLength;
        if (!isHeaderOnly && bytes.Length >= LinFrameCodec.FixedFrameLength)
        {
            var dataLength = bytes.Length - LinFrameCodec.FixedFrameLength;
            DataLengthText = $"{dataLength} bytes";
            PayloadText = FormatBytes(bytes.AsSpan(LinFrameCodec.HeaderLength, dataLength));
            ChecksumText = FormatByte(bytes[^1]);
        }

        if (!validPid)
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
