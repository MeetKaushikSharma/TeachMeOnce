using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace TeachMeOnce.DeploymentSmoke;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static int Main(string[] args)
    {
        var options = Arguments.Parse(args);
        var report = new DeploymentSmokeReport
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Mode = options.Mode,
            ApplicationPath = options.ApplicationPath,
            Status = "NotRun",
            CleanMachineTested = false,
            RuntimePrerequisite = options.Mode.StartsWith("fdd", StringComparison.OrdinalIgnoreCase)
                ? ".NET 10 Windows Desktop Runtime x64 is required."
                : "No separately installed .NET runtime is required by the application publish."
        };

        try
        {
            report = Run(options, report);
            WriteReport(report, options.OutputPath);
            Console.WriteLine($"Deployment smoke {report.Status}: mode={report.Mode}, UIA={report.UiAutomationPassed}, config={report.ConfigurationPassed}, data={report.WritableDataPassed}, logging={report.LoggingPassed}, errors={report.ErrorReportingPassed}.");
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException or ElementNotAvailableException)
        {
            report = report with
            {
                Status = "Failed",
                Passed = false,
                Failure = $"{exception.GetType().Name}: {exception.Message}"
            };
            WriteReport(report, options.OutputPath);
            Console.Error.WriteLine($"Deployment smoke failed: {report.Failure}");
            return 2;
        }
    }

    private static DeploymentSmokeReport Run(Arguments options, DeploymentSmokeReport report)
    {
        if (!File.Exists(options.ApplicationPath))
        {
            throw new InvalidOperationException($"Published application was not found: {options.ApplicationPath}");
        }

        var applicationDirectory = Path.GetDirectoryName(options.ApplicationPath)!;
        var configPath = Path.Combine(applicationDirectory, "deployment.json");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException("Published deployment.json was not present beside the application.");
        }

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachMeOnce", "DeploymentProbe");
        Directory.CreateDirectory(dataDirectory);
        var statusPath = Path.Combine(dataDirectory, "deployment-status.json");
        var logPath = Path.Combine(dataDirectory, "deployment.log");
        var errorPath = Path.Combine(dataDirectory, "deployment-error.json");
        foreach (var oldFile in new[] { statusPath, logPath, errorPath })
        {
            if (File.Exists(oldFile))
            {
                File.Delete(oldFile);
            }
        }

        Process? process = null;
        try
        {
            process = StartApplication(options.ApplicationPath, "--exit-after-seconds 30");
            var window = WaitForWindow(process.Id);
            var action = FindUnique(window, "DeploymentActionButton");
            var status = FindUnique(window, "DeploymentStatusText");
            var processId = ReadInt(window, AutomationElement.ProcessIdProperty);
            if (processId is null || processId.Value != process.Id)
            {
                throw new InvalidOperationException($"UI Automation process identity mismatch: expected {process.Id}, got {processId?.ToString() ?? "none"}.");
            }
            var automationProcessId = processId.Value;

            if (!action.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
                || patternObject is not InvokePattern pattern)
            {
                throw new InvalidOperationException("DeploymentActionButton did not expose InvokePattern.");
            }

            pattern.Invoke();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (string.Equals(ReadString(status, AutomationElement.NameProperty), "Status: action invoked", StringComparison.Ordinal))
                {
                    break;
                }

                Thread.Sleep(50);
            }

            var statusJson = WaitForFile(statusPath, 3000);
            var statusRecord = JsonSerializer.Deserialize<DeploymentStatusRecord>(File.ReadAllText(statusJson), JsonOptions);
            var actionConfirmed = string.Equals(ReadString(status, AutomationElement.NameProperty), "Status: action invoked", StringComparison.Ordinal);
            var loggingPassed = File.Exists(logPath) && File.ReadAllText(logPath).Contains("action invoked", StringComparison.Ordinal);
            var writableDataPassed = statusRecord is not null && Directory.Exists(statusRecord.DataDirectory);

            CloseProcess(process);
            process = null;

            var errorProcess = StartApplication(options.ApplicationPath, "--trigger-error --exit-after-seconds 3");
            try
            {
                WaitForFile(Path.Combine(dataDirectory, "deployment-error.json"), 3000);
                errorProcess.WaitForExit(5000);
            }
            finally
            {
                CloseProcess(errorProcess);
            }

            var errorReportingPassed = File.Exists(errorPath)
                && File.ReadAllText(errorPath).Contains("Intentional POC-08 error-reporting probe.", StringComparison.Ordinal);

            return report with
            {
                Status = actionConfirmed && statusRecord?.ConfigurationLoaded == true && loggingPassed && writableDataPassed && errorReportingPassed ? "Passed" : "Failed",
                Passed = actionConfirmed && statusRecord?.ConfigurationLoaded == true && loggingPassed && writableDataPassed && errorReportingPassed,
                UiAutomationPassed = true,
                ConfigurationPassed = statusRecord?.ConfigurationLoaded == true && File.Exists(configPath),
                WritableDataPassed = writableDataPassed,
                LoggingPassed = loggingPassed,
                ErrorReportingPassed = errorReportingPassed,
                ApplicationProcessId = automationProcessId,
                PublishedFileCount = Directory.EnumerateFiles(applicationDirectory, "*", SearchOption.TopDirectoryOnly).Count()
            };
        }
        finally
        {
            if (process is not null)
            {
                CloseProcess(process);
            }
        }
    }

    private static Process StartApplication(string applicationPath, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = applicationPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException($"Could not launch published application '{applicationPath}'.");
        Thread.Sleep(300);
        process.Refresh();
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Published application exited during startup with code {process.ExitCode}.");
        }

        return process;
    }

    private static AutomationElement WaitForWindow(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (window is not null)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"No UI Automation window was found for published process {processId}.");
    }

    private static AutomationElement FindUnique(AutomationElement window, string automationId)
    {
        var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException($"Expected one published UIA target '{automationId}', found {matches.Count}.");
    }

    private static string WaitForFile(string path, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return path;
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException($"Expected published application file was not created: {path}");
    }

    private static void CloseProcess(Process process)
    {
        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    _ = process.CloseMainWindow();
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill(entireProcessTree: false);
                    }
                }
                else
                {
                    process.Kill(entireProcessTree: false);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int? ReadInt(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is int integer ? integer : null;
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : value?.ToString();
    }

    private static void WriteReport(DeploymentSmokeReport report, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
    }

    private sealed class Arguments
    {
        public string ApplicationPath { get; private init; } = string.Empty;
        public string Mode { get; private init; } = string.Empty;
        public string OutputPath { get; private init; } = string.Empty;

        public static Arguments Parse(string[] args)
        {
            string? applicationPath = null;
            string? mode = null;
            string? outputPath = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--app": applicationPath = Next(args, ref index, "--app"); break;
                    case "--mode": mode = Next(args, ref index, "--mode"); break;
                    case "--output": outputPath = Next(args, ref index, "--output"); break;
                    default: throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(applicationPath) || string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Usage: deployment-smoke --app path --mode mode --output report");
            }

            return new Arguments { ApplicationPath = Path.GetFullPath(applicationPath), Mode = mode, OutputPath = Path.GetFullPath(outputPath) };
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            return args[index];
        }
    }

    private sealed class DeploymentStatusRecord
    {
        public bool ConfigurationLoaded { get; init; }
        public required string DataDirectory { get; init; }
    }
}

internal sealed record DeploymentSmokeReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Mode { get; init; }
    public required string ApplicationPath { get; init; }
    public required string RuntimePrerequisite { get; init; }
    public bool CleanMachineTested { get; init; }
    public int ApplicationProcessId { get; init; }
    public int PublishedFileCount { get; init; }
    public bool UiAutomationPassed { get; init; }
    public bool ConfigurationPassed { get; init; }
    public bool WritableDataPassed { get; init; }
    public bool LoggingPassed { get; init; }
    public bool ErrorReportingPassed { get; init; }
    public required string Status { get; init; }
    public bool Passed { get; init; }
    public string? Failure { get; init; }
}
