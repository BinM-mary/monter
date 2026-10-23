namespace WpfApp1.Models;

/// <summary>
/// CRC-32（IEEE 802.3，反射多项式 0xEDB88320、初值 0xFFFFFFFF、最终取反），
/// 用于固件映像校验值显示与第十四步固件校验请求（架构/OTA升级流程.md §14.6）。
/// <para>
/// 纯算法、无 IO 与界面依赖：固件加载时按归一化映像算一次用于显示；
/// 第十四步由传输快照按同一份数据复算，保证校验值与实际发送的字节一致。
/// 与 Bootloader 的多项式/初值/位序/最终异或是否完全一致仍需按设备实现核对（§14.6）。
/// </para>
/// </summary>
internal static class FirmwareCrc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
