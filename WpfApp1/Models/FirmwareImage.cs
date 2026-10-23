namespace WpfApp1.Models;

/// <summary>固件文件格式（按扩展名判定，读取时再次按内容校验）。</summary>
public enum FirmwareFileFormat : byte
{
    /// <summary>原始二进制映像（.bin）：整文件即映像，无地址信息。</summary>
    Binary,

    /// <summary>Intel HEX 记录文本（.hex）：数据记录按地址拼接为映像。</summary>
    IntelHex
}

/// <summary>
/// 一次成功加载后的固件映像（与界面解耦的数据模型）：
/// BIN 为整个文件字节，BaseAddress 固定 0；HEX 为数据记录从最低地址拼接的映像，
/// 段间空洞填 0xFF，BaseAddress 为最低数据地址。CRC32 按映像内容计算。
/// </summary>
public sealed record FirmwareImage(
    FirmwareFileFormat Format,
    string FileName,
    string FilePath,
    long FileSize,
    DateTimeOffset ModifiedAt,
    byte[] Data,
    long BaseAddress,
    uint Crc32,
    IntelHexSummary? HexSummary)
{
    /// <summary>归一化映像的字节数（HEX 与磁盘文件大小不同）。</summary>
    public long ImageSize => Data.Length;
}

/// <summary>Intel HEX 解析摘要（仅 HEX 文件提供）。</summary>
public sealed record IntelHexSummary(
    int TotalRecords,
    int DataRecords,
    long DataBytes,
    long LowestAddress,
    long HighestAddress,
    long? StartAddress);

/// <summary>十六进制预览行：绝对地址、偏移文本、16 字节十六进制文本、ASCII 文本。</summary>
public sealed record HexPreviewLine(
    long Address,
    string OffsetText,
    string HexText,
    string AsciiText);

/// <summary>固件页日志级别（与通信报文日志无关，仅本页操作记录）。</summary>
public enum FirmwareLogLevel : byte
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>固件页一条日志：时间 + 级别 + 标签（打开/读取/错误…）+ 消息。</summary>
public sealed record FirmwareLogEntry(
    DateTimeOffset Time,
    FirmwareLogLevel Level,
    string Tag,
    string Message);
