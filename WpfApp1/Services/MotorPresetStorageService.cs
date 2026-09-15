using System.IO;
using System.Text.Json;
using WpfApp1.Models;

namespace WpfApp1.Services;

public sealed class MotorPresetStorageService
{
    private const int ConfigurationVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string configurationPath;

    public MotorPresetStorageService()
        : this(Path.Combine(AppContext.BaseDirectory, "config", "motor-presets.json"))
    {
    }

    internal MotorPresetStorageService(string configurationPath)
    {
        this.configurationPath = configurationPath;
    }

    public IReadOnlyList<MotorPresetConfigurationItem> LoadOrCreate(
        IReadOnlyList<MotorPresetConfigurationItem> defaultPresets)
    {
        try
        {
            if (File.Exists(configurationPath))
            {
                var configuration = JsonSerializer.Deserialize<MotorPresetConfiguration>(File.ReadAllText(configurationPath));
                if (IsValid(configuration, defaultPresets))
                {
                    return configuration!.Presets;
                }
            }

            TrySave(defaultPresets, out _);
        }
        catch (IOException)
        {
            TrySave(defaultPresets, out _);
        }
        catch (JsonException)
        {
            TrySave(defaultPresets, out _);
        }
        catch (UnauthorizedAccessException)
        {
            // 应用程序仍使用默认预设，保存时会向用户显示无法写入的原因。
        }

        return Clone(defaultPresets);
    }

    public bool TrySave(IReadOnlyList<MotorPresetConfigurationItem> presets, out string? errorMessage)
    {
        try
        {
            var directory = Path.GetDirectoryName(configurationPath)!;
            Directory.CreateDirectory(directory);

            var configuration = new MotorPresetConfiguration
            {
                Version = ConfigurationVersion,
                Presets = Clone(presets).ToList()
            };
            var temporaryPath = $"{configurationPath}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, SerializerOptions));
            File.Move(temporaryPath, configurationPath, true);
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"无法保存预设配置：{exception.Message}";
            return false;
        }
    }

    private static bool IsValid(
        MotorPresetConfiguration? configuration,
        IReadOnlyList<MotorPresetConfigurationItem> defaultPresets)
    {
        if (configuration?.Version != ConfigurationVersion
            || configuration.Presets is null
            || configuration.Presets.Count != defaultPresets.Count
            || configuration.Presets.Any(preset => preset.PositionSteps < 0))
        {
            return false;
        }

        var expectedIds = defaultPresets.Select(preset => preset.Id).ToHashSet(StringComparer.Ordinal);
        return configuration.Presets.All(preset => expectedIds.Contains(preset.Id))
            && configuration.Presets.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count() == expectedIds.Count;
    }

    private static IReadOnlyList<MotorPresetConfigurationItem> Clone(IReadOnlyList<MotorPresetConfigurationItem> presets)
    {
        return presets.Select(preset => new MotorPresetConfigurationItem
        {
            Id = preset.Id,
            PositionSteps = preset.PositionSteps
        }).ToArray();
    }
}
