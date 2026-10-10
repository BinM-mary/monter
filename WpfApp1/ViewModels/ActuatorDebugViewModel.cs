using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private CancellationTokenSource? deviceInfoReadNotificationCancellation;
    private bool synchronizingTargetPosition;

    public ActuatorDebugViewModel(
        SerialSettingsViewModel serialSettings)
    {
        SerialSettings = serialSettings;
        SerialSettings.PropertyChanged += SerialSettings_PropertyChanged;
        TotalTravelSteps = DefaultTotalTravelSteps;
        ClearDeviceInfoValues();
        Presets = new ObservableCollection<MotorPresetViewModel>(CreatePresetViewModels(
            motorPresetStorage.LoadOrCreate(DefaultPresets)));
        EditablePresets = new ObservableCollection<MotorPresetViewModel>();
    }

    public SerialSettingsViewModel SerialSettings { get; }

    public ObservableCollection<MotorPresetViewModel> Presets { get; }

    public ObservableCollection<MotorPresetViewModel> EditablePresets { get; }

    // 新电机帧 ID 和信号布局接入后再启用设备操作。
    public bool IsDeviceProtocolAvailable => false;

    public string ProtocolStatusText => "LIN 应用帧定义待配置，设备操作暂不可用";

    public bool IsMotorCommandAvailable =>
        IsDeviceProtocolAvailable && SerialSettings.IsConnected && HasInitializationSteps;

    public bool IsSerialCommandAvailable => IsDeviceProtocolAvailable && SerialSettings.IsConnected;

    public bool IsCurrentPositionMemoryAvailable =>
        IsMotorCommandAvailable && HasCurrentPosition;

    [ObservableProperty]
    private int totalTravelSteps;

    [ObservableProperty]
    private double targetPercent;

    [ObservableProperty]
    private int targetPositionSteps;

    // 当前位置待绑定新电机的 LIN 状态信号。
    [ObservableProperty]
    private int currentPositionSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMotorCommandAvailable))]
    [NotifyPropertyChangedFor(nameof(IsCurrentPositionMemoryAvailable))]
    private bool hasInitializationSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentPositionMemoryAvailable))]
    private bool hasCurrentPosition;

    public double CurrentPositionPercent => TotalTravelSteps <= 0
        ? 0d
        : Math.Clamp(CurrentPositionSteps, 0, TotalTravelSteps) * 100d / TotalTravelSteps;

    public string CurrentPositionSummary =>
        $"当前： {Math.Clamp(CurrentPositionSteps, 0, TotalTravelSteps)} step / {CurrentPositionPercent:0.0}%";

    [ObservableProperty]
    private bool isPresetConfigurationOpen;

    [ObservableProperty]
    private string presetConfigurationMessage = string.Empty;

    [ObservableProperty]
    private bool isDeviceInfoReadNotificationVisible;

    [ObservableProperty]
    private string deviceInfoReadNotificationText = string.Empty;

    [ObservableProperty]
    private string initializationStepsText = string.Empty;

    [ObservableProperty]
    private string retreatAngleText = string.Empty;

    [ObservableProperty]
    private string firmwareVersionText = string.Empty;

    [ObservableProperty]
    private string hardwareVersionText = string.Empty;

    [ObservableProperty]
    private string currentVoltageText = string.Empty;

    [ObservableProperty]
    private string currentTemperatureText = string.Empty;

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

    [RelayCommand(CanExecute = nameof(IsDeviceProtocolAvailable))]
    private void ReadDeviceInfo(string operationName)
    {
        ShowDeviceInfoReadNotification(ProtocolStatusText);
    }

    private void SerialSettings_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SerialSettingsViewModel.IsConnected))
        {
            ClearDeviceInfoValues();
            OnPropertyChanged(nameof(IsMotorCommandAvailable));
            OnPropertyChanged(nameof(IsSerialCommandAvailable));
            OnPropertyChanged(nameof(IsCurrentPositionMemoryAvailable));
        }
    }

    private void ClearDeviceInfoValues()
    {
        HasInitializationSteps = false;
        HasCurrentPosition = false;
        CurrentPositionSteps = 0;
        InitializationStepsText = string.Empty;
        RetreatAngleText = string.Empty;
        FirmwareVersionText = string.Empty;
        HardwareVersionText = string.Empty;
        CurrentVoltageText = string.Empty;
        CurrentTemperatureText = string.Empty;
    }

    private void ShowDeviceInfoReadNotification(string text)
    {
        deviceInfoReadNotificationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        deviceInfoReadNotificationCancellation = cancellation;
        DeviceInfoReadNotificationText = text;
        IsDeviceInfoReadNotificationVisible = true;
        _ = HideDeviceInfoReadNotificationAsync(cancellation);
    }

    private async Task HideDeviceInfoReadNotificationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(deviceInfoReadNotificationCancellation, cancellation))
            {
                IsDeviceInfoReadNotificationVisible = false;
                deviceInfoReadNotificationCancellation = null;
            }

            cancellation.Dispose();
        }
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

    [RelayCommand]
    private void ReadCurrentPositionIntoPreset(string? presetId)
    {
        if (!IsCurrentPositionMemoryAvailable || string.IsNullOrWhiteSpace(presetId))
        {
            return;
        }

        var sourcePresets = IsPresetConfigurationOpen && EditablePresets.Count > 0
            ? EditablePresets
            : Presets;
        var preset = sourcePresets.FirstOrDefault(item =>
            string.Equals(item.Id, presetId, StringComparison.Ordinal));
        if (preset is null)
        {
            return;
        }

        var originalPosition = preset.PositionSteps;
        var position = Math.Clamp(CurrentPositionSteps, 0, TotalTravelSteps);
        preset.PositionSteps = position;

        var configuration = sourcePresets
            .Select(item => item.ToConfigurationItem())
            .ToArray();
        if (!motorPresetStorage.TrySave(configuration, out var errorMessage))
        {
            preset.PositionSteps = originalPosition;
            ShowDeviceInfoReadNotification(errorMessage ?? "无法保存预设配置。");
            return;
        }

        if (!ReferenceEquals(sourcePresets, Presets))
        {
            Presets.Clear();
            foreach (var item in CreatePresetViewModels(configuration))
            {
                Presets.Add(item);
            }
        }

        ShowDeviceInfoReadNotification($"{preset.DisplayName}已读入当前位置：{position} step");
    }

    [RelayCommand(CanExecute = nameof(IsMotorCommandAvailable))]
    private void ConfirmTargetPosition()
    {
        ShowDeviceInfoReadNotification(ProtocolStatusText);
    }

    [RelayCommand(CanExecute = nameof(IsMotorCommandAvailable))]
    private void MoveToPreset(string? presetId)
    {
        ShowDeviceInfoReadNotification(ProtocolStatusText);
    }

    [RelayCommand(CanExecute = nameof(IsSerialCommandAvailable))]
    private void InitializeCalibration()
    {
        ShowDeviceInfoReadNotification(ProtocolStatusText);
    }

    [RelayCommand(CanExecute = nameof(IsSerialCommandAvailable))]
    private void EmergencyStop()
    {
        ShowDeviceInfoReadNotification(ProtocolStatusText);
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

    partial void OnCurrentPositionStepsChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPositionPercent));
        OnPropertyChanged(nameof(CurrentPositionSummary));
    }

    partial void OnTotalTravelStepsChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPositionPercent));
        OnPropertyChanged(nameof(CurrentPositionSummary));
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
