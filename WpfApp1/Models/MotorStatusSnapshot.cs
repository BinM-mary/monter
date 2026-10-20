namespace WpfApp1.Models;

/// <summary>电机运动状态（ID 0x33 字节 6 bit0-2，3 bit）。</summary>
public enum MotorMotionState
{
    Stopped = 0,
    Moving = 1,
    Stalled = 2,
    OpenCircuit = 3,
    ShortCircuit = 4
}

/// <summary>电机电压状态（ID 0x33 字节 6 bit3-4，2 bit）。</summary>
public enum MotorVoltageState
{
    Normal = 0,
    Low = 1,
    High = 2,
    Reserved = 3
}

/// <summary>电机初始化状态（ID 0x33 字节 6 bit6-7，2 bit）。</summary>
public enum MotorInitializationState
{
    Completed = 0,
    InProgress = 1,
    Error = 2,
    Undefined = 3
}

/// <summary>
/// ID 0x33 读取应用帧回复解析出的电机状态快照。
/// 位置均为 16 bit 无符号、单位 step、低位在前；信号布局见 协议/PROTOCOL.md §4.3。
/// </summary>
public sealed record MotorStatusSnapshot(
    ushort CurrentPositionSteps,
    ushort PositiveLimitSteps,
    ushort NegativeLimitSteps,
    MotorMotionState MotionState,
    MotorVoltageState VoltageState,
    bool Overtemperature,
    MotorInitializationState InitializationState,
    byte StallRetreatSteps);

/// <summary>
/// ID 0x34 读取应用帧回复解析出的电机测量值。
/// 两组 4 字节均为 IEEE 754 binary32 的原始内存表示，按小端读取。
/// 单位与换算由设备侧约定，上位机不做倍率或偏移。
/// </summary>
public sealed record MotorMeasurementSnapshot(float Temperature, float Voltage);
