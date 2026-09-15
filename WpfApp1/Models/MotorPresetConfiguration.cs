namespace WpfApp1.Models;

public sealed class MotorPresetConfiguration
{
    public int Version { get; init; } = 1;

    public List<MotorPresetConfigurationItem> Presets { get; init; } = [];
}

public sealed class MotorPresetConfigurationItem
{
    public required string Id { get; init; }

    public int PositionSteps { get; init; }
}
