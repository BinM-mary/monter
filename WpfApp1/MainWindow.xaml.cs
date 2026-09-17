using System.Text;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using MahApps.Metro.Controls;
using WpfApp1.ViewModels;
using WpfApp1.Views;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        private const double ExpandedPaneLength = 190;
        private readonly MainWindowViewModel viewModel;
        private ProtocolLogWindow? protocolLogWindow;

        public static readonly DependencyProperty IsNavigationExpandedProperty =
            DependencyProperty.Register(
                nameof(IsNavigationExpanded),
                typeof(bool),
                typeof(MainWindow),
                new PropertyMetadata(true));

        public bool IsNavigationExpanded
        {
            get => (bool)GetValue(IsNavigationExpandedProperty);
            set => SetValue(IsNavigationExpandedProperty, value);
        }

        public MainWindow()
        {
            InitializeComponent();

            viewModel = new MainWindowViewModel();
            DataContext = viewModel;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            protocolLogWindow?.CloseForApplicationExit();
        }

        private void ProtocolLogWindow_Click(object sender, RoutedEventArgs e)
        {
            if (protocolLogWindow is null)
            {
                protocolLogWindow = new ProtocolLogWindow(viewModel.CommunicationLog)
                {
                    Owner = this
                };
                protocolLogWindow.Closed += (_, _) => protocolLogWindow = null;
            }

            if (!protocolLogWindow.IsVisible)
            {
                protocolLogWindow.Show();
            }

            protocolLogWindow.Activate();
        }

        private void MainHamburgerMenu_HamburgerButtonClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            IsNavigationExpanded = !IsNavigationExpanded;

            var targetWidth = IsNavigationExpanded
                ? ExpandedPaneLength
                : MainHamburgerMenu.CompactPaneLength;

            var animation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            MainHamburgerMenu.BeginAnimation(HamburgerMenu.OpenPaneLengthProperty, animation);
        }
    }
}
