using System.Globalization;
using WpfApp1.Services;

namespace WpfApp1.Models;

public enum CommunicationLogDirection
{
    Sent,
    Received
}

/// <summary>
/// 一条完整的串口收发记录，同时提供按协议帧拆分后的显示字段。
/// </summary>
public sealed class CommunicationLogEntry
{
    private readonly ProtocolLogFields fields;

    public CommunicationLogEntry(
        DateTimeOffset timestamp,
        CommunicationLogDirection direction,
        string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        Timestamp = timestamp;
        Direction = direction;
        Data = data;
        fields = ProtocolLogFields.Parse(data, direction);
    }

    public DateTimeOffset Timestamp { get; }

    public CommunicationLogDirection Direction { get; }

    /// <summary>
    /// 原始十六进制字符串（不带空格），保留该字段以兼容现有日志服务。
    /// </summary>
    public string Data { get; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string DirectionText => Direction == CommunicationLogDirection.Sent ? "TX" : "RX";

    public string FrameTypeText => fields.FrameTypeText;

    public string BreakText => fields.BreakText;

    public string CommandText => fields.CommandText;

    public string SubCommandText => fields.SubCommandText;

    public string DataLengthText => fields.DataLengthText;

    public string PayloadText => fields.PayloadText;

    public string ChecksumText => fields.ChecksumText;

    public string ChecksumStatusText => fields.ChecksumStatusText;

    public string RawDataText => fields.RawDataText;

    private sealed record ProtocolLogFields(
        string FrameTypeText,
        string BreakText,
        string CommandText,
        string SubCommandText,
        string DataLengthText,
        string PayloadText,
        string ChecksumText,
        string ChecksumStatusText,
        string RawDataText)
    {
        public static ProtocolLogFields Parse(
            string hexData,
            CommunicationLogDirection direction)
        {
            if (!TryParseHex(hexData, out var bytes))
            {
                return Invalid(hexData, "十六进制格式错误");
            }

            var breakText = bytes.Length >= 2 ? FormatBytes(bytes.AsSpan(0, 2)) : "—";
            var commandText = bytes.Length >= 3 ? FormatByte(bytes[2]) : "—";
            var subCommandText = bytes.Length >= 4 ? FormatByte(bytes[3]) : "—";
            var hasLength = bytes.Length >= 5;
            var dataLength = hasLength ? bytes[4] : -1;
            var dataLengthText = hasLength
                ? $"{dataLength} (0x{dataLength:X2})"
                : "—";

            var payloadStart = Math.Min(5, bytes.Length);
            var payloadEnd = bytes.Length >= ProtocolFrameCodec.FixedFrameLength
                ? bytes.Length - 1
                : bytes.Length;
            var payloadText = payloadEnd > payloadStart
                ? FormatBytes(bytes.AsSpan(payloadStart, payloadEnd - payloadStart))
                : "—";

            var checksumText = bytes.Length >= ProtocolFrameCodec.FixedFrameLength
                ? FormatByte(bytes[^1])
                : "—";

            var expectedHeader = direction == CommunicationLogDirection.Sent
                ? (ProtocolFrameCodec.RequestHeaderFirstByte, ProtocolFrameCodec.RequestHeaderSecondByte)
                : (ProtocolFrameCodec.ResponseHeaderFirstByte, ProtocolFrameCodec.ResponseHeaderSecondByte);
            var hasExpectedHeader = bytes.Length >= 2
                && bytes[0] == expectedHeader.Item1
                && bytes[1] == expectedHeader.Item2;
            var hasExpectedLength = hasLength
                && bytes.Length == ProtocolFrameCodec.FixedFrameLength + dataLength;
            var hasValidChecksum = hasExpectedLength
                && bytes.Length >= ProtocolFrameCodec.FixedFrameLength
                && ProtocolFrameCodec.CalculateChecksum(bytes.AsSpan(2, bytes.Length - 3)) == bytes[^1];

            var checksumStatusText = bytes.Length < ProtocolFrameCodec.FixedFrameLength
                ? "不完整"
                : !hasExpectedHeader
                    ? "帧头错误"
                    : !hasExpectedLength
                        ? "长度错误"
                        : hasValidChecksum ? "通过" : "失败";

            var frameTypeText = checksumStatusText == "通过"
                ? GetFrameTypeText(bytes)
                : "错误";

            return new ProtocolLogFields(
                frameTypeText,
                breakText,
                commandText,
                subCommandText,
                dataLengthText,
                payloadText,
                checksumText,
                checksumStatusText,
                FormatBytes(bytes));
        }

        private static ProtocolLogFields Invalid(string rawData, string status)
        {
            return new ProtocolLogFields(
                "错误",
                "—",
                "—",
                "—",
                "—",
                "—",
                "—",
                status,
                rawData);
        }

        private static bool TryParseHex(string hexData, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromHexString(hexData);
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }

        private static string GetFrameTypeText(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 2)
            {
                return "未知";
            }

            if (bytes[0] == ProtocolFrameCodec.RequestHeaderFirstByte
                && bytes[1] == ProtocolFrameCodec.RequestHeaderSecondByte)
            {
                return "请求";
            }

            if (bytes[0] == ProtocolFrameCodec.ResponseHeaderFirstByte
                && bytes[1] == ProtocolFrameCodec.ResponseHeaderSecondByte)
            {
                return bytes.Length >= 4 && bytes[3] == 0xDF
                    ? "不支持"
                    : "响应";
            }

            return "未知";
        }

        private static string FormatByte(byte value)
        {
            return $"0x{value.ToString("X2", CultureInfo.InvariantCulture)}";
        }

        private static string FormatBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return "—";
            }

            return string.Join(
                " ",
                bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        }
    }
}
