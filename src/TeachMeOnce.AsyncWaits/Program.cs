using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;

namespace TeachMeOnce.AsyncWaits;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(Arguments.Parse(args)).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"POC-04 async waits failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static async Task<int> RunAsync(Arguments options)
    {
        using var process = Process.GetProcessById(options.ProcessId);
        var waiter = new UiAutomationWaiter(TimeSpan.FromMilliseconds(50));
        var cases = new List<WaitCaseReport>();

        var mainWindow = await waiter.WaitForWindowAsync(
            process.Id,
            "TeachMeOnce",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        EnsureSatisfied(mainWindow, "main fixture window");

        var delayedButton = RequireElement(mainWindow.Value!, "DelayedDialogButton");
        Invoke(delayedButton, "DelayedDialogButton");

        var delayedElement = await waiter.WaitForElementAsync(
            mainWindow.Value!,
            "DelayedElement",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        cases.Add(ToCase("delayed element appearance", WaitStatus.Satisfied, delayedElement));
        EnsureSatisfied(delayedElement, "delayed element");

        var enabled = await waiter.WaitForEnabledAsync(
            delayedElement.Value!,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        cases.Add(ToCase("condition satisfaction", WaitStatus.Satisfied, enabled));
        EnsureSatisfied(enabled, "enabled delayed element");

        var timeout = await waiter.WaitForWindowAsync(
            process.Id,
            "POC-04 window that will never appear",
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None);
        cases.Add(ToCase("bounded timeout", WaitStatus.TimedOut, timeout));
        if (timeout.ProbeCount < 2)
        {
            throw new InvalidOperationException("The timeout wait made fewer than two probes; polling behavior was not exercised.");
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var cancelled = await waiter.WaitForWindowAsync(
            process.Id,
            "POC-04 window that will never appear",
            TimeSpan.FromSeconds(10),
            cancellation.Token);
        cases.Add(ToCase("prompt cancellation", WaitStatus.Cancelled, cancelled));
        if (cancelled.Elapsed > TimeSpan.FromSeconds(2))
        {
            throw new InvalidOperationException($"Cancellation took too long: {cancelled.Elapsed.TotalMilliseconds:0} ms.");
        }

        var missing = waiter.ResolveElementOnce(mainWindow.Value!, "Poc04MissingElement");
        cases.Add(ToCase("missing target", WaitStatus.NotFound, missing));

        var report = new WaitReport
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = process.Id,
            ProcessName = process.ProcessName,
            Cases = cases,
            Passed = cases.All(testCase => testCase.Passed)
        };

        WriteReport(report, options.OutputPath);
        if (!report.Passed)
        {
            Console.Error.WriteLine("POC-04 async wait checks failed.");
            return 1;
        }

        Console.WriteLine($"POC-04 async wait checks passed: {cases.Count} cases.");
        return 0;
    }

    private static WaitCaseReport ToCase(string name, WaitStatus expected, WaitResult<AutomationElement> result)
    {
        return new WaitCaseReport
        {
            Name = name,
            ExpectedStatus = expected.ToString(),
            ActualStatus = result.Status.ToString(),
            Passed = result.Status == expected,
            Reason = result.Reason,
            ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
            ProbeCount = result.ProbeCount,
            UiAutomationErrorCount = result.UiAutomationErrorCount
        };
    }

    private static AutomationElement RequireElement(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return window.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException($"Fixture element '{automationId}' was not found.");
    }

    private static void Invoke(AutomationElement element, string description)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
            || patternObject is not InvokePattern pattern)
        {
            throw new InvalidOperationException($"Element '{description}' does not expose InvokePattern.");
        }

        pattern.Invoke();
    }

    private static void EnsureSatisfied(WaitResult<AutomationElement> result, string description)
    {
        if (result.Status != WaitStatus.Satisfied || result.Value is null)
        {
            throw new InvalidOperationException($"Could not find {description}: {result.Reason}");
        }
    }

    private static void WriteReport(WaitReport report, string? outputPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Async wait report written to {Path.GetFullPath(outputPath)}");
    }

    private sealed class Arguments
    {
        public const string Usage = "Usage: async-waits --pid id [--output path]";

        public int ProcessId { get; private init; }
        public string? OutputPath { get; private init; }

        public static Arguments Parse(string[] args)
        {
            int? processId = null;
            string? outputPath = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pid":
                        processId = int.Parse(Next(args, ref index, "--pid"));
                        break;
                    case "--output":
                        outputPath = Next(args, ref index, "--output");
                        break;
                    case "--help":
                        throw new InvalidOperationException(Usage);
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (!processId.HasValue || processId.Value <= 0)
            {
                throw new ArgumentException("--pid is required and must be positive.");
            }

            return new Arguments { ProcessId = processId.Value, OutputPath = outputPath };
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
}

internal sealed class WaitReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required IReadOnlyList<WaitCaseReport> Cases { get; init; }
    public bool Passed { get; init; }
}

internal sealed class WaitCaseReport
{
    public required string Name { get; init; }
    public required string ExpectedStatus { get; init; }
    public required string ActualStatus { get; init; }
    public bool Passed { get; init; }
    public required string Reason { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public int ProbeCount { get; init; }
    public int UiAutomationErrorCount { get; init; }
}
