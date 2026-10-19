namespace TeachMeOnce.WpfCustomFixture;

public partial class App : System.Windows.Application
{
    private readonly System.Windows.Threading.DispatcherTimer _exitTimer = new();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        var seconds = ReadExitAfterSeconds(e.Args);
        if (seconds is null)
        {
            return;
        }

        _exitTimer.Interval = TimeSpan.FromSeconds(seconds.Value);
        _exitTimer.Tick += (_, _) => Shutdown(0);
        _exitTimer.Start();
    }

    private static int? ReadExitAfterSeconds(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == "--exit-after-seconds" && int.TryParse(args[index + 1], out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }

        return null;
    }
}
