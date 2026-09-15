using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class ActuatorDebugViewModel : ObservableObject
{
    private const int DefaultTotalTravelSteps = 12_000;
    private static readonly MotorPresetConfigurationItem[] DefaultPresets =
    [
        new() { Id = "open25", PositionSteps = 3_000 },
        new() { Id = "open50", PositionSteps = 6_000 },
        new() { Id = "openFull", PositionSteps = 12_000 }
    ];

    private static readonly IReadOnlyDictionary<string, string> PresetDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["open25"] = "OPEN1",
            ["open50"] = "OPEN2",
            ["openFull"] = "OPEN3"
        };

    private readonly MotorPresetStorageService motorPresetStorage = new();
    private bool synchronizingTargetPosition;

    public ActuatorDebugViewModel(SerialSettingsViewModel serialSettings)
    {
        SerialSettings = serialSettings;
        TotalTravelSteps = DefaultTotalTravelSteps;
        Presets = new ObservableCollection<MotorPresetViewModel>(CreatePresetViewModels(
            motorPresetStorage.LoadOrCreate(DefaultPresets)));
        EditablePresets = new ObservableCollection<MotorPresetViewModel>();
    }

    public SerialSettingsViewModel SerialSettings { get; }

    public ObservableCollection<MotorPresetViewModel> Presets { get; }

    public ObservableCollection<MotorPresetViewModel> EditablePresets { get; }

    // 协议命令尚未确定，所有实际动作按钮必须保持禁用，避免给用户造成已执行的误导。
    public bool IsMotorCommandAvailable => false;

    [ObservableProperty]
    private int totalTravelSteps;

    [ObservableProperty]
    private double targetPercent;

    [ObservableProperty]
    private int targetPositionSteps;

    [ObservableProperty]
    private bool isPresetConfigurationOpen;

    [ObservableProperty]
    private string presetConfigurationMessage = string.Empty;

    [ObservableProperty]
    private string title = "执行器调试";

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string statusText = "系统就绪";

    [RelayCommand]
    private void Test()
    {
        StatusText = $"你好，{UserName}";
    }

    [RelayCommand]
    private void OpenPresetConfiguration()
    {
        EditablePresets.Clear();
        foreach (var preset in Presets)
        {
            EditablePresets.Add(new MotorPresetViewModel(
                preset.Id,
                preset.DisplayName,
                preset.PositionSteps,
                TotalTravelSteps));
        }

        PresetConfigurationMessage = string.Empty;
        IsPresetConfigurationOpen = true;
    }

    [RelayCommand]
    private void ClosePresetConfiguration()
    {
        PresetConfigurationMessage = string.Empty;
        IsPresetConfigurationOpen = false;
    }

    [RelayCommand]
    private void SavePresetConfiguration()
    {
        var configuration = EditablePresets
            .Select(preset => preset.ToConfigurationItem())
            .ToArray();

        if (!motorPresetStorage.TrySave(configuration, out var errorMessage))
        {
            PresetConfigurationMessage = errorMessage ?? "无法保存预设配置。";
            return;
        }

        Presets.Clear();
        foreach (var preset in CreatePresetViewModels(configuration))
        {
            Presets.Add(preset);
        }

        IsPresetConfigurationOpen = false;
    }

    partial void OnTargetPercentChanged(double value)
    {
        if (synchronizingTargetPosition)
        {
            return;
        }

        var normalizedValue = double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;
        synchronizingTargetPosition = true;
        try
        {
            TargetPercent = normalizedValue;
            TargetPositionSteps = (int)Math.Round(TotalTravelSteps * normalizedValue / 100d);
        }
        finally
        {
            synchronizingTargetPosition = false;
        }
    }

    partial void OnTargetPositionStepsChanged(int value)
    {
        if (synchronizingTargetPosition)
        {
            return;
        }

        var normalizedValue = Math.Clamp(value, 0, TotalTravelSteps);
        synchronizingTargetPosition = true;
        try
        {
            TargetPositionSteps = normalizedValue;
            TargetPercent = Math.Round(normalizedValue * 100d / TotalTravelSteps, 1);
        }
        finally
        {
            synchronizingTargetPosition = false;
        }
    }

    private IEnumerable<MotorPresetViewModel> CreatePresetViewModels(
        IEnumerable<MotorPresetConfigurationItem> configuration)
    {
        var positionsById = configuration.ToDictionary(preset => preset.Id, preset => preset.PositionSteps, StringComparer.Ordinal);
        return DefaultPresets.Select(defaultPreset => new MotorPresetViewModel(
            defaultPreset.Id,
            PresetDisplayNames[defaultPreset.Id],
            positionsById.GetValueOrDefault(defaultPreset.Id, defaultPreset.PositionSteps),
            TotalTravelSteps));
    }
}
