using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Automation;

namespace TeachMeOnce.TargetResolution;

public enum ResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    ContextMismatch,
    InvalidDescriptor
}

public enum ProcessIdentityPolicy
{
    ProcessIdAndName,
    ProcessName
}

public sealed class TargetDescriptor
{
    public required ProcessContext Process { get; init; }
    public required WindowContext Window { get; init; }
    public required ElementDescriptor Element { get; init; }
    public required IReadOnlyList<ElementDescriptor> Ancestors { get; init; }
}

public sealed class ProcessContext
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public string? IntegrityLevel { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessIdentityPolicy IdentityPolicy { get; init; } = ProcessIdentityPolicy.ProcessIdAndName;
}

public sealed class WindowContext
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public required string ControlType { get; init; }
    public bool MatchName { get; init; } = true;
}

public sealed class ElementDescriptor
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public required string ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsPassword { get; init; }
    public required IReadOnlyList<string> RequiredPatterns { get; init; }
    public bool MatchName { get; init; } = true;
}

public sealed class CandidateSummary
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public required string ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsPassword { get; init; }
}

public sealed class ResolutionResult
{
    public required ResolutionStatus Status { get; init; }
    public required string Reason { get; init; }
    public AutomationElement? Element { get; init; }
    public required IReadOnlyList<CandidateSummary> Candidates { get; init; }
}

public static class TargetDescriptorBuilder
{
    public static TargetDescriptor Build(
        AutomationElement window,
        AutomationElement target,
        ProcessIdentityPolicy identityPolicy = ProcessIdentityPolicy.ProcessIdAndName,
        bool matchWindowName = true,
        bool matchElementName = true,
        bool allowTargetWithoutControlAncestor = false)
    {
        var processId = ReadInt(target, AutomationElement.ProcessIdProperty)
            ?? throw new InvalidOperationException("The target did not expose a process ID.");
        var processName = Process.GetProcessById(processId).ProcessName;
        string? executablePath = null;
        try
        {
            executablePath = Process.GetProcessById(processId).MainModule?.FileName;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Executable path can be unavailable across a Windows access boundary.
        }
        var windowDescriptor = BuildWindowContext(window);
        var chain = BuildAncestorChain(window, target, matchWindowName);

        if (chain.Count == 0 && !allowTargetWithoutControlAncestor)
        {
            throw new InvalidOperationException("The target is not a descendant of the supplied window.");
        }

        var targetDescriptor = chain.Count == 0
            ? BuildElementDescriptor(target)
            : chain[^1];

        if (chain.Count == 0)
        {
            var windowProcessId = ReadInt(window, AutomationElement.ProcessIdProperty);
            if (windowProcessId is null || windowProcessId.Value != processId)
            {
                throw new InvalidOperationException("The target and supplied window did not expose the same process identity.");
            }
        }

        return new TargetDescriptor
        {
            Process = new ProcessContext
            {
                ProcessId = processId,
                ProcessName = processName,
                ExecutablePath = executablePath,
                IdentityPolicy = identityPolicy
            },
            Window = new WindowContext
            {
                AutomationId = windowDescriptor.AutomationId,
                Name = windowDescriptor.Name,
                ClassName = windowDescriptor.ClassName,
                FrameworkId = windowDescriptor.FrameworkId,
                ControlType = windowDescriptor.ControlType,
                MatchName = matchWindowName
            },
            Element = matchElementName
                ? targetDescriptor
                : WithMatchName(targetDescriptor, false),
            Ancestors = chain.Count > 1
                ? chain.Take(chain.Count - 1).ToArray()
                : Array.Empty<ElementDescriptor>()
        };
    }

    private static WindowContext BuildWindowContext(AutomationElement window)
    {
        return new WindowContext
        {
            AutomationId = ReadString(window, AutomationElement.AutomationIdProperty),
            Name = ReadString(window, AutomationElement.NameProperty),
            ClassName = ReadString(window, AutomationElement.ClassNameProperty),
            FrameworkId = ReadString(window, AutomationElement.FrameworkIdProperty),
            ControlType = ReadControlType(window)
        };
    }

    private static List<ElementDescriptor> BuildAncestorChain(
        AutomationElement window,
        AutomationElement target,
        bool matchWindowName)
    {
        var chain = new List<ElementDescriptor>();
        var walker = TreeWalker.ControlViewWalker;
        var current = target;

        for (var depth = 0; depth < 64 && current is not null; depth++)
        {
            var descriptor = BuildElementDescriptor(current);
            if (!matchWindowName && IsSameElement(current, window))
            {
                descriptor = WithMatchName(descriptor, false);
            }

            chain.Add(descriptor);
            if (IsSameElement(current, window))
            {
                chain.Reverse();
                return chain;
            }

            current = walker.GetParent(current);
        }

        return new List<ElementDescriptor>();
    }

    private static bool IsSameElement(AutomationElement left, AutomationElement right)
    {
        return left.Equals(right);
    }

    public static ElementDescriptor BuildElementDescriptor(AutomationElement element)
    {
        return new ElementDescriptor
        {
            AutomationId = ReadString(element, AutomationElement.AutomationIdProperty),
            Name = ReadString(element, AutomationElement.NameProperty),
            ControlType = ReadControlType(element),
            ClassName = ReadString(element, AutomationElement.ClassNameProperty),
            FrameworkId = ReadString(element, AutomationElement.FrameworkIdProperty),
            IsPassword = ReadBool(element, AutomationElement.IsPasswordProperty),
            RequiredPatterns = element.GetSupportedPatterns()
                .Select(pattern => System.Windows.Automation.Automation.PatternName(pattern) ?? pattern.ProgrammaticName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static ElementDescriptor WithMatchName(ElementDescriptor descriptor, bool matchName)
    {
        return new ElementDescriptor
        {
            AutomationId = descriptor.AutomationId,
            Name = descriptor.Name,
            ControlType = descriptor.ControlType,
            ClassName = descriptor.ClassName,
            FrameworkId = descriptor.FrameworkId,
            IsPassword = descriptor.IsPassword,
            RequiredPatterns = descriptor.RequiredPatterns,
            MatchName = matchName
        };
    }

    public static CandidateSummary BuildCandidateSummary(AutomationElement element)
    {
        var descriptor = BuildElementDescriptor(element);
        return new CandidateSummary
        {
            AutomationId = descriptor.AutomationId,
            Name = descriptor.Name,
            ControlType = descriptor.ControlType,
            ClassName = descriptor.ClassName,
            FrameworkId = descriptor.FrameworkId,
            IsPassword = descriptor.IsPassword
        };
    }

    public static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : Normalize(value?.ToString());
    }

    public static bool? ReadBool(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is bool boolean ? boolean : null;
    }

    public static int? ReadInt(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is int integer ? integer : null;
    }

    public static string ReadControlType(AutomationElement element)
    {
        return (element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType)
            ?.ProgrammaticName ?? "Unknown";
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed class TargetResolver
{
    public ResolutionResult Resolve(AutomationElement window, TargetDescriptor descriptor)
    {
        try
        {
            return ResolveCore(window, descriptor);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            return Failure(ResolutionStatus.ContextMismatch, $"The current UI Automation target became stale or unavailable: {exception.GetType().Name}.");
        }
    }

    private static ResolutionResult ResolveCore(AutomationElement window, TargetDescriptor descriptor)
    {
        if (!MatchesWindowContext(window, descriptor, out var contextReason))
        {
            return Failure(ResolutionStatus.ContextMismatch, contextReason);
        }

        if (string.IsNullOrWhiteSpace(descriptor.Element.ControlType))
        {
            return Failure(ResolutionStatus.InvalidDescriptor, "The descriptor has no control type.");
        }

        var candidates = new List<AutomationElement>();
        var summaries = new List<CandidateSummary>();
        var descendants = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement candidate in descendants)
        {
            if (MatchesTarget(candidate, descriptor))
            {
                candidates.Add(candidate);
                summaries.Add(TargetDescriptorBuilder.BuildCandidateSummary(candidate));
            }
        }

        return candidates.Count switch
        {
            0 => Failure(ResolutionStatus.NotFound, "No element matched the complete descriptor."),
            1 => new ResolutionResult
            {
                Status = ResolutionStatus.Resolved,
                Reason = "Exactly one element matched the complete descriptor.",
                Element = candidates[0],
                Candidates = summaries
            },
            _ => new ResolutionResult
            {
                Status = ResolutionStatus.Ambiguous,
                Reason = $"{candidates.Count} elements matched the complete descriptor; no guess was made.",
                Candidates = summaries
            }
        };
    }

    private static bool MatchesWindowContext(
        AutomationElement window,
        TargetDescriptor descriptor,
        out string reason)
    {
        var processId = TargetDescriptorBuilder.ReadInt(window, AutomationElement.ProcessIdProperty);
        if (processId is null)
        {
            reason = "The current window did not expose a process ID.";
            return false;
        }

        if (descriptor.Process.IdentityPolicy == ProcessIdentityPolicy.ProcessIdAndName
            && processId.Value != descriptor.Process.ProcessId)
        {
            reason = $"Process ID mismatch: expected {descriptor.Process.ProcessId}, got {processId.Value}.";
            return false;
        }

        if (descriptor.Process.IdentityPolicy is not (ProcessIdentityPolicy.ProcessIdAndName or ProcessIdentityPolicy.ProcessName))
        {
            reason = "The descriptor contains an unsupported process identity policy.";
            return false;
        }

        var processName = Process.GetProcessById(processId.Value).ProcessName;
        if (!string.Equals(processName, descriptor.Process.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Process name mismatch: expected '{descriptor.Process.ProcessName}', got '{processName}'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Process.ExecutablePath))
        {
            string? executablePath = null;
            try
            {
                executablePath = Process.GetProcessById(processId.Value).MainModule?.FileName;
            }
            catch (Win32Exception)
            {
                reason = "The current process executable identity could not be inspected.";
                return false;
            }
            catch (InvalidOperationException)
            {
                reason = "The current process executable identity could not be inspected.";
                return false;
            }

            if (!string.Equals(executablePath, descriptor.Process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Process executable identity mismatch.";
                return false;
            }
        }

        var currentWindow = new WindowContext
        {
            AutomationId = TargetDescriptorBuilder.ReadString(window, AutomationElement.AutomationIdProperty),
            Name = TargetDescriptorBuilder.ReadString(window, AutomationElement.NameProperty),
            ClassName = TargetDescriptorBuilder.ReadString(window, AutomationElement.ClassNameProperty),
            FrameworkId = TargetDescriptorBuilder.ReadString(window, AutomationElement.FrameworkIdProperty),
            ControlType = TargetDescriptorBuilder.ReadControlType(window)
        };

        if (!Equals(currentWindow, descriptor.Window))
        {
            reason = "Top-level window context does not match the descriptor.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool Equals(WindowContext current, WindowContext expected)
    {
        return MatchesOptional(current.AutomationId, expected.AutomationId)
            && (!expected.MatchName || current.Name == expected.Name)
            && MatchesOptional(current.ClassName, expected.ClassName)
            && MatchesOptional(current.FrameworkId, expected.FrameworkId)
            && current.ControlType == expected.ControlType;
    }

    private static bool MatchesTarget(AutomationElement candidate, TargetDescriptor descriptor)
    {
        var candidateDescriptor = TargetDescriptorBuilder.BuildElementDescriptor(candidate);
        var expected = descriptor.Element;

        if (!MatchesOptional(candidateDescriptor.AutomationId, expected.AutomationId)
            || candidateDescriptor.ControlType != expected.ControlType
            || !MatchesOptional(candidateDescriptor.ClassName, expected.ClassName)
            || !MatchesOptional(candidateDescriptor.FrameworkId, expected.FrameworkId)
            || candidateDescriptor.IsPassword != expected.IsPassword
            || (expected.MatchName && !string.Equals(candidateDescriptor.Name, expected.Name, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!expected.RequiredPatterns.All(pattern => candidateDescriptor.RequiredPatterns.Contains(pattern)))
        {
            return false;
        }

        return MatchesAncestors(candidate, descriptor);
    }

    private static bool MatchesAncestors(AutomationElement candidate, TargetDescriptor descriptor)
    {
        if (descriptor.Ancestors.Count == 0)
        {
            return true;
        }

        var walker = TreeWalker.ControlViewWalker;
        var current = walker.GetParent(candidate);
        for (var index = descriptor.Ancestors.Count - 1; index >= 0; index--)
        {
            var expected = descriptor.Ancestors[index];
            if (index == 0 && !descriptor.Window.MatchName)
            {
                expected = WithMatchName(expected, false);
            }

            if (current is null || !MatchesDescriptor(current, expected))
            {
                return false;
            }

            current = walker.GetParent(current);
        }

        return true;
    }

    private static bool MatchesDescriptor(AutomationElement element, ElementDescriptor expected)
    {
        var actual = TargetDescriptorBuilder.BuildElementDescriptor(element);
        return MatchesOptional(actual.AutomationId, expected.AutomationId)
            && (!expected.MatchName || actual.Name == expected.Name)
            && actual.ControlType == expected.ControlType
            && MatchesOptional(actual.ClassName, expected.ClassName)
            && MatchesOptional(actual.FrameworkId, expected.FrameworkId);
    }

    private static ElementDescriptor WithMatchName(ElementDescriptor descriptor, bool matchName)
    {
        return new ElementDescriptor
        {
            AutomationId = descriptor.AutomationId,
            Name = descriptor.Name,
            ControlType = descriptor.ControlType,
            ClassName = descriptor.ClassName,
            FrameworkId = descriptor.FrameworkId,
            IsPassword = descriptor.IsPassword,
            RequiredPatterns = descriptor.RequiredPatterns,
            MatchName = matchName
        };
    }

    private static bool MatchesOptional(string? actual, string? expected)
    {
        return expected is null || string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static ResolutionResult Failure(ResolutionStatus status, string reason)
    {
        return new ResolutionResult
        {
            Status = status,
            Reason = reason,
            Candidates = Array.Empty<CandidateSummary>()
        };
    }
}
