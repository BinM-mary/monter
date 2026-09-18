using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
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
    private bool hasSentCurrentPositionQueryForConnection;

    public ActuatorDebugViewModel(
        SerialSettingsViewModel serialSettings,
        DeviceCommunicationService deviceCommunication)
    {
        SerialSettings = serialSettings;
        this.deviceCommunication = deviceCommunication;
        SerialSettings.PropertyChanged += SerialSettings_PropertyChanged;
        deviceCommunication.FrameReceived += HandleFrameReceived;
        TotalTravelSteps = DefaultTotalTravelSteps;
        ClearDeviceInfoValues();
        Presets = new ObservableCollection<MotorPresetViewModel>(CreatePresetViewModels(
            motorPresetStorage.LoadOrCreate(DefaultPresets)));
        EditablePresets = new ObservableCollection<MotorPresetViewModel>();
    }

    public SerialSettingsViewModel SerialSettings { get; }

    public ObservableCollection<MotorPresetViewModel> Presets { get; }

    public ObservableCollection<MotorPresetViewModel> EditablePresets { get; }

    public bool IsMotorCommandAvailable =>
        SerialSettings.IsConnected && HasInitializationSteps;

    public bool IsSerialCommandAvailable => SerialSettings.IsConnected;

    public bool IsCurrentPositionMemoryAvailable =>
        IsMotorCommandAvailable && HasCurrentPosition;

    [ObservableProperty]
    private int totalTravelSteps;

    [ObservableProperty]
    private double targetPercent;

    [ObservableProperty]
    private int targetPositionSteps;

    // 当前位置由任意时刻收到的 0x30 / 0x40 有效响应更新。
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

            if ((expectedDataLength.HasValue && response.Data.Length != expectedDataLength.Value)
                || (!expectedDataLength.HasValue && response.Data.Length == 0))
            {
                ShowDeviceInfoReadNotification($"{operationName}响应数据长度错误");
                return;
            }

            if (!TryApplyDeviceInfoResponse(
                    operationName,
                    response.Data,
                    out var shouldRequestCurrentPosition))
            {
                ShowDeviceInfoReadNotification($"{operationName}响应数据范围错误");
                return;
            }

            ShowDeviceInfoReadNotification($"{operationName}成功");
            if (shouldRequestCurrentPosition)
            {
                await SendCurrentPositionQueryOnceAsync();
            }
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
        hasSentCurrentPositionQueryForConnection = false;
        InitializationStepsText = string.Empty;
        RetreatAngleText = string.Empty;
        FirmwareVersionText = string.Empty;
        HardwareVersionText = string.Empty;
        CurrentVoltageText = string.Empty;
        CurrentTemperatureText = string.Empty;
    }

    private bool TryApplyDeviceInfoResponse(
        string operationName,
        byte[] data,
        out bool shouldRequestCurrentPosition)
    {
        shouldRequestCurrentPosition = false;
        switch (operationName)
        {
            case "读取初始化步数":
                if (!TryReadStepValue(data, out var initializationSteps)
                    || initializationSteps == 0)
                {
                    return false;
                }

                InitializationStepsText = initializationSteps.ToString(CultureInfo.InvariantCulture);
                UpdateTotalTravelSteps(initializationSteps);
                HasInitializationSteps = true;
                shouldRequestCurrentPosition = true;
                return true;
            case "读取固件版本":
                FirmwareVersionText = $"V{data[0]}.{data[1]}.{data[2]}";
                return true;
            case "读取硬件版本":
                HardwareVersionText = FormatHardwareVersion(data[0]);
                return true;
            case "读取电压和温度参数":
                var voltageMillivolts = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
                var temperatureTenths = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2, 2));
                CurrentVoltageText =
                    $"{(voltageMillivolts / 1000d).ToString("0.000", CultureInfo.InvariantCulture)} V";
                CurrentTemperatureText =
                    $"{(temperatureTenths / 10d).ToString("0.0", CultureInfo.InvariantCulture)} ℃";
                return true;
            default:
                return false;
        }
    }

    private void UpdateTotalTravelSteps(int value)
    {
        TotalTravelSteps = value;
        synchronizingTargetPosition = true;
        try
        {
            TargetPositionSteps = Math.Clamp(TargetPositionSteps, 0, value);
            TargetPercent = Math.Round(TargetPositionSteps * 100d / value, 1);
        }
        finally
        {
            synchronizingTargetPosition = false;
        }

        CurrentPositionSteps = Math.Clamp(CurrentPositionSteps, 0, value);

        foreach (var preset in Presets)
        {
            preset.SetTotalTravelSteps(value);
        }

        foreach (var preset in EditablePresets)
        {
            preset.SetTotalTravelSteps(value);
        }
    }

    private static string FormatHardwareVersion(byte revision)
    {
        return revision <= 25
            ? $"Rev.{(char)('A' + revision)}"
            : $"Rev.0x{revision:X2}";
    }

    private async Task SendCurrentPositionQueryOnceAsync()
    {
        if (!IsMotorCommandAvailable || hasSentCurrentPositionQueryForConnection)
        {
            return;
        }

        hasSentCurrentPositionQueryForConnection = true;
        try
        {
            await deviceCommunication.SendAsync(
                0x30,
                0x00,
                ReadOnlyMemory<byte>.Empty);
        }
        catch (Exception exception)
        {
            ShowDeviceInfoReadNotification($"读取当前位置发送失败：{exception.Message}");
        }
    }

    private void HandleFrameReceived(ProtocolFrame frame)
    {
        if (frame.Command != 0x30 || frame.SubCommand != 0x40)
        {
            return;
        }

        if (!TryReadStepValue(frame.Data, out var position))
        {
            RunOnUiThread(() => ShowDeviceInfoReadNotification("当前位置响应数据范围错误"));
            return;
        }

        RunOnUiThread(() =>
        {
            if (!SerialSettings.IsConnected)
            {
                return;
            }

            CurrentPositionSteps = position;
            HasCurrentPosition = true;
        });
    }

    private static bool TryReadStepValue(byte[] data, out int value)
    {
        value = 0;
        if (data.Length == 0)
        {
            return false;
        }

        var stepValue = new BigInteger(
            data,
            isUnsigned: true,
            isBigEndian: true);
        if (stepValue > int.MaxValue)
        {
            return false;
        }

        value = (int)stepValue;
        return true;
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
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
        out int? expectedDataLength)
    {
        switch (operationName)
        {
            case "读取初始化步数":
                subCommand = 0x00;
                expectedDataLength = null;
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

    [RelayCommand]
    private Task ConfirmTargetPositionAsync()
    {
        return SendTargetPositionAsync(TargetPositionSteps);
    }

    [RelayCommand]
    private async Task MoveToPresetAsync(string? presetId)
    {
        if (string.Equals(presetId, "close", StringComparison.Ordinal))
        {
            TargetPositionSteps = 0;
            await SendTargetPositionAsync(0);
            return;
        }

        var preset = Presets.FirstOrDefault(item =>
            string.Equals(item.Id, presetId, StringComparison.Ordinal));
        if (preset is null)
        {
            return;
        }

        TargetPositionSteps = preset.PositionSteps;
        await SendTargetPositionAsync(preset.PositionSteps);
    }

    [RelayCommand]
    private Task InitializeCalibrationAsync()
    {
        return SendMotorControlFrameAsync(
            0x00,
            ReadOnlyMemory<byte>.Empty,
            "已发送初始化校准命令",
            requiresInitializationSteps: false);
    }

    [RelayCommand]
    private Task EmergencyStopAsync()
    {
        return SendMotorControlFrameAsync(
            0x02,
            ReadOnlyMemory<byte>.Empty,
            "已发送紧急停止命令",
            requiresInitializationSteps: false);
    }

    private Task SendTargetPositionAsync(int position)
    {
        var normalizedPosition = Math.Clamp(position, 0, TotalTravelSteps);
        return SendMotorControlFrameAsync(
            0x01,
            CreatePositionData(normalizedPosition),
            $"已发送目标位置：{normalizedPosition} step",
            requiresInitializationSteps: true);
    }

    private async Task SendMotorControlFrameAsync(
        byte subCommand,
        ReadOnlyMemory<byte> data,
        string successMessage,
        bool requiresInitializationSteps)
    {
        if (!SerialSettings.IsConnected)
        {
            ShowDeviceInfoReadNotification("请先连接串口");
            return;
        }

        if (requiresInitializationSteps && !HasInitializationSteps)
        {
            ShowDeviceInfoReadNotification("请先读取初始化步数");
            return;
        }

        try
        {
            await deviceCommunication.SendAsync(0x20, subCommand, data);
            ShowDeviceInfoReadNotification(successMessage);
        }
        catch (Exception exception)
        {
            ShowDeviceInfoReadNotification($"发送失败：{exception.Message}");
        }
    }

    private static byte[] CreatePositionData(int position)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, position);

        var firstDataIndex = 0;
        while (firstDataIndex < buffer.Length - 1 && buffer[firstDataIndex] == 0)
        {
            firstDataIndex++;
        }

        return buffer[firstDataIndex..].ToArray();
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
