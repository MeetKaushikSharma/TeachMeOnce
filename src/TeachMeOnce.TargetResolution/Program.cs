using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace TeachMeOnce.TargetResolution;

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
            var options = Arguments.Parse(args);
            var process = Process.GetProcessById((int)options.ProcessId);
            var processName = process.ProcessName;
            var window = WaitForWindow(process.Id);
            var target = FindSingle(window, "ValueTextBox");
            var descriptor = TargetDescriptorBuilder.Build(window, target);
            var resolver = new TargetResolver();
            var cases = new List<ResolutionCaseReport>();

            var initial = resolver.Resolve(window, descriptor);
            cases.Add(RecordCase("initial unique match", ResolutionStatus.Resolved, initial));

            Invoke(window, "RemoveValueButton");
            var missing = WaitForStatus(window, descriptor, resolver, ResolutionStatus.NotFound);
            cases.Add(RecordCase("missing control", ResolutionStatus.NotFound, missing));

            Invoke(window, "RecreateValueButton");
            var reacquired = WaitForStatus(window, descriptor, resolver, ResolutionStatus.Resolved);
            cases.Add(RecordCase("reacquisition after UI recreation", ResolutionStatus.Resolved, reacquired));

            Invoke(window, "AddDuplicateValueButton");
            var ambiguous = WaitForStatus(window, descriptor, resolver, ResolutionStatus.Ambiguous);
            cases.Add(RecordCase("duplicate exact matches", ResolutionStatus.Ambiguous, ambiguous));

            Invoke(window, "RemoveDuplicateValueButton");
            var restored = WaitForStatus(window, descriptor, resolver, ResolutionStatus.Resolved);
            cases.Add(RecordCase("deterministic restoration after duplicate removal", ResolutionStatus.Resolved, restored));

            CloseWindow(window);
            var staleObserved = WaitForStale(target);
            cases.Add(new ResolutionCaseReport
            {
                Name = "stale element after window close",
                ExpectedStatus = ResolutionStatus.NotFound.ToString(),
                ActualStatus = staleObserved ? ResolutionStatus.NotFound.ToString() : "StaleElementStillAvailable",
                Passed = staleObserved,
                Reason = staleObserved
                    ? "The original AutomationElement became unavailable after its top-level window closed."
                    : "The original AutomationElement remained available after its top-level window closed."
            });

            var report = new ResolverReport
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                ProcessId = process.Id,
                ProcessName = processName,
                Descriptor = descriptor,
                Cases = cases,
                Passed = cases.All(testCase => testCase.Passed)
            };

            WriteReport(report, options.OutputPath);
            if (!report.Passed)
            {
                Console.Error.WriteLine("POC-03 resolver checks failed.");
                return 1;
            }

            Console.WriteLine($"POC-03 resolver checks passed: {cases.Count} cases.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ElementNotAvailableException)
        {
            Console.Error.WriteLine($"Target resolution failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static ResolutionCaseReport RecordCase(
        string name,
        ResolutionStatus expected,
        ResolutionResult result)
    {
        return new ResolutionCaseReport
        {
            Name = name,
            ExpectedStatus = expected.ToString(),
            ActualStatus = result.Status.ToString(),
            CandidateCount = result.Candidates.Count,
            ReturnedElement = result.Element is not null,
            Passed = result.Status == expected,
            Reason = result.Reason
        };
    }

    private static bool WaitForStale(AutomationElement target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = target.Current.AutomationId;
            }
            catch (ElementNotAvailableException)
            {
                return true;
            }
            catch (COMException exception) when (IsStaleElementException(exception))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static bool IsStaleElementException(COMException exception)
    {
        return exception.HResult == unchecked((int)0x8000FFFF)
            || exception.HResult == unchecked((int)0x80040201);
    }

    private static void CloseWindow(AutomationElement window)
    {
        if (!window.TryGetCurrentPattern(WindowPattern.Pattern, out var patternObject)
            || patternObject is not WindowPattern pattern)
        {
            throw new InvalidOperationException("The fixture window does not expose WindowPattern.");
        }

        pattern.Close();
    }

    private static ResolutionResult WaitForStatus(
        AutomationElement window,
        TargetDescriptor descriptor,
        TargetResolver resolver,
        ResolutionStatus expected)
    {
        ResolutionResult last = new()
        {
            Status = ResolutionStatus.NotFound,
            Reason = "Resolution was not attempted.",
            Candidates = Array.Empty<CandidateSummary>()
        };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            last = resolver.Resolve(window, descriptor);
            if (last.Status == expected)
            {
                return last;
            }

            Thread.Sleep(100);
        }

        return last;
    }

    private static AutomationElement WaitForWindow(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (window is not null)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"No UI Automation window was found for process {processId}.");
    }

    private static AutomationElement FindSingle(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var matches = window.FindAll(TreeScope.Descendants, condition);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"Expected one initial '{automationId}' element, found {matches.Count}.");
        }

        return matches[0];
    }

    private static void Invoke(AutomationElement window, string automationId)
    {
        var element = FindSingle(window, automationId);
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
            || patternObject is not InvokePattern pattern)
        {
            throw new InvalidOperationException($"Fixture control '{automationId}' does not expose InvokePattern.");
        }

        pattern.Invoke();
    }

    private static void WriteReport(ResolverReport report, string? outputPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Resolver report written to {Path.GetFullPath(outputPath)}");
    }

    private sealed class Arguments
    {
        public const string Usage = "Usage: target-resolution --pid id [--output path]";

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

internal sealed class ResolverReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required TargetDescriptor Descriptor { get; init; }
    public required IReadOnlyList<ResolutionCaseReport> Cases { get; init; }
    public bool Passed { get; init; }
}

internal sealed class ResolutionCaseReport
{
    public required string Name { get; init; }
    public required string ExpectedStatus { get; init; }
    public required string ActualStatus { get; init; }
    public int CandidateCount { get; init; }
    public bool ReturnedElement { get; init; }
    public bool Passed { get; init; }
    public required string Reason { get; init; }
}
