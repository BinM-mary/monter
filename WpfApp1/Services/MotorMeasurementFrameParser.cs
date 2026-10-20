using System.Buffers.Binary;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// ID 0x34 读取应用帧回复（8 字节 DATA）的数值提取。
/// 前 4 字节为温度、后 4 字节为电压，均为 4 字节 float 的原始内存表示。
/// 中间件通过联合体（ByteToFloat）直接拷贝内存，Cortex-M 为小端，
/// 因此按 little-endian binary32 读取；不做倍率、偏移或单位换算。
/// 信号布局见 协议/PROTOCOL.md §12.2。
/// </summary>
public static class MotorMeasurementFrameParser
{
    public static MotorMeasurementSnapshot Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new ArgumentException("0x34 回复 DATA 必须为 8 字节。", nameof(data));
        }

        var temperature = BinaryPrimitives.ReadSingleLittleEndian(data[..4]);
        var voltage = BinaryPrimitives.ReadSingleLittleEndian(data[4..8]);
        return new MotorMeasurementSnapshot(temperature, voltage);
    }
}
