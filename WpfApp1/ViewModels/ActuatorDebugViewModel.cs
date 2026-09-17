using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    private readonly DeviceCommunicationService deviceCommunication;
    private CancellationTokenSource? deviceInfoReadNotificationCancellation;
    private bool synchronizingTargetPosition;

    public ActuatorDebugViewModel(
        SerialSettingsViewModel serialSettings,
        DeviceCommunicationService deviceCommunication)
    {
        SerialSettings = serialSettings;
        this.deviceCommunication = deviceCommunication;
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

    [RelayCommand]
    private async Task ReadDeviceInfoAsync(string operationName)
    {
        if (!SerialSettings.IsConnected)
        {
            ShowDeviceInfoReadNotification("请先连接串口");
            return;
        }

        if (!TryGetDeviceInfoRequest(operationName, out var subCommand, out var expectedDataLength))
        {
            return;
        }

        ShowDeviceInfoReadNotification($"正在{operationName}");

        try
        {
            var response = await deviceCommunication.SendRequestAsync(
                0x10,
                subCommand,
                ReadOnlyMemory<byte>.Empty,
                SerialSettings.RequestTimeout);

            if (response.SubCommand == 0xDF)
            {
                ShowDeviceInfoReadNotification($"{operationName}不受设备支持");
                return;
            }

            if (response.Data.Length != expectedDataLength)
            {
                ShowDeviceInfoReadNotification($"{operationName}响应数据长度错误");
                return;
            }

            ApplyDeviceInfoResponse(operationName, response.Data);
            ShowDeviceInfoReadNotification($"{operationName}成功");
        }
        catch (TimeoutException)
        {
            ShowDeviceInfoReadNotification($"{operationName}超时");
        }
        catch (Exception exception)
        {
            ShowDeviceInfoReadNotification($"{operationName}失败：{exception.Message}");
        }
    }

    private void SerialSettings_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SerialSettingsViewModel.IsConnected))
        {
            ClearDeviceInfoValues();
        }
    }

    private void ClearDeviceInfoValues()
    {
        InitializationStepsText = string.Empty;
        RetreatAngleText = string.Empty;
        FirmwareVersionText = string.Empty;
        HardwareVersionText = string.Empty;
        CurrentVoltageText = string.Empty;
        CurrentTemperatureText = string.Empty;
    }

    private void ApplyDeviceInfoResponse(
        string operationName,
        byte[] data)
    {
        switch (operationName)
        {
            case "读取初始化步数":
                InitializationStepsText = BinaryPrimitives
                    .ReadUInt32BigEndian(data)
                    .ToString(CultureInfo.InvariantCulture);
                break;
            case "读取固件版本":
                FirmwareVersionText = $"V{data[0]}.{data[1]}.{data[2]}";
                break;
            case "读取硬件版本":
                HardwareVersionText = FormatHardwareVersion(data[0]);
                break;
            case "读取电压和温度参数":
                var voltageMillivolts = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
                var temperatureTenths = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2, 2));
                CurrentVoltageText =
                    $"{(voltageMillivolts / 1000d).ToString("0.000", CultureInfo.InvariantCulture)} V";
                CurrentTemperatureText =
                    $"{(temperatureTenths / 10d).ToString("0.0", CultureInfo.InvariantCulture)} ℃";
                break;
        }
    }

    private static string FormatHardwareVersion(byte revision)
    {
        return revision <= 25
            ? $"Rev.{(char)('A' + revision)}"
            : $"Rev.0x{revision:X2}";
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

    private static bool TryGetDeviceInfoRequest(
        string operationName,
        out byte subCommand,
        out int expectedDataLength)
    {
        switch (operationName)
        {
            case "读取初始化步数":
                subCommand = 0x00;
                expectedDataLength = 4;
                return true;
            case "读取固件版本":
                subCommand = 0x01;
                expectedDataLength = 3;
                return true;
            case "读取硬件版本":
                subCommand = 0x02;
                expectedDataLength = 1;
                return true;
            case "读取电压和温度参数":
                subCommand = 0x03;
                expectedDataLength = 4;
                return true;
            default:
                subCommand = 0;
                expectedDataLength = 0;
                return false;
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
