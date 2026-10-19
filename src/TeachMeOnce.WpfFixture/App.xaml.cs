namespace TeachMeOnce.WpfFixture;

public partial class App : System.Windows.Application
{
    private readonly System.Windows.Threading.DispatcherTimer _exitTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _shutdownEventTimer = new();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        var seconds = ReadExitAfterSeconds(e.Args);
        if (seconds is null)
        {
            ConfigureShutdownEvent(e.Args);
            return;
        }

        _exitTimer.Interval = TimeSpan.FromSeconds(seconds.Value);
        _exitTimer.Tick += (_, _) => Shutdown(0);
        _exitTimer.Start();
        ConfigureShutdownEvent(e.Args);
    }

    private void ConfigureShutdownEvent(string[] args)
    {
        var eventName = ReadArgument(args, "--shutdown-event");
        if (eventName is null)
        {
            return;
        }

        _shutdownEventTimer.Interval = TimeSpan.FromMilliseconds(250);
        _shutdownEventTimer.Tick += (_, _) =>
        {
            try
            {
                using var shutdownEvent = System.Threading.EventWaitHandle.OpenExisting(eventName);
                if (shutdownEvent.WaitOne(0))
                {
                    _shutdownEventTimer.Stop();
                    Shutdown(0);
                }
            }
            catch (System.Threading.WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
        _shutdownEventTimer.Start();
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

    private static string? ReadArgument(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == option && !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
