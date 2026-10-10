using WpfApp1.Models;

namespace WpfApp1.Services;

/// <summary>同一总线的固定帧定义。未登记 ID 不猜测长度，不进入业务处理。</summary>
public sealed class LinFrameCatalog
{
    private readonly IReadOnlyDictionary<byte, LinFrameDefinition> definitions;

    public LinFrameCatalog(IEnumerable<LinFrameDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        this.definitions = definitions.ToDictionary(definition => definition.Id);
    }

    public IReadOnlyCollection<LinFrameDefinition> Definitions => definitions.Values.ToArray();

    public bool TryGet(byte id, out LinFrameDefinition? definition)
    {
        return definitions.TryGetValue(id, out definition);
    }

    public LinFrameDefinition Get(byte id)
    {
        return definitions.TryGetValue(id, out var definition)
            ? definition
            : throw new InvalidOperationException($"LIN 帧 0x{id:X2} 尚未配置长度、发布节点和校验类型。");
    }
}
