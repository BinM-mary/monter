namespace WpfApp1.Models;

/// <summary>已按帧定义和 checksum 校验通过的一条完整 LIN 帧。</summary>
public sealed class LinFrame
{
    internal LinFrame(LinFrameDefinition definition, byte protectedId, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        ProtectedId = protectedId;
        Data = data.ToArray();
    }

    public LinFrameDefinition Definition { get; }
    public byte Id => Definition.Id;
    public byte ProtectedId { get; }
    public ReadOnlyMemory<byte> Data { get; }
}
