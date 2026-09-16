using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfApp1.Services;

namespace WpfApp1.ViewModels;

public sealed partial class ProtocolLogWindowViewModel : ObservableObject
{
    private readonly ICommunicationLogService communicationLog;

    public ProtocolLogWindowViewModel(ICommunicationLogService communicationLog)
    {
        this.communicationLog = communicationLog;
    }

    public ICommunicationLogService CommunicationLog => communicationLog;

    [RelayCommand]
    private void Clear()
    {
        communicationLog.Clear();
    }
}
