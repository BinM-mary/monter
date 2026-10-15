namespace WpfApp1.Services;

/// <summary>
/// ID 0x15 写入应用帧的当前数据状态（8 字节 DATA）。
/// 布局：字节 0 运动控制指令（0x0 停止 / 0x1 运动到指定位置 / 0x2 一直运动 / 0x3 执行初始化）、
/// 字节 1 运动方向（0x0 UNKNOWN / 0x1 CW / 0x2 CCW，上位机暂未实现，保留 0）、
/// 字节 2-3 运动位置（16 bit 无符号 step，低位在前），字节 4-7 保留为 0。
/// 初始全 0；上位机操作只修改对应字段，轮询服务每 100 ms 按当前快照组帧发送。
/// 线程安全：界面线程写字段，轮询线程读快照。
/// </summary>
public sealed class MotorCommandService
{
    public const byte FrameId = 0x15;
    public const int DataLength = 8;

    public const byte CommandStop = 0x0;
    public const byte CommandMoveToPosition = 0x1;
    public const byte CommandContinuous = 0x2;
    public const byte CommandInitialize = 0x3;

    private readonly object stateLock = new();
    private byte motionCommand = CommandStop;
    // 运动方向：字段保留（0x0 UNKNOWN / 0x1 CW / 0x2 CCW），上位机暂未实现，固定为 0。
    private byte direction = 0;
    private ushort targetPositionSteps;

    /// <summary>当前 8 字节 DATA 快照，用于 100 ms 周期组帧。</summary>
    public byte[] CreateDataSnapshot()
    {
        lock (stateLock)
        {
            return
            [
                motionCommand,
                direction,
                (byte)(targetPositionSteps & 0xFF),
                (byte)(targetPositionSteps >> 8),
                0, 0, 0, 0
            ];
        }
    }

    /// <summary>运动到指定位置：指令 0x1，位置为 16 bit 无符号 step。</summary>
    public void SetMoveToPosition(ushort positionSteps)
    {
        lock (stateLock)
        {
            motionCommand = CommandMoveToPosition;
            targetPositionSteps = positionSteps;
        }
    }

    /// <summary>执行初始化：指令 0x3；位置与方向字段保持不变。</summary>
    public void SetInitialize()
    {
        lock (stateLock)
        {
            motionCommand = CommandInitialize;
        }
    }

    /// <summary>电机立即停下：指令 0x0；位置与方向字段保持不变。</summary>
    public void SetStop()
    {
        lock (stateLock)
        {
            motionCommand = CommandStop;
        }
    }
}
