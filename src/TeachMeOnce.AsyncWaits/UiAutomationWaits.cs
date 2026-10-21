using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.AsyncWaits;

public enum WaitStatus
{
    Satisfied,
    TimedOut,
    Cancelled,
    NotFound,
    Ambiguous,
    Error
}

public sealed class WaitResult<T> where T : class
{
    public required WaitStatus Status { get; init; }
    public T? Value { get; init; }
    public required string Reason { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int ProbeCount { get; init; }
    public int UiAutomationErrorCount { get; init; }
}

public sealed class UiAutomationWaiter
{
    private readonly TimeSpan _pollInterval;

    public UiAutomationWaiter(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "The poll interval must be positive.");
        }
    }

    public Task<WaitResult<AutomationElement>> WaitForWindowAsync(
        int processId,
        string titleSubstring,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return WaitAsync(
            probe: () => FindWindow(processId, titleSubstring),
            satisfied: element => element is not null,
            timeout,
            cancellationToken,
            description: $"window title containing '{titleSubstring}'");
    }

    public Task<WaitResult<AutomationElement>> WaitForWindowAsync(
        TargetDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return WaitForDescriptorWindowAsync(descriptor, timeout, cancellationToken);
    }

    private async Task<WaitResult<AutomationElement>> WaitForDescriptorWindowAsync(
        TargetDescriptor descriptor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        var started = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow + timeout;
        var probes = 0;
        var lastProbeSummary = "no top-level candidates were inspected";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probes++;
            var matches = FindWindows(descriptor, out lastProbeSummary);
            if (matches.Count > 1)
            {
                return new WaitResult<AutomationElement>
                {
                    Status = WaitStatus.Ambiguous,
                    Reason = $"Multiple top-level windows matched process '{descriptor.Process.ProcessName}' and its window context.",
                    Elapsed = Stopwatch.GetElapsedTime(started),
                    ProbeCount = probes
                };
            }

            if (matches.Count == 1)
            {
                return new WaitResult<AutomationElement>
                {
                    Status = WaitStatus.Satisfied,
                    Value = matches[0],
                    Reason = "Exactly one top-level window matched the selected target context.",
                    Elapsed = Stopwatch.GetElapsedTime(started),
                    ProbeCount = probes
                };
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return new WaitResult<AutomationElement>
        {
            Status = WaitStatus.TimedOut,
            Reason = $"Timed out waiting for exactly one top-level window for process '{descriptor.Process.ProcessName}'; {lastProbeSummary}.",
            Elapsed = Stopwatch.GetElapsedTime(started),
            ProbeCount = probes
        };
    }

    public Task<WaitResult<AutomationElement>> WaitForElementAsync(
        AutomationElement window,
        string automationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return WaitAsync(
            probe: () => FindElement(window, automationId),
            satisfied: element => element is not null,
            timeout,
            cancellationToken,
            description: $"element AutomationId '{automationId}'");
    }

    public Task<WaitResult<AutomationElement>> WaitForNamedElementAsync(
        AutomationElement window,
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return WaitAsync(
            probe: () => FindNamedElement(window, name),
            satisfied: element => element is not null,
            timeout,
            cancellationToken,
            description: $"element named '{name}'");
    }

    public Task<WaitResult<AutomationElement>> WaitForEnabledAsync(
        AutomationElement element,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return WaitAsync(
            probe: () => element,
            satisfied: candidate => candidate is not null && IsEnabled(candidate),
            timeout,
            cancellationToken,
            description: "element enabled state");
    }

    public WaitResult<AutomationElement> ResolveElementOnce(
        AutomationElement window,
        string automationId)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var element = FindElement(window, automationId);
            return new WaitResult<AutomationElement>
            {
                Status = element is null ? WaitStatus.NotFound : WaitStatus.Satisfied,
                Value = element,
                Reason = element is null
                    ? $"Element '{automationId}' was not found in the current UI tree."
                    : $"Element '{automationId}' was found.",
                Elapsed = Stopwatch.GetElapsedTime(started),
                ProbeCount = 1
            };
        }
        catch (Exception exception) when (IsRecoverableUiAutomationError(exception))
        {
            return new WaitResult<AutomationElement>
            {
                Status = WaitStatus.Error,
                Reason = $"UI Automation failed while looking for '{automationId}': {exception.Message}",
                Elapsed = Stopwatch.GetElapsedTime(started),
                ProbeCount = 1,
                UiAutomationErrorCount = 1
            };
        }
    }

    private async Task<WaitResult<AutomationElement>> WaitAsync(
        Func<AutomationElement?> probe,
        Func<AutomationElement?, bool> satisfied,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string description)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        var started = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow + timeout;
        var probeCount = 0;
        var uiAutomationErrorCount = 0;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(WaitStatus.Cancelled, null, $"Wait for {description} was cancelled.");
            }

            probeCount++;
            try
            {
                var value = probe();
                if (satisfied(value))
                {
                    return Result(WaitStatus.Satisfied, value, $"Condition for {description} was satisfied.");
                }
            }
            catch (Exception exception) when (IsRecoverableUiAutomationError(exception))
            {
                uiAutomationErrorCount++;
                lastError = exception.Message;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(WaitStatus.Cancelled, null, $"Wait for {description} was cancelled.");
            }
        }

        var reason = lastError is null
            ? $"Timed out waiting for {description}."
            : $"Timed out waiting for {description}; last UI Automation error: {lastError}";
        return Result(WaitStatus.TimedOut, null, reason);

        WaitResult<AutomationElement> Result(
            WaitStatus status,
            AutomationElement? value,
            string reason)
        {
            return new WaitResult<AutomationElement>
            {
                Status = status,
                Value = value,
                Reason = reason,
                Elapsed = Stopwatch.GetElapsedTime(started),
                ProbeCount = probeCount,
                UiAutomationErrorCount = uiAutomationErrorCount
            };
        }
    }

    private static AutomationElement? FindWindow(int processId, string titleSubstring)
    {
        var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement candidate in candidates)
        {
            var name = ReadString(candidate, AutomationElement.NameProperty);
            if (name?.IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) >= 0
                && BelongsToProcess(candidate, processId))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<AutomationElement> FindWindows(TargetDescriptor descriptor, out string summary)
    {
        var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        var matches = new List<AutomationElement>();
        var seenHandles = new HashSet<IntPtr>();
        var processMatches = 0;
        var contextMismatches = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AutomationElement candidate in candidates)
        {
            try
            {
                var processId = ReadInt(candidate, AutomationElement.ProcessIdProperty);
                if (processId is null)
                {
                    continue;
                }

                var nativeHandle = ReadInt(candidate, AutomationElement.NativeWindowHandleProperty);
                if (nativeHandle is null or 0 || !IsWindowVisible(new IntPtr(nativeHandle.Value)))
                {
                    continue;
                }

                GetWindowThreadProcessId(new IntPtr(nativeHandle.Value), out var nativeProcessId);
                if (nativeProcessId != processId.Value)
                {
                    continue;
                }

                if (!seenHandles.Add(new IntPtr(nativeHandle.Value)))
                {
                    continue;
                }

                var processName = Process.GetProcessById(processId.Value).ProcessName;
                if (!string.Equals(processName, descriptor.Process.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                processMatches++;

                var current = new WindowContext
                {
                    AutomationId = ReadString(candidate, AutomationElement.AutomationIdProperty),
                    Name = ReadString(candidate, AutomationElement.NameProperty),
                    ClassName = ReadString(candidate, AutomationElement.ClassNameProperty),
                    FrameworkId = ReadString(candidate, AutomationElement.FrameworkIdProperty),
                    ControlType = ReadControlType(candidate),
                    MatchName = descriptor.Window.MatchName
                };

                if (!MatchesWindow(current, descriptor.Window, out var mismatch))
                {
                    contextMismatches[mismatch] = contextMismatches.TryGetValue(mismatch, out var count) ? count + 1 : 1;
                    continue;
                }

                matches.Add(candidate);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or ArgumentException or InvalidOperationException)
            {
                // The window disappeared while polling; the next poll may find its replacement.
            }
        }

        var mismatchSummary = contextMismatches.Count == 0
            ? "none"
            : string.Join(", ", contextMismatches.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}"));
        summary = $"rootCandidates={candidates.Count}, processMatches={processMatches}, contextMismatches={mismatchSummary}";
        return matches;
    }

    private static bool MatchesWindow(WindowContext current, WindowContext expected, out string mismatch)
    {
        if (expected.AutomationId is not null && current.AutomationId != expected.AutomationId)
        {
            mismatch = "automationId";
            return false;
        }

        if (expected.MatchName && current.Name != expected.Name)
        {
            mismatch = "name";
            return false;
        }

        if (expected.ClassName is not null && current.ClassName != expected.ClassName)
        {
            mismatch = "className";
            return false;
        }

        if (expected.FrameworkId is not null && current.FrameworkId != expected.FrameworkId)
        {
            mismatch = "frameworkId";
            return false;
        }

        if (current.ControlType != expected.ControlType)
        {
            mismatch = "controlType";
            return false;
        }

        mismatch = string.Empty;
        return true;
    }

    private static string ReadControlType(AutomationElement element)
    {
        return (element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType)
            ?.ProgrammaticName ?? "Unknown";
    }

    private static bool BelongsToProcess(AutomationElement element, int processId)
    {
        var reportedProcessId = ReadInt(element, AutomationElement.ProcessIdProperty);
        if (reportedProcessId == processId)
        {
            return true;
        }

        var nativeHandle = ReadInt(element, AutomationElement.NativeWindowHandleProperty);
        if (nativeHandle is null or 0)
        {
            return false;
        }

        GetWindowThreadProcessId(new IntPtr(nativeHandle.Value), out var nativeProcessId);
        return nativeProcessId == processId;
    }

    private static AutomationElement? FindElement(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return window.FindFirst(TreeScope.Descendants, condition);
    }

    private static AutomationElement? FindNamedElement(AutomationElement window, string name)
    {
        var condition = new PropertyCondition(AutomationElement.NameProperty, name);
        return window.FindFirst(TreeScope.Descendants, condition);
    }

    private static bool IsEnabled(AutomationElement element)
    {
        var value = element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty);
        return value is true;
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : value?.ToString();
    }

    private static int? ReadInt(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is int integer ? integer : null;
    }

    private static bool IsRecoverableUiAutomationError(Exception exception)
    {
        return exception is ElementNotAvailableException
            or ElementNotEnabledException
            or InvalidOperationException
            or COMException;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);
}
