using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
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
    private readonly MotorCommandService motorCommands;
    private readonly DeviceInformationService deviceInfo;
    private readonly DiagnosticTransactionService diagnostics;
    private CancellationTokenSource? deviceInfoReadNotificationCancellation;
    private bool isDiagnosticActive;

    public ActuatorDebugViewModel(
        SerialSettingsViewModel serialSettings,
        MotorCommandService motorCommands,
        DeviceInformationService deviceInfo,
        DiagnosticTransactionService diagnostics)
    {
        SerialSettings = serialSettings;
        this.motorCommands = motorCommands ?? throw new ArgumentNullException(nameof(motorCommands));
        this.deviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        SerialSettings.PropertyChanged += SerialSettings_PropertyChanged;
        diagnostics.DiagnosticActivityChanged += Diagnostics_DiagnosticActivityChanged;
        TotalTravelSteps = DefaultTotalTravelSteps;
        ClearDeviceInfoValues();
        Presets = new ObservableCollection<MotorPresetViewModel>(CreatePresetViewModels(
            motorPresetStorage.LoadOrCreate(DefaultPresets)));
        EditablePresets = new ObservableCollection<MotorPresetViewModel>();
    }

    public SerialSettingsViewModel SerialSettings { get; }

    public ObservableCollection<MotorPresetViewModel> Presets { get; }

    public ObservableCollection<MotorPresetViewModel> EditablePresets { get; }

    // 0x33 状态显示与 0x15 控制写入均已接入。三个设备版本读取（0x22 F1 80/F1 95/F1 93）已实现；
    // "读取电压和温度参数"协议仍未定义，保持禁用。
    public string ProtocolStatusText =>
        "0x33 状态显示 + 0x15 控制写入已接入；三个设备版本读取已实现，电压温度参数待定义";

    // 诊断进行中（含排队等待期间）禁用电机操作；暂停期间 0x33 不再刷新，显示保持最后一次有效值。
    private bool IsDiagnosticIdle => !isDiagnosticActive;

    // 0x15 写入帧由调度器每 100 ms 发送，串口连接且无诊断在跑时才可下发控制指令。
    public bool IsMotorCommandAvailable => SerialSettings.IsConnected && IsDiagnosticIdle;

    public bool IsSerialCommandAvailable => SerialSettings.IsConnected && IsDiagnosticIdle;

    // "记忆当前位置"只保存到本机配置，不写设备；收到有效 0x33 回复（HasCurrentPosition）即可用。
    public bool IsCurrentPositionMemoryAvailable => HasCurrentPosition;

    [ObservableProperty]
    private int totalTravelSteps;

    // 0x33 回复的负极限位置；目标位置合法范围为 [NegativeLimitSteps, TotalTravelSteps]。
    [ObservableProperty]
    private int negativeLimitSteps;

    [ObservableProperty]
    private int targetPositionSteps;

    // 当前位置由 0x33 回复绑定（字节 0-1），无符号 step。
    [ObservableProperty]
    private int currentPositionSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentPositionMemoryAvailable))]
    private bool hasCurrentPosition;

    public string CurrentPositionSummary =>
        $"当前： {Math.Clamp(CurrentPositionSteps, 0, TotalTravelSteps)} step";

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
    private string bootloaderVersionText = string.Empty;

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

    // 设备状态显示：运动状态与电压/温度/堵转故障，由 0x33 回复驱动。
    // 未收到有效帧时显示"—"（灰色）；正常为绿色，故障为红色。
    private const string StatusNormalColor = "#107C10";
    private const string StatusFaultColor = "#C42B1C";
    private const string StatusIdleColor = "#5C5C5C";
    private const string StatusMovingColor = "#0078D4";

    [ObservableProperty]
    private string motionStatusText = "—";

    [ObservableProperty]
    private string motionStatusColor = StatusIdleColor;

    [ObservableProperty]
    private string stallFaultText = "—";

    [ObservableProperty]
    private string stallFaultColor = StatusIdleColor;

    [ObservableProperty]
    private string undervoltageFaultText = "—";

    [ObservableProperty]
    private string undervoltageFaultColor = StatusIdleColor;

    [ObservableProperty]
    private string overvoltageFaultText = "—";

    [ObservableProperty]
    private string overvoltageFaultColor = StatusIdleColor;

    [ObservableProperty]
    private string overtemperatureFaultText = "—";

    [ObservableProperty]
    private string overtemperatureFaultColor = StatusIdleColor;

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

    // 三个版本读取：串口已连接即可入队（诊断排队期间仍可继续提交）；
    // 电压/温度参数协议未定义，保持禁用。读取结果为版本文本或界面提示，失败不清空已有显示。
    private bool CanReadDeviceInfo(string? operationName)
    {
        return SerialSettings.IsConnected && TryMapVersionKind(operationName, out _);
    }

    [RelayCommand(CanExecute = nameof(CanReadDeviceInfo))]
    private async Task ReadDeviceInfoAsync(string? operationName)
    {
        if (!TryMapVersionKind(operationName, out var kind))
        {
            return;
        }

        var displayName = operationName ?? "设备信息";
        var result = await deviceInfo.ReadVersionAsync(kind).ConfigureAwait(false);
        if (result.Success)
        {
            SetVersionText(kind, result.VersionText ?? string.Empty);
            ShowDeviceInfoReadNotification($"{displayName}读取成功", NotificationLongDuration);
            return;
        }

        // 失败不清空已有版本显示（状态保持最后一次有效读取的结果）。
        ShowDeviceInfoReadNotification($"{displayName}读取失败：{result.FailureText}", NotificationLongDuration);
    }

    private void SetVersionText(DeviceVersionKind kind, string versionText)
    {
        switch (kind)
        {
            case DeviceVersionKind.Bootloader:
                BootloaderVersionText = versionText;
                break;
            case DeviceVersionKind.Firmware:
                FirmwareVersionText = versionText;
                break;
            case DeviceVersionKind.Hardware:
                HardwareVersionText = versionText;
                break;
        }
    }

    private static bool TryMapVersionKind(string? operationName, out DeviceVersionKind kind)
    {
        switch (operationName)
        {
            case "读取Bootloader版本":
                kind = DeviceVersionKind.Bootloader;
                return true;
            case "读取固件版本":
                kind = DeviceVersionKind.Firmware;
                return true;
            case "读取硬件版本":
                kind = DeviceVersionKind.Hardware;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private void Diagnostics_DiagnosticActivityChanged(bool diagnosticActive)
    {
        // 协调层在后台线程上报，界面状态切回 UI 线程更新。
        _ = Application.Current?.Dispatcher.BeginInvoke(() => ApplyDiagnosticActivity(diagnosticActive));
    }

    private void ApplyDiagnosticActivity(bool diagnosticActive)
    {
        isDiagnosticActive = diagnosticActive;
        OnPropertyChanged(nameof(IsMotorCommandAvailable));
        OnPropertyChanged(nameof(IsSerialCommandAvailable));
        ConfirmTargetPositionCommand.NotifyCanExecuteChanged();
        MoveToPresetCommand.NotifyCanExecuteChanged();
        InitializeCalibrationCommand.NotifyCanExecuteChanged();
        EmergencyStopCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        diagnostics.DiagnosticActivityChanged -= Diagnostics_DiagnosticActivityChanged;
        SerialSettings.PropertyChanged -= SerialSettings_PropertyChanged;
        deviceInfoReadNotificationCancellation?.Cancel();
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
            // 生成器不会为属性形式的 CanExecute 自动接线（只有 lambda 求值），
            // 连接状态变化时显式刷新，避免按钮等 WPF 下一次 RequerySuggested 才解禁。
            ConfirmTargetPositionCommand.NotifyCanExecuteChanged();
            MoveToPresetCommand.NotifyCanExecuteChanged();
            InitializeCalibrationCommand.NotifyCanExecuteChanged();
            EmergencyStopCommand.NotifyCanExecuteChanged();
            ReadDeviceInfoCommand.NotifyCanExecuteChanged();
        }
    }

    private void ClearDeviceInfoValues()
    {
        HasCurrentPosition = false;
        CurrentPositionSteps = 0;
        InitializationStepsText = string.Empty;
        BootloaderVersionText = string.Empty;
        RetreatAngleText = string.Empty;
        FirmwareVersionText = string.Empty;
        HardwareVersionText = string.Empty;
        CurrentVoltageText = string.Empty;
        CurrentTemperatureText = string.Empty;
        ClearMotorStatusDisplay();
    }

    /// <summary>
    /// 应用一次 0x33 回复解析结果（须在 UI 线程调用）。
    /// 状态值保持"最后一次有效读取的结果"：只有新回复到达才更新，本地不做超时清零。
    /// </summary>
    public void ApplyMotorStatus(MotorStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);

        CurrentPositionSteps = status.CurrentPositionSteps;
        HasCurrentPosition = true;

        // 正/负极限与目标位置绑定：滑块范围与 step 输入钳制使用 [负极限, 正极限]。
        // 负极限大于正极限属于异常配置，以较大值作为上限避免钳制异常。
        NegativeLimitSteps = status.NegativeLimitSteps;
        TotalTravelSteps = Math.Max(status.PositiveLimitSteps, status.NegativeLimitSteps);

        switch (status.MotionState)
        {
            case MotorMotionState.Stopped:
            case MotorMotionState.Stalled:
                // 堵转期间电机处于停止；堵转本身在"堵转故障"行单独显示。
                MotionStatusText = "停止";
                MotionStatusColor = StatusNormalColor;
                break;
            case MotorMotionState.Moving:
                MotionStatusText = "运动中";
                MotionStatusColor = StatusMovingColor;
                break;
            default:
                MotionStatusText = "保留（0x3）";
                MotionStatusColor = StatusIdleColor;
                break;
        }

        if (status.MotionState == MotorMotionState.Stalled)
        {
            StallFaultText = "堵转";
            StallFaultColor = StatusFaultColor;
        }
        else
        {
            StallFaultText = "正常";
            StallFaultColor = StatusNormalColor;
        }

        if (status.VoltageState == MotorVoltageState.Low)
        {
            UndervoltageFaultText = "欠压";
            UndervoltageFaultColor = StatusFaultColor;
        }
        else
        {
            UndervoltageFaultText = "正常";
            UndervoltageFaultColor = StatusNormalColor;
        }

        if (status.VoltageState == MotorVoltageState.High)
        {
            OvervoltageFaultText = "过压";
            OvervoltageFaultColor = StatusFaultColor;
        }
        else
        {
            OvervoltageFaultText = "正常";
            OvervoltageFaultColor = StatusNormalColor;
        }

        if (status.Overtemperature)
        {
            OvertemperatureFaultText = "过温";
            OvertemperatureFaultColor = StatusFaultColor;
        }
        else
        {
            OvertemperatureFaultText = "正常";
            OvertemperatureFaultColor = StatusNormalColor;
        }
    }

    private void ClearMotorStatusDisplay()
    {
        NegativeLimitSteps = 0;
        TotalTravelSteps = DefaultTotalTravelSteps;
        MotionStatusText = "—";
        MotionStatusColor = StatusIdleColor;
        StallFaultText = "—";
        StallFaultColor = StatusIdleColor;
        UndervoltageFaultText = "—";
        UndervoltageFaultColor = StatusIdleColor;
        OvervoltageFaultText = "—";
        OvervoltageFaultColor = StatusIdleColor;
        OvertemperatureFaultText = "—";
        OvertemperatureFaultColor = StatusIdleColor;
    }

    private static readonly TimeSpan NotificationShortDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NotificationLongDuration = TimeSpan.FromSeconds(3);

    private void ShowDeviceInfoReadNotification(string text)
    {
        ShowDeviceInfoReadNotification(text, NotificationShortDuration);
    }

    private void ShowDeviceInfoReadNotification(string text, TimeSpan duration)
    {
        deviceInfoReadNotificationCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        deviceInfoReadNotificationCancellation = cancellation;
        DeviceInfoReadNotificationText = text;
        IsDeviceInfoReadNotificationVisible = true;
        _ = HideDeviceInfoReadNotificationAsync(cancellation, duration);
    }

    private async Task HideDeviceInfoReadNotificationAsync(CancellationTokenSource cancellation, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, cancellation.Token);
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
        var position = (ushort)Math.Clamp(TargetPositionSteps, 0, ushort.MaxValue);
        motorCommands.SetMoveToPosition(position);
        ShowDeviceInfoReadNotification($"已下发目标位置：{position} step");
    }

    [RelayCommand(CanExecute = nameof(IsMotorCommandAvailable))]
    private void MoveToPreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return;
        }

        int position;
        if (string.Equals(presetId, "close", StringComparison.Ordinal))
        {
            // CLOSE：运动到负极限位置。
            position = NegativeLimitSteps;
        }
        else
        {
            var preset = Presets.FirstOrDefault(item =>
                string.Equals(item.Id, presetId, StringComparison.Ordinal));
            if (preset is null)
            {
                return;
            }

            position = preset.PositionSteps;
        }

        var target = (ushort)Math.Clamp(position, 0, ushort.MaxValue);
        motorCommands.SetMoveToPosition(target);
        ShowDeviceInfoReadNotification($"已下发目标位置：{target} step");
    }

    [RelayCommand(CanExecute = nameof(IsSerialCommandAvailable))]
    private void InitializeCalibration()
    {
        motorCommands.SetInitialize();
        ShowDeviceInfoReadNotification("已下发初始化指令");
    }

    [RelayCommand(CanExecute = nameof(IsSerialCommandAvailable))]
    private void EmergencyStop()
    {
        motorCommands.SetStop();
        ShowDeviceInfoReadNotification("已下发停止指令");
    }

    partial void OnCurrentPositionStepsChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPositionSummary));
    }

    partial void OnTotalTravelStepsChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPositionSummary));
        // 正极限变化后，目标位置可能超出新的合法范围，触发一次钳制。
        TargetPositionSteps = Math.Clamp(TargetPositionSteps, NegativeLimitSteps, value);
    }

    partial void OnNegativeLimitStepsChanged(int value)
    {
        TargetPositionSteps = Math.Clamp(TargetPositionSteps, value, TotalTravelSteps);
    }

    partial void OnTargetPositionStepsChanged(int value)
    {
        var normalizedValue = Math.Clamp(value, NegativeLimitSteps, TotalTravelSteps);
        if (normalizedValue != value)
        {
            TargetPositionSteps = normalizedValue;
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
