using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// ID 0x33 读取应用帧回复（8 字节 DATA）的信号提取。
/// 信号布局见 PROTOCOL.md §4.3；保留位不参与业务判断。
/// </summary>
public static class MotorStatusFrameParser
{
    public static MotorStatusSnapshot Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new ArgumentException("0x33 回复 DATA 必须为 8 字节。", nameof(data));
        }

        // 位置信号：16 bit 无符号，低位在前（字节 0/2/4 为 LSB）。
        var current = (ushort)(data[0] | (data[1] << 8));
        var positiveLimit = (ushort)(data[2] | (data[3] << 8));
        var negativeLimit = (ushort)(data[4] | (data[5] << 8));

        // 字节 6：bit0-1 运动状态，bit2-3 电压状态，bit4 温度状态；其余位保留。
        var flags = data[6];
        return new MotorStatusSnapshot(
            current,
            positiveLimit,
            negativeLimit,
            (MotorMotionState)(flags & 0x3),
            (MotorVoltageState)((flags >> 2) & 0x3),
            ((flags >> 4) & 0x1) != 0);
    }
}
