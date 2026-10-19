using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace TeachMeOnce.UiAutomationCoverage;

internal static class Program
{
    private const int LegacyIAccessiblePatternId = 10018;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            var options = Arguments.Parse(args);
            using var launchedProcess = LaunchTarget(options.LaunchPath);
            var target = ResolveTargetWindow(options, launchedProcess);
            var report = Inspect(target, options);

            var json = JsonSerializer.Serialize(report, JsonOptions);
            if (options.OutputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
                File.WriteAllText(options.OutputPath, json);
                Console.WriteLine($"Coverage report written to {Path.GetFullPath(options.OutputPath)}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ElementNotAvailableException)
        {
            Console.Error.WriteLine($"Coverage inspection failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static Process? LaunchTarget(string? launchPath)
    {
        if (launchPath is null)
        {
            return null;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath(launchPath),
            UseShellExecute = true
        }) ?? throw new InvalidOperationException($"Could not start target '{launchPath}'.");

        process.WaitForInputIdle(5000);
        return process;
    }

    private static AutomationElement ResolveTargetWindow(Arguments options, Process? launchedProcess)
    {
        var processId = options.ProcessId ?? launchedProcess?.Id;
        var condition = processId is null
            ? Condition.TrueCondition
            : new PropertyCondition(AutomationElement.ProcessIdProperty, processId.Value);

        if (options.UseForegroundWindow && processId is null && options.TitleContains is null)
        {
            return GetForegroundWindowElement();
        }

        var deadline = DateTime.UtcNow.AddSeconds(options.WindowTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            foreach (AutomationElement candidate in candidates)
            {
                if (options.TitleContains is null)
                {
                    return candidate;
                }

                var title = ReadString(candidate, AutomationElement.NameProperty);
                if (title?.IndexOf(options.TitleContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            Thread.Sleep(100);
        }

        if (options.UseForegroundWindow)
        {
            return GetForegroundWindowElement();
        }

        throw new InvalidOperationException("No matching top-level window was found.");
    }

    private static AutomationElement GetForegroundWindowElement()
    {
        var foregroundHandle = GetForegroundWindow();
        if (foregroundHandle != IntPtr.Zero)
        {
            var foreground = AutomationElement.FromHandle(foregroundHandle);
            if (foreground is not null)
            {
                return foreground;
            }
        }

        throw new InvalidOperationException("The current foreground window could not be converted to a UI Automation element.");
    }

    private static CoverageReport Inspect(AutomationElement target, Arguments options)
    {
        var elements = new List<ElementReport>();
        InspectElement(target, string.Empty, elements, options.MaxElements);

        return new CoverageReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Target = new TargetReport
            {
                Name = ReadString(target, AutomationElement.NameProperty),
                AutomationId = ReadString(target, AutomationElement.AutomationIdProperty),
                ProcessId = ReadInt(target, AutomationElement.ProcessIdProperty),
                FrameworkId = ReadString(target, AutomationElement.FrameworkIdProperty),
                ClassName = ReadString(target, AutomationElement.ClassNameProperty)
            },
            ElementCount = elements.Count,
            Elements = elements
        };
    }

    private static void InspectElement(
        AutomationElement element,
        string ancestry,
        ICollection<ElementReport> reports,
        int maxElements)
    {
        if (reports.Count >= maxElements)
        {
            return;
        }

        var controlType = ReadControlType(element);
        var name = ReadString(element, AutomationElement.NameProperty);
        var automationId = ReadString(element, AutomationElement.AutomationIdProperty);
        var currentAncestry = string.IsNullOrEmpty(ancestry)
            ? controlType
            : $"{ancestry} > {controlType}";

        reports.Add(new ElementReport
        {
            Index = reports.Count,
            Ancestry = currentAncestry,
            Name = name,
            AutomationId = automationId,
            ControlType = controlType,
            ClassName = ReadString(element, AutomationElement.ClassNameProperty),
            FrameworkId = ReadString(element, AutomationElement.FrameworkIdProperty),
            IsEnabled = ReadBool(element, AutomationElement.IsEnabledProperty),
            IsOffscreen = ReadBool(element, AutomationElement.IsOffscreenProperty),
            IsPassword = ReadBool(element, AutomationElement.IsPasswordProperty),
            SupportedPatterns = GetSupportedPatterns(element)
        });

        if (reports.Count >= maxElements)
        {
            return;
        }

        AutomationElementCollection children;
        try
        {
            children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch (ElementNotAvailableException)
        {
            return;
        }

        foreach (AutomationElement child in children)
        {
            InspectElement(child, currentAncestry, reports, maxElements);
            if (reports.Count >= maxElements)
            {
                break;
            }
        }
    }

    private static IReadOnlyList<string> GetSupportedPatterns(AutomationElement element)
    {
        var supported = new List<string>();
        try
        {
            // GetSupportedPatterns asks the provider for the patterns it really
            // exposes. This also preserves native patterns such as
            // UIA_LegacyIAccessiblePatternId, which has no managed wrapper class.
            foreach (var pattern in element.GetSupportedPatterns())
            {
                var name = pattern.Id == LegacyIAccessiblePatternId
                    ? "LegacyIAccessible"
                    : Automation.PatternName(pattern) ?? pattern.ProgrammaticName;

                if (!string.IsNullOrWhiteSpace(name) && !supported.Contains(name, StringComparer.Ordinal))
                {
                    supported.Add(name);
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return supported;
    }

    private static string ReadControlType(AutomationElement element)
    {
        try
        {
            return ((ControlType)element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty))
                .ProgrammaticName;
        }
        catch (ElementNotAvailableException)
        {
            return "Unavailable";
        }
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property);
            return value == AutomationElement.NotSupported ? null : value?.ToString();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static int? ReadInt(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property);
            return value is int integer ? integer : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static bool? ReadBool(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property);
            return value is bool boolean ? boolean : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed class Arguments
    {
        public const string Usage = "Usage: coverage [--launch path] [--pid id] [--title-contains text] [--foreground] [--output path] [--max-elements n]";

        public string? LaunchPath { get; private init; }
        public int? ProcessId { get; private init; }
        public string? TitleContains { get; private init; }
        public bool UseForegroundWindow { get; private init; }
        public string? OutputPath { get; private init; }
        public int WindowTimeoutSeconds { get; private init; } = 10;
        public int MaxElements { get; private init; } = 500;

        public static Arguments Parse(string[] args)
        {
            string? launchPath = null;
            int? processId = null;
            string? titleContains = null;
            string? outputPath = null;
            var foreground = false;
            var timeout = 10;
            var maxElements = 500;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--launch":
                        launchPath = Next(args, ref index, "--launch");
                        break;
                    case "--pid":
                        processId = int.Parse(Next(args, ref index, "--pid"));
                        break;
                    case "--title-contains":
                        titleContains = Next(args, ref index, "--title-contains");
                        break;
                    case "--foreground":
                        foreground = true;
                        break;
                    case "--output":
                        outputPath = Next(args, ref index, "--output");
                        break;
                    case "--timeout-seconds":
                        timeout = int.Parse(Next(args, ref index, "--timeout-seconds"));
                        break;
                    case "--max-elements":
                        maxElements = int.Parse(Next(args, ref index, "--max-elements"));
                        break;
                    case "--help":
                        throw new InvalidOperationException(Usage);
                    default:
                        throw new ArgumentException($"Unknown argument '{args[index]}'.");
                }
            }

            if (timeout <= 0 || maxElements <= 0)
            {
                throw new ArgumentException("Timeout and max-elements must be positive.");
            }

            if (launchPath is null && processId is null && titleContains is null && !foreground)
            {
                throw new ArgumentException("Provide --launch, --pid, --title-contains, or --foreground.");
            }

            return new Arguments
            {
                LaunchPath = launchPath,
                ProcessId = processId,
                TitleContains = titleContains,
                UseForegroundWindow = foreground,
                OutputPath = outputPath,
                WindowTimeoutSeconds = timeout,
                MaxElements = maxElements
            };
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

    private sealed class CoverageReport
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public required TargetReport Target { get; init; }
        public int ElementCount { get; init; }
        public required IReadOnlyList<ElementReport> Elements { get; init; }
    }

    private sealed class TargetReport
    {
        public string? Name { get; init; }
        public string? AutomationId { get; init; }
        public int? ProcessId { get; init; }
        public string? FrameworkId { get; init; }
        public string? ClassName { get; init; }
    }

    private sealed class ElementReport
    {
        public int Index { get; init; }
        public required string Ancestry { get; init; }
        public string? Name { get; init; }
        public string? AutomationId { get; init; }
        public required string ControlType { get; init; }
        public string? ClassName { get; init; }
        public string? FrameworkId { get; init; }
        public bool? IsEnabled { get; init; }
        public bool? IsOffscreen { get; init; }
        public bool? IsPassword { get; init; }
        public required IReadOnlyList<string> SupportedPatterns { get; init; }
    }
}
