using System.IO;
using System.Text.Json;
using System.Windows;

namespace TeachMeOnce.DeploymentProbe;

public partial class App : Application
{
    internal static DeploymentRuntime Runtime { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Runtime = DeploymentRuntime.Initialize(e.Args);
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            var reportPath = DeploymentRuntime.WriteEarlyErrorReport(exception);
            MessageBox.Show(
                $"TeachMeOnce deployment probe could not start.\n\nError report: {reportPath}",
                "TeachMeOnce Deployment Probe",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

internal sealed class DeploymentRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private DeploymentRuntime(string dataDirectory, string logPath, string statusPath, string? configPath, bool configurationLoaded, int? exitAfterSeconds)
    {
        DataDirectory = dataDirectory;
        LogPath = logPath;
        StatusPath = statusPath;
        ConfigPath = configPath;
        ConfigurationLoaded = configurationLoaded;
        ExitAfterSeconds = exitAfterSeconds;
    }

    public string DataDirectory { get; }
    public string LogPath { get; }
    public string StatusPath { get; }
    public string? ConfigPath { get; }
    public bool ConfigurationLoaded { get; }
    public int? ExitAfterSeconds { get; }

    public static DeploymentRuntime Initialize(string[] args)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "deployment.json");
        var config = File.Exists(configPath)
            ? JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(configPath))
            : null;
        var dataName = config?.DataDirectoryName ?? "TeachMeOnce\\DeploymentProbe";
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dataName);
        Directory.CreateDirectory(dataDirectory);
        var logPath = Path.Combine(dataDirectory, "deployment.log");
        var statusPath = Path.Combine(dataDirectory, "deployment-status.json");
        var runtime = new DeploymentRuntime(dataDirectory, logPath, statusPath, config is null ? null : configPath, config is not null, ReadExitAfterSeconds(args));
        runtime.AppendLog($"startup base={AppContext.BaseDirectory} configLoaded={runtime.ConfigurationLoaded}");
        runtime.WriteStatus("Started", null);
        if (args.Contains("--trigger-error", StringComparer.OrdinalIgnoreCase))
        {
            runtime.CaptureError(new InvalidOperationException("Intentional POC-08 error-reporting probe."));
        }

        return runtime;
    }

    public void AppendLog(string message)
    {
        Directory.CreateDirectory(DataDirectory);
        File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    public void WriteStatus(string state, string? error)
    {
        File.WriteAllText(StatusPath, JsonSerializer.Serialize(new DeploymentStatus
        {
            State = state,
            ConfigurationLoaded = ConfigurationLoaded,
            ConfigPath = ConfigPath,
            DataDirectory = DataDirectory,
            LogPath = LogPath,
            Error = error
        }, JsonOptions));
    }

    public string CaptureError(Exception exception)
    {
        var errorPath = Path.Combine(DataDirectory, "deployment-error.json");
        File.WriteAllText(errorPath, JsonSerializer.Serialize(new DeploymentError
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            TimestampUtc = DateTimeOffset.UtcNow,
            ApplicationBase = AppContext.BaseDirectory
        }, JsonOptions));
        AppendLog($"error type={exception.GetType().Name} report={errorPath}");
        WriteStatus("ErrorCaptured", errorPath);
        return errorPath;
    }

    public static string WriteEarlyErrorReport(Exception exception)
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachMeOnce", "DeploymentProbe");
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "deployment-error.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new DeploymentError
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            TimestampUtc = DateTimeOffset.UtcNow,
            ApplicationBase = AppContext.BaseDirectory
        }, JsonOptions));
        return path;
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

internal sealed class DeploymentConfig
{
    public string? Application { get; init; }
    public string? DataDirectoryName { get; init; }
    public int ConfigurationVersion { get; init; }
}

internal sealed class DeploymentStatus
{
    public required string State { get; init; }
    public bool ConfigurationLoaded { get; init; }
    public string? ConfigPath { get; init; }
    public required string DataDirectory { get; init; }
    public required string LogPath { get; init; }
    public string? Error { get; init; }
}

internal sealed class DeploymentError
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public required string ApplicationBase { get; init; }
}
