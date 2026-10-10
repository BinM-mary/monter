namespace WpfApp1.Models;

public enum LinChecksumType
{
    Classic,
    Enhanced
}

public enum LinPublisher
{
    Master,
    Slave
}

/// <summary>
/// 帧长度、发布节点和校验类型来自电机帧表/LDF，不是串口线上附加字段。
/// </summary>
public sealed record LinFrameDefinition
{
    public LinFrameDefinition(
        byte id,
        int dataLength,
        LinPublisher publisher,
        LinChecksumType checksumType)
    {
        if (id > 0x3D)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "LIN ID 必须为 0x00～0x3D，0x3E/0x3F 保留。");
        }

        if (dataLength is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength), "LIN 数据区长度必须为 1～8 字节。");
        }

        if (!Enum.IsDefined(publisher) || !Enum.IsDefined(checksumType))
        {
            throw new ArgumentException("发布节点或校验类型无效。");
        }

        if (id >= 0x3C && (dataLength != 8 || checksumType != LinChecksumType.Classic))
        {
            throw new ArgumentException("诊断帧固定为 8 字节，且必须使用 classic checksum。");
        }

        if ((id == 0x3C && publisher != LinPublisher.Master)
            || (id == 0x3D && publisher != LinPublisher.Slave))
        {
            throw new ArgumentException("0x3C 由主节点发布，0x3D 由从节点发布。");
        }

        Id = id;
        DataLength = dataLength;
        Publisher = publisher;
        ChecksumType = checksumType;
    }

    public byte Id { get; }
    public int DataLength { get; }
    public LinPublisher Publisher { get; }
    public LinChecksumType ChecksumType { get; }
    public bool IsDiagnostic => Id >= 0x3C;
}
