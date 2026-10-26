using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public partial class ActuatorDebugViewModel : ObservableObject
{
    private const int DefaultTotalTravelSteps = 12_000;

    /// <summary>
    /// 目标位置输入上限：0x15 帧的目标位置是 16 bit 无符号（协议/PROTOCOL.md §5.2），
    /// 所以输入框允许 0～65535 的较大步数，不再按设备回报的行程值截断用户输入。
    /// </summary>
    public const int MaxTargetPositionSteps = ushort.MaxValue;

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

    // 0x33 状态显示、0x34 温度/电压数值与 0x15 控制写入均已接入。
    // 三个设备版本读取（0x22 F1 80/F1 95/F1 93）已实现。
    public string ProtocolStatusText =>
        "0x33 状态 + 0x34 温度/电压 + 0x15 控制已接入；三个设备版本读取已实现";

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

    /// <summary>
    /// 目标位置输入框的原始文本。输入框不直接绑定 <see cref="TargetPositionSteps"/>：
    /// 超过 16 bit 上限（65535）或非数字的输入会原样保留，由"确定"统一提示并拒绝下发，
    /// 避免绑定层把 70000 静默改成 65535、或把无法解析的文本悄悄退回旧值。
    /// </summary>
    [ObservableProperty]
    private string targetPositionText = "0";

    // 当前位置由 0x33 回复绑定（字节 0-1），无符号 step。
    [ObservableProperty]
    private int currentPositionSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentPositionMemoryAvailable))]
    private bool hasCurrentPosition;

    public string CurrentPositionSummary =>
        $"当前： {Math.Clamp(CurrentPositionSteps, 0, SliderMaximumSteps)} step";

    /// <summary>
    /// 滑块范围上限：设备回报的行程（与正/负极限取大者），至少为 1，
    /// 避免范围长度为 0 时滑块无法拖动、也不再出现"最小 &gt; 最大"的非法范围。
    /// </summary>
    public int SliderMaximumSteps => Math.Max(1, Math.Max(TotalTravelSteps, NegativeLimitSteps));

    /// <summary>
    /// 滑块范围下限：设备回报的负极限，收敛到 [0, 上限]。
    /// 设备若把负极限按补码回报（例如 -224 报成 0xFF20 = 65312），这里按 0 兜底显示。
    /// </summary>
    public int SliderMinimumSteps => Math.Clamp(NegativeLimitSteps, 0, SliderMaximumSteps);

    /// <summary>
    /// 滑块当前位置：只把目标位置收敛到滑块范围用于显示，<b>不回写</b>目标位置。
    /// 这样输入框里收到的较大步数（如 65312）不会被滑块范围吞掉，也避免 RangeBase 出现 Value &gt; Maximum 的中间态。
    /// </summary>
    public int TargetPositionSliderValue
    {
        get => Math.Clamp(TargetPositionSteps, SliderMinimumSteps, SliderMaximumSteps);
        set
        {
            if (value == TargetPositionSteps)
            {
                return;
            }

            TargetPositionSteps = value;
            OnPropertyChanged();
        }
    }

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
    private string stallRetreatStepsText = string.Empty;

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

    // 0x33 新增状态：初始化、开路、短路与马达故障汇总。
    [ObservableProperty]
    private string initializationStatusText = "—";

    [ObservableProperty]
    private string initializationStatusColor = StatusIdleColor;

    [ObservableProperty]
    private string openCircuitFaultText = "—";

    [ObservableProperty]
    private string openCircuitFaultColor = StatusIdleColor;

    [ObservableProperty]
    private string shortCircuitFaultText = "—";

    [ObservableProperty]
    private string shortCircuitFaultColor = StatusIdleColor;

    [ObservableProperty]
    private string motorFaultText = "—";

    [ObservableProperty]
    private string motorFaultColor = StatusIdleColor;

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

    // 三个版本读取：串口已连接即可入队（诊断排队期间仍可继续提交）。
    // 温度/电压已改由 0x34 周期读取，不再通过诊断按钮触发。
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
        StallRetreatStepsText = string.Empty;
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

        // 行程与负极限先行规范化：设备可能把负极限按补码回报（-224 报成 0xFF20 = 65312），
        // 这种值不能直接当作上界，否则界面会出现“最小 > 最大”的非法区间。
        // 判据：负极限不超过正极限才认为区间有效；无效时负极限退回 0，行程取正极限。
        var positiveLimitSteps = (int)status.PositiveLimitSteps;
        var negativeLimitSteps = (int)status.NegativeLimitSteps;
        var rangeIsUsable = negativeLimitSteps <= positiveLimitSteps;
        var reportedTravelSteps = rangeIsUsable ? Math.Max(positiveLimitSteps, negativeLimitSteps) : positiveLimitSteps;

        TotalTravelSteps = reportedTravelSteps > 0 ? reportedTravelSteps : DefaultTotalTravelSteps;
        NegativeLimitSteps = Math.Clamp(rangeIsUsable ? negativeLimitSteps : 0, 0, TotalTravelSteps);

        switch (status.InitializationState)
        {
            case MotorInitializationState.Completed:
                InitializationStatusText = "完成";
                InitializationStatusColor = StatusNormalColor;
                break;
            case MotorInitializationState.InProgress:
                InitializationStatusText = "初始化中";
                InitializationStatusColor = StatusMovingColor;
                break;
            case MotorInitializationState.Error:
                InitializationStatusText = "错误";
                InitializationStatusColor = StatusFaultColor;
                break;
            default:
                InitializationStatusText = "未定义";
                InitializationStatusColor = StatusIdleColor;
                break;
        }

        switch (status.MotionState)
        {
            case MotorMotionState.Stopped:
                MotionStatusText = "停止";
                MotionStatusColor = StatusNormalColor;
                break;
            case MotorMotionState.Moving:
                MotionStatusText = "运动中";
                MotionStatusColor = StatusMovingColor;
                break;
            case MotorMotionState.Stalled:
                MotionStatusText = "堵转";
                MotionStatusColor = StatusFaultColor;
                break;
            case MotorMotionState.OpenCircuit:
                MotionStatusText = "开路";
                MotionStatusColor = StatusFaultColor;
                break;
            case MotorMotionState.ShortCircuit:
                MotionStatusText = "短路";
                MotionStatusColor = StatusFaultColor;
                break;
            default:
                MotionStatusText = $"保留（0x{(int)status.MotionState:X}）";
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

        if (status.MotionState == MotorMotionState.OpenCircuit)
        {
            OpenCircuitFaultText = "开路";
            OpenCircuitFaultColor = StatusFaultColor;
        }
        else
        {
            OpenCircuitFaultText = "正常";
            OpenCircuitFaultColor = StatusNormalColor;
        }

        if (status.MotionState == MotorMotionState.ShortCircuit)
        {
            ShortCircuitFaultText = "短路";
            ShortCircuitFaultColor = StatusFaultColor;
        }
        else
        {
            ShortCircuitFaultText = "正常";
            ShortCircuitFaultColor = StatusNormalColor;
        }

        var hasMotorFault = status.MotionState is MotorMotionState.Stalled
            or MotorMotionState.OpenCircuit
            or MotorMotionState.ShortCircuit;
        MotorFaultText = hasMotorFault ? "故障" : "正常";
        MotorFaultColor = hasMotorFault ? StatusFaultColor : StatusNormalColor;

        // 字节 7：堵转回退步数，单位 step。
        StallRetreatStepsText = $"{status.StallRetreatSteps} step";

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

    /// <summary>
    /// 应用一次 0x34 回复解析结果（须在 UI 线程调用）。
    /// 非有限值（NaN/Infinity）视为无效读取，保留上一次有效显示。
    /// 温度、电压的单位由设备侧约定，界面只显示数值，不附加倍率或单位。
    /// </summary>
    public void ApplyMotorMeasurements(MotorMeasurementSnapshot measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (float.IsFinite(measurement.Temperature))
        {
            CurrentTemperatureText = measurement.Temperature.ToString("0.00", CultureInfo.InvariantCulture);
        }

        if (float.IsFinite(measurement.Voltage))
        {
            CurrentVoltageText = measurement.Voltage.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private void ClearMotorStatusDisplay()
    {
        NegativeLimitSteps = 0;
        TotalTravelSteps = DefaultTotalTravelSteps;
        InitializationStatusText = "—";
        InitializationStatusColor = StatusIdleColor;
        MotionStatusText = "—";
        MotionStatusColor = StatusIdleColor;
        OpenCircuitFaultText = "—";
        OpenCircuitFaultColor = StatusIdleColor;
        ShortCircuitFaultText = "—";
        ShortCircuitFaultColor = StatusIdleColor;
        MotorFaultText = "—";
        MotorFaultColor = StatusIdleColor;
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
                MaxTargetPositionSteps));
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
        // 输入框允许自由输入，但保存前必须逐项确认落在 0～65535：超出上限的值不会被静默改写成 65535。
        var invalidPreset = EditablePresets.FirstOrDefault(preset => preset.PositionInputError is not null);
        if (invalidPreset is not null)
        {
            PresetConfigurationMessage = $"{invalidPreset.DisplayName}：{invalidPreset.PositionInputError}";
            return;
        }

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
        var position = Math.Clamp(CurrentPositionSteps, 0, MaxTargetPositionSteps);
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
        // 超出 16 bit 上限（65535）不截断后下发：0x15 只有 2 字节位置字段，
        // 硬发会让 70000 之类回绕成 4464 这样的完全不同的位置，宁可拒绝并说明原因。
        if (!TryParseTargetPositionText(TargetPositionText, out var position))
        {
            ShowDeviceInfoReadNotification(DescribeTargetPositionInputError(TargetPositionText));
            return;
        }

        motorCommands.SetMoveToPosition((ushort)position);
        // 超出行程只做提示，不拦截：较大步数值仍然照原样下发（0x15 目标位置为 16 bit 无符号）。
        ShowDeviceInfoReadNotification(position > TotalTravelSteps
            ? $"已下发目标位置：{position} step（超出行程 {TotalTravelSteps} step）"
            : $"已下发目标位置：{position} step");
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
        NotifySliderRangeChanged();
    }

    partial void OnNegativeLimitStepsChanged(int value)
    {
        NotifySliderRangeChanged();
    }

    partial void OnTargetPositionTextChanged(string value)
    {
        // 只有能解析且落在协议范围内的输入才实时同步到目标位置（滑块随之移动）；
        // 超限/非法文本原样保留，等"确定"时统一提示，不在输入过程中改写用户输入。
        if (TryParseTargetPositionText(value, out var position))
        {
            TargetPositionSteps = position;
        }
    }

    partial void OnTargetPositionStepsChanged(int value)
    {
        // 输入框只受 16 bit 协议上限约束：设备回报的行程仅作参考，
        // 不再把用户输入的较大步数强制改写成行程值。
        var normalizedValue = Math.Clamp(value, 0, MaxTargetPositionSteps);
        if (normalizedValue != value)
        {
            TargetPositionSteps = normalizedValue;
            return;
        }

        // 滑块或预设改动目标位置时把文本同步过去；用户正在输入且文本已是同一数值时不打断。
        if (!TryParseTargetPositionText(TargetPositionText, out var textValue) || textValue != value)
        {
            TargetPositionText = value.ToString(CultureInfo.InvariantCulture);
        }

        OnPropertyChanged(nameof(TargetPositionSliderValue));
    }

    private static bool TryParseTargetPositionText(string? text, out int position)
    {
        position = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0
            || parsed > MaxTargetPositionSteps)
        {
            return false;
        }

        position = (int)parsed;
        return true;
    }

    private static string DescribeTargetPositionInputError(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "请先输入目标位置（0～65535 step）。";
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            // 位数多到 long 都装不下时同样是"太大"，不用报成无法识别。
            return trimmed.All(char.IsDigit)
                ? $"目标位置 {trimmed} 超过 16 bit 上限 {MaxTargetPositionSteps} step，未下发。"
                : $"目标位置需为 0～65535 的整数，当前“{trimmed}”无法识别，未下发。";
        }

        return parsed < 0
            ? $"目标位置不能为负数（当前 {parsed}），未下发。"
            : $"目标位置 {parsed} 超过 16 bit 上限 {MaxTargetPositionSteps} step，未下发。";
    }

    private void NotifySliderRangeChanged()
    {
        OnPropertyChanged(nameof(SliderMaximumSteps));
        OnPropertyChanged(nameof(SliderMinimumSteps));
        OnPropertyChanged(nameof(TargetPositionSliderValue));
    }

    private IEnumerable<MotorPresetViewModel> CreatePresetViewModels(
        IEnumerable<MotorPresetConfigurationItem> configuration)
    {
        var positionsById = configuration.ToDictionary(preset => preset.Id, preset => preset.PositionSteps, StringComparer.Ordinal);
        return DefaultPresets.Select(defaultPreset => new MotorPresetViewModel(
            defaultPreset.Id,
            PresetDisplayNames[defaultPreset.Id],
            positionsById.GetValueOrDefault(defaultPreset.Id, defaultPreset.PositionSteps),
            MaxTargetPositionSteps));
    }
}
