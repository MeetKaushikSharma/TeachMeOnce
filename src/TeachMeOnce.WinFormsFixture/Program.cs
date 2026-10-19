namespace TeachMeOnce.WinFormsFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var exitAfterSeconds = ReadExitAfterSeconds(Environment.GetCommandLineArgs());
        System.Windows.Forms.Timer? exitTimer = null;
        if (exitAfterSeconds is not null)
        {
            var timer = new System.Windows.Forms.Timer { Interval = exitAfterSeconds.Value * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.Exit();
            };
            timer.Start();
            exitTimer = timer;
        }

        System.Threading.EventWaitHandle? shutdownEvent = null;
        var shutdownEventName = ReadArgument(Environment.GetCommandLineArgs(), "--shutdown-event");
        System.Windows.Forms.Timer? shutdownEventTimer = null;
        if (shutdownEventName is not null)
        {
            shutdownEventTimer = new System.Windows.Forms.Timer { Interval = 250 };
            shutdownEventTimer.Tick += (_, _) =>
            {
                try
                {
                    shutdownEvent ??= System.Threading.EventWaitHandle.OpenExisting(shutdownEventName);
                    if (shutdownEvent!.WaitOne(0))
                    {
                        shutdownEventTimer?.Stop();
                        Application.Exit();
                    }
                }
                catch (System.Threading.WaitHandleCannotBeOpenedException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            };
            shutdownEventTimer.Start();
        }

        Application.Run(new MainForm());
        exitTimer?.Dispose();
        shutdownEventTimer?.Dispose();
        shutdownEvent?.Dispose();
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
