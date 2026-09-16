using MahApps.Metro.Controls;
using WpfApp1.Services;
using WpfApp1.ViewModels;

namespace WpfApp1.Views;

public partial class ProtocolLogWindow : MetroWindow
{
    public ProtocolLogWindow(ICommunicationLogService communicationLog)
    {
        InitializeComponent();
        DataContext = new ProtocolLogWindowViewModel(communicationLog);
    }
}
