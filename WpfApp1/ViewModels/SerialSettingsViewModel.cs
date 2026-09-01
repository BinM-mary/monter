using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

/// <summary>
/// 管理串口枚举、编辑与应用。连接、重连等通信策略由后续通信层负责。
/// </summary>
public partial class SerialSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan PortCheckInactivityThreshold = TimeSpan.FromSeconds(3);

    private readonly ISerialPortDiscoveryService discoveryService;
    private readonly ISerialPortConnectionService connectionService;
    private CancellationTokenSource? portMonitoringCancellationSource;

    public SerialSettingsViewModel(
        ISerialPortDiscoveryService discoveryService,
        ISerialPortConnectionService connectionService)
    {
        this.discoveryService = discoveryService;
        this.connectionService = connectionService;
        BaudRates = new ReadOnlyCollection<int>(new[] { 9_600, 19_200, 38_400, 57_600, 115_200, 230_400 });
        DataBitOptions = new ReadOnlyCollection<int>(new[] { 8, 7 });
        ParityOptions = new ReadOnlyCollection<Parity>(new[] { Parity.None, Parity.Even, Parity.Odd });
        StopBitOptions = new ReadOnlyCollection<StopBits>(new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two });
        AppliedSettings = new SerialPortSettings("未选择", 115_200, 8, Parity.None, StopBits.One, 1_000, 1_000);
    }

    public ObservableCollection<SerialPortOption> AvailablePorts { get; } = new();

    public ReadOnlyCollection<int> BaudRates { get; }

    public ReadOnlyCollection<int> DataBitOptions { get; }

    public ReadOnlyCollection<Parity> ParityOptions { get; }

    public ReadOnlyCollection<StopBits> StopBitOptions { get; }

    [ObservableProperty]
    private bool isFlyoutOpen;

    partial void OnIsFlyoutOpenChanged(bool value)
    {
        if (!value && !IsConnected)
        {
            StopPortMonitoring();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectionPortDisplayText))]
    private bool isConnected;

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value && !IsFlyoutOpen)
        {
            StopPortMonitoring();
        }
    }

    [ObservableProperty]
    private SerialPortOption? selectedPort;

    [ObservableProperty]
    private int selectedBaudRate = 115_200;

    [ObservableProperty]
    private int selectedDataBits = 8;

    [ObservableProperty]
    private Parity selectedParity = Parity.None;

    [ObservableProperty]
    private StopBits selectedStopBits = StopBits.One;

    [ObservableProperty]
    private int readTimeout = 1_000;

    [ObservableProperty]
    private int writeTimeout = 1_000;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedSettingsText))]
    [NotifyPropertyChangedFor(nameof(ConnectionPortDisplayText))]
    private SerialPortSettings appliedSettings;

    public string AppliedSettingsText => AppliedSettings.DisplayText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionPortDisplayText))]
    private string connectionStatusText = "未连接";

    public string ConnectionButtonText => IsConnected ? "断开连接" : "连接设备";

    public string ConnectionPortDisplayText => IsConnected
        ? AppliedSettingsText
        : ConnectionStatusText == "串口已断开" ? "串口已移除" : "未连接";

    [RelayCommand]
    private async Task OpenAsync()
    {
        IsFlyoutOpen = true;
        StartPortMonitoring();
        await LoadAvailablePortsAsync(portMonitoringCancellationSource!.Token);
    }

    [RelayCommand]
    private void Close()
    {
        IsFlyoutOpen = false;
        ValidationMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync("未连接");
            return;
        }

        if (SelectedPort is null || AppliedSettings.PortName == "未选择")
        {
            ConnectionStatusText = "未选择串口";
            ValidationMessage = "请先在串口设置中选择串口并应用设置。";
            return;
        }

        if (!await IsAppliedPortAvailableAsync())
        {
            IsConnected = false;
            ConnectionStatusText = "串口已断开";
            ValidationMessage = string.IsNullOrEmpty(ValidationMessage)
                ? $"未检测到 {AppliedSettings.PortName}，请重新选择可用串口。"
                : ValidationMessage;
            return;
        }

        var connectionResult = await connectionService.ConnectAsync(AppliedSettings);
        if (!connectionResult.IsSuccess)
        {
            IsConnected = false;
            ConnectionStatusText = "连接失败";
            ValidationMessage = $"无法打开 {AppliedSettings.PortName}：{connectionResult.ErrorMessage}";
            return;
        }

        IsConnected = true;
        ConnectionStatusText = "已连接";
        ValidationMessage = string.Empty;
        StartPortMonitoring();
    }

    private void StartPortMonitoring()
    {
        StopPortMonitoring();
        portMonitoringCancellationSource = new CancellationTokenSource();
        _ = MonitorAvailablePortsAsync(portMonitoringCancellationSource.Token);
    }

    private void StopPortMonitoring()
    {
        portMonitoringCancellationSource?.Cancel();
        portMonitoringCancellationSource?.Dispose();
        portMonitoringCancellationSource = null;
    }

    private async Task MonitorAvailablePortsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (ShouldCheckPortAvailability())
                {
                    await LoadAvailablePortsAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 面板关闭时取消轮询，不需要向用户显示错误。
        }
    }

    private async Task LoadAvailablePortsAsync(CancellationToken cancellationToken)
    {
        var preferredPortName = SelectedPort?.Name;
        ValidationMessage = string.Empty;

        try
        {
            var discoveredPorts = await discoveryService.GetAvailablePortsAsync(cancellationToken);
            if (HasPortListChanged(discoveredPorts))
            {
                ReplaceAvailablePorts(discoveredPorts);
                SelectedPort = FindPreferredPort(preferredPortName);
            }

            await DisconnectIfConnectedPortWasRemovedAsync();
        }
        catch (OperationCanceledException)
        {
            // 关闭设置面板会取消当前一次枚举。
        }
        catch (Exception exception)
        {
            ValidationMessage = exception.Message;
        }
    }

    private bool ShouldCheckPortAvailability()
    {
        // 设置面板打开时，用户需要实时看到插拔变化；系统端口枚举不读取串口缓冲区，可与接收并行。
        return IsFlyoutOpen
            || !IsConnected
            || !connectionService.HasRecentReceiveActivity(PortCheckInactivityThreshold);
    }

    private async Task<bool> IsAppliedPortAvailableAsync()
    {
        try
        {
            var discoveredPorts = await discoveryService.GetAvailablePortsAsync();
            if (HasPortListChanged(discoveredPorts))
            {
                var preferredPortName = SelectedPort?.Name;
                ReplaceAvailablePorts(discoveredPorts);
                SelectedPort = FindPreferredPort(preferredPortName);
            }

            return discoveredPorts.Any(port => port.Name.Equals(AppliedSettings.PortName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            ValidationMessage = $"串口检测失败：{exception.Message}";
            return false;
        }
    }

    private void ReplaceAvailablePorts(IReadOnlyList<SerialPortOption> discoveredPorts)
    {
        AvailablePorts.Clear();
        foreach (var port in discoveredPorts)
        {
            AvailablePorts.Add(port);
        }
    }

    private bool HasPortListChanged(IReadOnlyList<SerialPortOption> discoveredPorts)
    {
        return AvailablePorts.Count != discoveredPorts.Count
            || !AvailablePorts.Select(port => port.Name)
                .SequenceEqual(discoveredPorts.Select(port => port.Name), StringComparer.OrdinalIgnoreCase);
    }

    private SerialPortOption? FindPreferredPort(string? preferredPortName)
    {
        return AvailablePorts.FirstOrDefault(port => port.Name.Equals(preferredPortName, StringComparison.OrdinalIgnoreCase))
            ?? AvailablePorts.FirstOrDefault(port => port.Name.Equals(AppliedSettings.PortName, StringComparison.OrdinalIgnoreCase))
            ?? (AvailablePorts.Count == 1 ? AvailablePorts[0] : null);
    }

    private async Task DisconnectIfConnectedPortWasRemovedAsync()
    {
        var connectedPortName = connectionService.ConnectedPortName;
        if (connectedPortName is null || AvailablePorts.Any(port => port.Name.Equals(connectedPortName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await DisconnectAsync("串口已断开");
    }

    private async Task DisconnectAsync(string statusText)
    {
        try
        {
            await connectionService.DisconnectAsync();
        }
        catch (Exception exception)
        {
            ValidationMessage = $"串口释放异常：{exception.Message}";
        }
        finally
        {
            IsConnected = false;
            ConnectionStatusText = statusText;
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (SelectedPort is null)
        {
            ValidationMessage = "请先选择一个已检测到的串口。";
            return;
        }

        if (ReadTimeout <= 0 || WriteTimeout <= 0)
        {
            ValidationMessage = "读写超时必须大于 0 毫秒。";
            return;
        }

        AppliedSettings = new SerialPortSettings(
            SelectedPort.Name,
            SelectedBaudRate,
            SelectedDataBits,
            SelectedParity,
            SelectedStopBits,
            ReadTimeout,
            WriteTimeout);

        ValidationMessage = string.Empty;
        IsFlyoutOpen = false;
    }
}
