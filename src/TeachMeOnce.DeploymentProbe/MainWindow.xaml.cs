using System.Windows;

namespace TeachMeOnce.DeploymentProbe;

public partial class MainWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer? _exitTimer;

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = App.Runtime.ConfigurationLoaded ? "Status: deployment ready" : "Status: configuration missing";
        if (App.Runtime.ExitAfterSeconds is int seconds)
        {
            _exitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _exitTimer.Tick += (_, _) => Application.Current.Shutdown(0);
            _exitTimer.Start();
        }
    }

    private void ProbeActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        App.Runtime.AppendLog("action invoked");
        App.Runtime.WriteStatus("ActionInvoked", null);
        StatusText.Text = "Status: action invoked";
    }
}
