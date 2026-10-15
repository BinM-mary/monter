namespace WpfApp1.Models;

/// <summary>电机运动状态（ID 0x33 字节 6 bit0-1）。</summary>
public enum MotorMotionState
{
    Stopped = 0,
    Moving = 1,
    Stalled = 2,
    Reserved = 3
}

/// <summary>电机电压状态（ID 0x33 字节 6 bit2-3）。</summary>
public enum MotorVoltageState
{
    Normal = 0,
    Low = 1,
    High = 2,
    Reserved = 3
}

/// <summary>
/// ID 0x33 读取应用帧回复解析出的电机状态快照。
/// 位置均为 16 bit 无符号、单位 step、低位在前；信号布局见 PROTOCOL.md §4.3。
/// </summary>
public sealed record MotorStatusSnapshot(
    ushort CurrentPositionSteps,
    ushort PositiveLimitSteps,
    ushort NegativeLimitSteps,
    MotorMotionState MotionState,
    MotorVoltageState VoltageState,
    bool Overtemperature);
