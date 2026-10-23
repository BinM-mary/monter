using System.Globalization;

namespace WpfApp1.Models;

/// <summary>
/// 第十一至第十四步共用的固件传输快照（架构/OTA升级流程.md §11.1、§12.5、§14.6）：
/// 起始地址与长度直接取自第十步已提交的 <see cref="ErasePlan"/>，固件字节与 CRC32 取自同一份
/// 加载时映像的副本，一经创建不再回读界面、不再重新读取文件。
/// <para>
/// 目的：申请下载（34）、分块传输（36）、结束传输（37）与校验（31 01 02 02）必须使用**同一份**
/// 地址、大小与数据；任何一步都不允许重新从界面取参数，也不允许中途换文件。
/// </para>
/// </summary>
public sealed class FirmwareTransferPlan
{
    /// <summary>本阶段每包固件数据上限（§11.2、§12.3）：128 字节，不含 SID 与块号。</summary>
    public const int MaxBlockPayload = 128;

    /// <summary>
    /// 本项目 LIN-TP 的 UDS 长度上限（12 位 PCI，4095）；设备返回的最大块长超过此值时，
    /// 单条 36 请求无法编码，按格式错误处理，不静默截断（§11.2）。
    /// </summary>
    public const int MaxBlockLengthLimit = DiagnosticMultiFrameOptions.EncodingLengthLimit;

    /// <summary>
    /// **临时测试开关，默认 0 表示不启用（正常流程）**：非 0 时把快照的 CRC32 与该值按位异或，
    /// 用于构造一个错误的校验值发给设备，验证设备侧校验失败路径（§14.4 的 `71 01 02 02 01`）。
    /// <para>
    /// 取值范围示例：`0x00000001` 只翻转最低位；`0xFFFFFFFF` 取反，与正确值差异最明显。
    /// </para>
    /// <para>
    /// 只影响**发给设备**的校验值：界面上「校验值」读的是加载时 <see cref="FirmwareImage.Crc32"/>，
    /// 仍是正确值，因此界面与 `OTA-11/OTA-14` 日志里的 CRC32 可以直接对照，确认发出去的确实不同。
    /// 测完必须改回 `0`。
    /// </para>
    /// </summary>
    public const uint Crc32TestXor = 0x00000000u;

    private FirmwareTransferPlan(
        uint startAddress,
        byte[] data,
        uint crc32,
        FirmwareFileFormat format,
        string firmwareFileName)
    {
        StartAddress = startAddress;
        Data = data;
        Crc32 = crc32;
        Format = format;
        FirmwareFileName = firmwareFileName;
    }

    /// <summary>下载与校验的起始地址（大端四字节写入请求），与第十步擦除范围同源。</summary>
    public uint StartAddress { get; }

    /// <summary>固件字节长度（不是结束地址）；等于 <see cref="Data"/> 的长度。</summary>
    public uint Length => (uint)Data.Length;

    /// <summary>
    /// 参与传输与 CRC32 计算的固件字节（加载时归一化映像的副本）：
    /// HEX 段间空洞已填 0xFF，不含 HEX 文本的冒号、记录头或字符（§11.1）。
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 本快照数据的 CRC32（IEEE 802.3，多项式 0xEDB88320、初值 0xFFFFFFFF、最终取反，§14.6）。
    /// 在创建快照时按 <see cref="Data"/> 重新计算，保证校验值与实际发送的字节来自同一份数据。
    /// 临时测试开关 <see cref="Crc32TestXor"/> 非 0 时该值会被异或扰动（仅用于验证设备侧失败路径）。
    /// </summary>
    public uint Crc32 { get; }

    public FirmwareFileFormat Format { get; }

    /// <summary>生成快照的固件文件名（仅供日志追溯，不参与报文）。</summary>
    public string FirmwareFileName { get; }

    /// <summary>结束边界（不含），即 start + length。</summary>
    public uint EndAddressExclusive => StartAddress + Length;

    /// <summary>起始地址文本，例如 0x00005400。</summary>
    public string StartAddressText => "0x" + StartAddress.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>长度文本，例如 24,476 字节。</summary>
    public string LengthText => Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节";

    /// <summary>CRC32 文本（数值表示；报文按高字节先发送，§14.6）。</summary>
    public string Crc32Text => "0x" + Crc32.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>日志用的范围描述。</summary>
    public string RangeText =>
        $"{StartAddressText} – 0x{(EndAddressExclusive - 1).ToString("X8", CultureInfo.InvariantCulture)}" +
        $"（长度 {LengthText}，结束边界不含）";

    /// <summary>
    /// 按设备返回的每包容量计算总块数；容量非法时抛 <see cref="ArgumentOutOfRangeException"/>，
    /// 避免把 0 容量算成无穷循环。
    /// </summary>
    public int GetBlockCount(int payloadCapacity)
    {
        if (payloadCapacity is < 1 or > MaxBlockPayload)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadCapacity),
                $"每包容量必须为 1～{MaxBlockPayload} 字节，实际 {payloadCapacity}。");
        }

        return (int)((Length + (uint)payloadCapacity - 1) / (uint)payloadCapacity);
    }

    /// <summary>
    /// 创建传输快照（§11.1、§14.6）：地址与长度复用第十步的 <paramref name="range"/>，
    /// 数据取加载时映像的副本，CRC32 按该副本重新计算。
    /// 长度与映像不一致时拒绝生成，避免用旧范围发送新文件。
    /// </summary>
    public static bool TryCreate(
        FirmwareImage? image,
        ErasePlan? range,
        out FirmwareTransferPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        if (image is null)
        {
            error = "尚未加载固件文件，无法确定下载与校验范围。";
            return false;
        }

        if (range is null)
        {
            error = "尚未生成擦除范围，无法确定下载与校验范围。";
            return false;
        }

        if (image.Data.Length == 0)
        {
            error = "固件映像为空（长度为 0），没有可传输的数据。";
            return false;
        }

        if (range.Length != (uint)image.Data.Length)
        {
            error = $"擦除范围长度 {range.Length} 与固件映像长度 {image.Data.Length} 不一致，" +
                "拒绝用不同范围生成下载与校验请求。";
            return false;
        }

        if (range.StartAddress != (uint)image.BaseAddress)
        {
            error = $"擦除起始地址 0x{range.StartAddress:X8} 与固件映像起始地址 0x{image.BaseAddress:X8} 不一致，" +
                "拒绝生成下载与校验请求。";
            return false;
        }

        // 复制一份快照：后续所有块与 CRC 都读这份副本，界面换文件也不影响本次 OTA（§12.5）。
        var data = image.Data.ToArray();
        plan = new FirmwareTransferPlan(
            range.StartAddress,
            data,
            // 仅当测试开关非 0 时才产生“错误 CRC”，默认值与正确值完全一致。
            FirmwareCrc32.Compute(data) ^ Crc32TestXor,
            image.Format,
            image.FileName);
        return true;
    }
}
