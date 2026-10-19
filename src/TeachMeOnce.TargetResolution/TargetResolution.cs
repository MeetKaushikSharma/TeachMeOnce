using System.Diagnostics;
using System.Windows.Automation;

namespace TeachMeOnce.TargetResolution;

internal enum ResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    ContextMismatch,
    InvalidDescriptor
}

internal sealed class TargetDescriptor
{
    public required ProcessContext Process { get; init; }
    public required WindowContext Window { get; init; }
    public required ElementDescriptor Element { get; init; }
    public required IReadOnlyList<ElementDescriptor> Ancestors { get; init; }
}

internal sealed class ProcessContext
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
}

internal sealed class WindowContext
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public required string ControlType { get; init; }
}

internal sealed class ElementDescriptor
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public required string ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsPassword { get; init; }
    public required IReadOnlyList<string> RequiredPatterns { get; init; }
}

internal sealed class CandidateSummary
{
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public required string ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsPassword { get; init; }
}

internal sealed class ResolutionResult
{
    public required ResolutionStatus Status { get; init; }
    public required string Reason { get; init; }
    public AutomationElement? Element { get; init; }
    public required IReadOnlyList<CandidateSummary> Candidates { get; init; }
}

internal static class TargetDescriptorBuilder
{
    public static TargetDescriptor Build(AutomationElement window, AutomationElement target)
    {
        var processId = ReadInt(target, AutomationElement.ProcessIdProperty)
            ?? throw new InvalidOperationException("The target did not expose a process ID.");
        var processName = Process.GetProcessById(processId).ProcessName;
        var windowDescriptor = BuildWindowContext(window);
        var chain = BuildAncestorChain(window, target);

        if (chain.Count == 0)
        {
            throw new InvalidOperationException("The target is not a descendant of the supplied window.");
        }

        return new TargetDescriptor
        {
            Process = new ProcessContext
            {
                ProcessId = processId,
                ProcessName = processName
            },
            Window = windowDescriptor,
            Element = chain[^1],
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
        AutomationElement target)
    {
        var chain = new List<ElementDescriptor>();
        var walker = TreeWalker.ControlViewWalker;
        var current = target;

        for (var depth = 0; depth < 64 && current is not null; depth++)
        {
            chain.Add(BuildElementDescriptor(current));
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

    internal static ElementDescriptor BuildElementDescriptor(AutomationElement element)
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
                .Select(pattern => Automation.PatternName(pattern) ?? pattern.ProgrammaticName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
        };
    }

    internal static CandidateSummary BuildCandidateSummary(AutomationElement element)
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

    internal static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : Normalize(value?.ToString());
    }

    internal static bool? ReadBool(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is bool boolean ? boolean : null;
    }

    internal static int? ReadInt(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is int integer ? integer : null;
    }

    internal static string ReadControlType(AutomationElement element)
    {
        return (element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType)
            ?.ProgrammaticName ?? "Unknown";
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

internal sealed class TargetResolver
{
    public ResolutionResult Resolve(AutomationElement window, TargetDescriptor descriptor)
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
        if (processId is null || processId.Value != descriptor.Process.ProcessId)
        {
            reason = $"Process ID mismatch: expected {descriptor.Process.ProcessId}, got {processId?.ToString() ?? "none"}.";
            return false;
        }

        var processName = Process.GetProcessById(processId.Value).ProcessName;
        if (!string.Equals(processName, descriptor.Process.ProcessName, StringComparison.Ordinal))
        {
            reason = $"Process name mismatch: expected '{descriptor.Process.ProcessName}', got '{processName}'.";
            return false;
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
        return current.AutomationId == expected.AutomationId
            && current.Name == expected.Name
            && current.ClassName == expected.ClassName
            && current.FrameworkId == expected.FrameworkId
            && current.ControlType == expected.ControlType;
    }

    private static bool MatchesTarget(AutomationElement candidate, TargetDescriptor descriptor)
    {
        var candidateDescriptor = TargetDescriptorBuilder.BuildElementDescriptor(candidate);
        var expected = descriptor.Element;

        if (candidateDescriptor.AutomationId != expected.AutomationId
            || candidateDescriptor.ControlType != expected.ControlType
            || candidateDescriptor.ClassName != expected.ClassName
            || candidateDescriptor.FrameworkId != expected.FrameworkId
            || candidateDescriptor.IsPassword != expected.IsPassword
            || !string.Equals(candidateDescriptor.Name, expected.Name, StringComparison.Ordinal))
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
            if (current is null || !MatchesDescriptor(current, descriptor.Ancestors[index]))
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
        return actual.AutomationId == expected.AutomationId
            && actual.Name == expected.Name
            && actual.ControlType == expected.ControlType
            && actual.ClassName == expected.ClassName
            && actual.FrameworkId == expected.FrameworkId;
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
