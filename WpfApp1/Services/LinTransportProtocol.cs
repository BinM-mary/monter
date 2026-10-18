using System.Diagnostics;
using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>
/// LIN 诊断传输层（TP）：SF 请求编码与 SF/FF/CF 响应重组，与 WPF、串口和界面无关。
/// 行为依据 WpfApp1诊断架构讨论记录.md §11.3、§14：
/// - 输入为 8 字节 PDU（LIN 校验已由统一接收入口处理，3C 回显不进入 TP）；
/// - 先检查状态与 NAD，再解释 PCI；错误 NAD 忽略且不刷新 N_Cr；
/// - SF 长度 1～6；FF 总长度 7～4095，缓冲上限为协议最大值，不为具体业务写死；
/// - CF 序号 1、2、…、15、0、1…（4 位回绕），错序/重复/新首帧/N_Cr 到期放弃重组；
/// - “重组已放弃”后不因后续输入重启；消息收齐只交付一次；
/// - 以普通返回值向协调层报告结果，不建立独立事件系统。
/// </summary>
public sealed class LinTransportProtocol
{
    private enum TpState : byte
    {
        WaitingFirstFrame,
        Reassembling,
        Abandoned,
        Completed
    }

    private const int MaxMessageLength = 4095;

    private readonly object stateLock = new();
    private TpState state = TpState.WaitingFirstFrame;
    private byte expectedNad;
    private long ncrDuration;
    private long ncrDeadline;
    private int declaredLength;
    private int receivedLength;
    private byte nextSequenceNumber;
    private byte[] reassemblyBuffer = [];
    private string? abandonReason;

    /// <summary>
    /// 将一条 UDS 消息编码为单帧 PDU：NAD + PCI(0x0L) + 消息 + 0xFF 填充至 8 字节。
    /// 当前三个版本请求（3 字节）均为 SF。
    /// </summary>
    public static byte[] EncodeSingleFrame(byte nad, ReadOnlySpan<byte> message)
    {
        if (message.Length is < 1 or > 6)
        {
            throw new ArgumentException("单帧消息长度必须为 1～6 字节。", nameof(message));
        }

        var pdu = new byte[8];
        pdu[0] = nad;
        pdu[1] = (byte)message.Length;
        message.CopyTo(pdu.AsSpan(2));
        // 填充不参与 UDS 消息，但属于 PDU，参与 LIN 校验。
        pdu.AsSpan(2 + message.Length).Fill(0xFF);
        return pdu;
    }

    /// <summary>为一笔新业务建立“等待首帧”状态；不清空共用串口字节缓存（§12.4）。</summary>
    public void BeginReceive(byte nad, TimeSpan consecutiveFrameTimeout)
    {
        lock (stateLock)
        {
            expectedNad = nad;
            ncrDuration = ToTimestampUnits(consecutiveFrameTimeout);
            state = TpState.WaitingFirstFrame;
            reassemblyBuffer = [];
            receivedLength = 0;
            declaredLength = 0;
            nextSequenceNumber = 0;
            ncrDeadline = 0;
            abandonReason = null;
        }
    }

    /// <summary>处理一个 8 字节诊断响应 PDU；receivedAt 为入口提供的软件接收单调时间。</summary>
    public TpReceiveResult ProcessPdu(ReadOnlySpan<byte> pdu, long receivedAt)
    {
        if (pdu.Length != 8)
        {
            return TpReceiveResult.ContinueWaiting;
        }

        lock (stateLock)
        {
            if (state is TpState.Abandoned or TpState.Completed)
            {
                // 已放弃/已收齐：只接收记录（由入口完成），不重启本笔重组、不重复交付。
                return TpReceiveResult.ContinueWaiting;
            }

            if (pdu[0] != expectedNad)
            {
                // 错误 NAD：忽略，不改变状态、缓冲和序号，不刷新 N_Cr。
                return TpReceiveResult.ContinueWaiting;
            }

            var pci = pdu[1];
            switch (pci >> 4)
            {
                case 0:
                    return ProcessSingleFrame(pdu, pci);
                case 1:
                    return ProcessFirstFrame(pdu, pci, receivedAt);
                case 2:
                    return ProcessConsecutiveFrame(pdu, pci, receivedAt);
                default:
                    // 不支持的 PCI 类型：两种状态下均忽略本帧。
                    return TpReceiveResult.ContinueWaiting;
            }
        }
    }

    /// <summary>检查 N_Cr；到期则放弃重组并返回原因，不改变业务总截止或发送节拍（§12.4）。</summary>
    public TpReceiveResult CheckTimeout(long now)
    {
        lock (stateLock)
        {
            if (state == TpState.Reassembling
                && ncrDeadline != 0
                && now >= ncrDeadline)
            {
                return AbandonLocked("N_Cr 超时");
            }

            return TpReceiveResult.ContinueWaiting;
        }
    }

    /// <summary>清理本笔 TP 数据与计时；由协调层在业务结束、断线或退出时组织调用。</summary>
    public void Reset()
    {
        lock (stateLock)
        {
            state = TpState.WaitingFirstFrame;
            reassemblyBuffer = [];
            receivedLength = 0;
            declaredLength = 0;
            nextSequenceNumber = 0;
            ncrDeadline = 0;
            abandonReason = null;
        }
    }

    private TpReceiveResult ProcessSingleFrame(ReadOnlySpan<byte> pdu, byte pci)
    {
        var length = pci & 0x0F;
        if (length is < 1 or > 6)
        {
            // 非法 SF：等待首帧时忽略（仍允许截止前的后续有效首帧）；
            // 正在重组时同样忽略，保留原重组及预期序号（§14.2）。
            return TpReceiveResult.ContinueWaiting;
        }

        if (state == TpState.Reassembling)
        {
            // 重组期间出现另一条合法 SF：当前响应传输不再连续，放弃本次重组。
            return AbandonLocked("重组期间出现新首帧");
        }

        var message = pdu.Slice(2, length).ToArray();
        state = TpState.Completed;
        ncrDeadline = 0;
        return TpReceiveResult.Complete(message);
    }

    private TpReceiveResult ProcessFirstFrame(ReadOnlySpan<byte> pdu, byte pci, long receivedAt)
    {
        var totalLength = ((pci & 0x0F) << 8) | pdu[2];
        if (totalLength is < 7 or > MaxMessageLength)
        {
            // 非法 FF：两种状态下均忽略，不为非法长度分配缓冲。
            return TpReceiveResult.ContinueWaiting;
        }

        if (state == TpState.Reassembling)
        {
            // 重组期间出现另一条合法 FF：放弃，不把新 FF 作为本笔新起点。
            return AbandonLocked("重组期间出现新首帧");
        }

        declaredLength = totalLength;
        reassemblyBuffer = new byte[totalLength];
        pdu.Slice(3, 5).CopyTo(reassemblyBuffer);
        receivedLength = 5;
        nextSequenceNumber = 1;
        ncrDeadline = receivedAt + ncrDuration;
        state = TpState.Reassembling;
        return TpReceiveResult.ContinueWaiting;
    }

    private TpReceiveResult ProcessConsecutiveFrame(ReadOnlySpan<byte> pdu, byte pci, long receivedAt)
    {
        if (state == TpState.WaitingFirstFrame)
        {
            // 孤立 CF：忽略，不启动 N_Cr。
            return TpReceiveResult.ContinueWaiting;
        }

        // 接受 CF 前先检查 N_Cr 是否已到期，避免处理延迟让过期帧刷新计时（§14.3）。
        if (ncrDeadline != 0 && receivedAt >= ncrDeadline)
        {
            return AbandonLocked("N_Cr 超时");
        }

        var sequenceNumber = (byte)(pci & 0x0F);
        if (sequenceNumber != nextSequenceNumber)
        {
            // 错序或重复序号：放弃重组。
            return AbandonLocked($"CF 序号错误（期望 {nextSequenceNumber:X}，收到 {sequenceNumber:X}）");
        }

        var remaining = declaredLength - receivedLength;
        var take = Math.Min(6, remaining);
        pdu.Slice(2, take).CopyTo(reassemblyBuffer.AsSpan(receivedLength));
        receivedLength += take;

        if (receivedLength >= declaredLength)
        {
            var message = reassemblyBuffer;
            reassemblyBuffer = [];
            state = TpState.Completed;
            ncrDeadline = 0;
            return TpReceiveResult.Complete(message);
        }

        // 4 位序号回绕：…、15、0、1…
        nextSequenceNumber = (byte)((sequenceNumber + 1) & 0x0F);
        ncrDeadline = receivedAt + ncrDuration;
        return TpReceiveResult.ContinueWaiting;
    }

    private TpReceiveResult AbandonLocked(string reason)
    {
        reassemblyBuffer = [];
        ncrDeadline = 0;
        abandonReason = reason;
        state = TpState.Abandoned;
        return TpReceiveResult.Abandoned(reason);
    }

    private static long ToTimestampUnits(TimeSpan timeout)
    {
        return (long)(timeout.TotalSeconds * Stopwatch.Frequency);
    }
}
