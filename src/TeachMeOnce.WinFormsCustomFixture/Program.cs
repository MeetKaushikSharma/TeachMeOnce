namespace TeachMeOnce.WinFormsCustomFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var seconds = ReadExitAfterSeconds(Environment.GetCommandLineArgs());
        System.Windows.Forms.Timer? exitTimer = null;
        if (seconds is not null)
        {
            var timer = new System.Windows.Forms.Timer { Interval = seconds.Value * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.Exit();
            };
            timer.Start();
            exitTimer = timer;
        }

        Application.Run(new MainForm());
        exitTimer?.Dispose();
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
