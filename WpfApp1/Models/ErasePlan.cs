using System.Buffers.Binary;
using System.Globalization;

namespace WpfApp1.Models;

/// <summary>
/// 第十步擦除参数的不可变快照（架构/OTA升级流程.md §10.3）：
/// 起始地址与字节长度都来自本次加载的固件映像，一经创建不再回读界面或重新读取文件；
/// 第十步提交后与后续下载步骤共用同一份范围，避免两步之间因换文件或改 NAD 而漂移。
/// </summary>
public sealed record ErasePlan
{
    /// <summary>RoutineControl 服务号（§10.2）。</summary>
    public const byte RoutineControlServiceId = 0x31;

    /// <summary>StartRoutine 子功能（§10.2）。</summary>
    public const byte StartRoutineSubFunction = 0x01;

    /// <summary>擦除例程标识 RID（§10.2 解析图片得到 FF 00）。</summary>
    public const ushort EraseRoutineId = 0xFF00;

    /// <summary>
    /// 完整 UDS 请求长度：服务 1 + 子功能 1 + RID 2 + 起始地址 4 + 字节长度 4（§10.2）。
    /// 固定 12 字节，必然走多帧（FF + 两条 CF）。
    /// </summary>
    public const int RequestLength = 12;

    private ErasePlan(uint startAddress, uint length, FirmwareFileFormat format, string firmwareFileName)
    {
        StartAddress = startAddress;
        Length = length;
        Format = format;
        FirmwareFileName = firmwareFileName;
    }

    /// <summary>擦除起始地址（大端四字节写入请求），来自固件映像的最低数据地址。</summary>
    public uint StartAddress { get; }

    /// <summary>擦除字节长度（大端四字节写入请求）；不是结束地址。</summary>
    public uint Length { get; }

    /// <summary>参数来源的固件格式（当前只有 HEX 能给出确定的起始地址）。</summary>
    public FirmwareFileFormat Format { get; }

    /// <summary>生成该参数快照的固件文件名（仅供日志追溯，不参与报文）。</summary>
    public string FirmwareFileName { get; }

    /// <summary>结束边界（不含），即 start + length（§10.3）。</summary>
    public uint EndAddressExclusive => StartAddress + Length;

    /// <summary>最后一个字节地址，即 start + length - 1（§10.3）。</summary>
    public uint EndAddressInclusive => EndAddressExclusive - 1;

    /// <summary>起始地址文本，例如 0x00005400。</summary>
    public string StartAddressText => "0x" + StartAddress.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>结束边界文本（不含），例如 0x0000B39C。</summary>
    public string EndAddressExclusiveText => "0x" + EndAddressExclusive.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>长度文本，例如 24,476 字节。</summary>
    public string LengthText => Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节";

    /// <summary>日志用的范围描述：起始 – 最后字节（长度）。</summary>
    public string RangeText =>
        $"{StartAddressText} – 0x{EndAddressInclusive.ToString("X8", CultureInfo.InvariantCulture)}" +
        $"（起始 {StartAddressText}，长度 {LengthText}，结束边界 {EndAddressExclusiveText} 不含）";

    /// <summary>
    /// 生成 12 字节 UDS 请求：31 01 FF 00 + 大端四字节起始地址 + 大端四字节字节长度（§10.2、§10.3）。
    /// 不追加格式标识 44，也不把结束地址当长度（§10.3）。
    /// </summary>
    public byte[] ToUdsRequest()
    {
        var uds = new byte[RequestLength];
        uds[0] = RoutineControlServiceId;
        uds[1] = StartRoutineSubFunction;
        BinaryPrimitives.WriteUInt16BigEndian(uds.AsSpan(2, 2), EraseRoutineId);
        BinaryPrimitives.WriteUInt32BigEndian(uds.AsSpan(4, 4), StartAddress);
        BinaryPrimitives.WriteUInt32BigEndian(uds.AsSpan(8, 4), Length);
        return uds;
    }

    /// <summary>
    /// 按固件映像生成擦除参数（§10.3）。
    /// HEX：起始地址取归一化映像的最低数据地址，长度取 <see cref="FirmwareImage.ImageSize"/>
    /// （含段间 0xFF 空洞，不用磁盘文件大小）。
    /// BIN：原始文件不含地址信息，现有模型 BaseAddress=0 只是加载默认值，
    /// 设备擦除地址未确认，因此不生成参数、不发送猜测报文。
    /// </summary>
    public static bool TryCreate(FirmwareImage? image, out ErasePlan? plan, out string? error)
    {
        plan = null;
        error = null;

        if (image is null)
        {
            error = "尚未加载固件文件，无法确定擦除范围。";
            return false;
        }

        if (image.Format == FirmwareFileFormat.Binary)
        {
            error = "BIN 文件不含烧录起始地址（模型 BaseAddress=0 是加载默认值，不是设备 APP 地址），" +
                "擦除地址来源未确认，本次不发送擦除请求。";
            return false;
        }

        if (image.BaseAddress < 0 || image.BaseAddress > uint.MaxValue)
        {
            error = $"固件起始地址 {image.BaseAddress} 超出 32 位地址范围，无法写入擦除请求。";
            return false;
        }

        var length = image.ImageSize;
        if (length <= 0)
        {
            error = "固件映像为空（长度为 0），没有可擦除的范围。";
            return false;
        }

        if (length > uint.MaxValue)
        {
            error = $"固件映像长度 {length} 超出四字节长度字段范围，无法写入擦除请求。";
            return false;
        }

        var start = (uint)image.BaseAddress;
        var size = (uint)length;

        // 长度是字节数而不是结束地址，但仍需确认 start + length 不越过 32 位地址空间（§10.3 第 1 条）。
        if ((ulong)start + size > (ulong)uint.MaxValue + 1)
        {
            error = $"擦除范围 {start:X8} + {size:X} 字节越过 32 位地址空间，参数非法。";
            return false;
        }

        plan = new ErasePlan(start, size, image.Format, image.FileName);
        return true;
    }
}
