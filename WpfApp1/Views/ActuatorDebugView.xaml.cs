using System.Windows;
using System.Windows.Controls;

namespace WpfApp1.Views;

public partial class ActuatorDebugView : UserControl
{
    public ActuatorDebugView()
    {
        InitializeComponent();
    }

    private void RememberCurrentPositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
