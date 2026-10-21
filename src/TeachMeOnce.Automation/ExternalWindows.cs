using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Automation;

public enum ExternalTargetStatus
{
    Attached,
    NotFound,
    Ambiguous,
    Unsupported,
    Stale,
    AccessDenied
}

public enum IntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    System = 5,
    Protected = 6
}

public sealed record IntegrityInfo(
    IntegrityLevel Level,
    bool Available,
    string Reason)
{
    public static IntegrityInfo Unavailable(string reason) => new(IntegrityLevel.Unknown, false, reason);
}

public sealed record ExternalWindowCandidate(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string WindowTitle,
    string? AutomationId,
    string? ClassName,
    string? FrameworkId,
    string ControlType,
    IntegrityInfo Integrity)
{
    public string DisplayName => $"{WindowTitle} — {ProcessName} (PID {ProcessId})";
}

public sealed record ExternalTarget(
    ExternalWindowCandidate Window,
    IntegrityInfo CurrentIntegrity,
    bool SameIntegrity)
{
    public bool IsSupported => Window.Integrity.Available && SameIntegrity;
}

public sealed record ExternalTargetSelection(
    ExternalTargetStatus Status,
    ExternalTarget? Target,
    string Reason);

public sealed class ExternalTargetSelector
{
    public IReadOnlyList<ExternalWindowCandidate> Enumerate()
    {
        var candidates = new List<ExternalWindowCandidate>();
        var seenHandles = new HashSet<IntPtr>();
        var root = AutomationElement.RootElement;
        var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
        for (var index = 0; index < windows.Count; index++)
        {
            var element = windows[index];
            try
            {
                var handleValue = ReadInt(element, AutomationElement.NativeWindowHandleProperty);
                var processId = ReadInt(element, AutomationElement.ProcessIdProperty);
                if (handleValue is null or 0 || processId is null || processId.Value == Environment.ProcessId)
                {
                    continue;
                }

                var handle = new IntPtr(handleValue.Value);
                if (!IsWindowVisible(handle))
                {
                    continue;
                }

                if (!seenHandles.Add(handle))
                {
                    continue;
                }

                using var process = Process.GetProcessById(processId.Value);
                if (process.HasExited)
                {
                    continue;
                }

                if (string.Equals(process.ProcessName, "TeachMeOnce.App", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var integrity = IntegrityInspector.ReadProcess(process);
                candidates.Add(new ExternalWindowCandidate(
                    handle,
                    processId.Value,
                    process.ProcessName,
                    TryGetExecutablePath(process),
                    ReadString(element, AutomationElement.NameProperty) ?? "(untitled window)",
                    ReadString(element, AutomationElement.AutomationIdProperty),
                    ReadString(element, AutomationElement.ClassNameProperty),
                    ReadString(element, AutomationElement.FrameworkIdProperty),
                    ReadControlType(element),
                    integrity));
            }
            catch (ElementNotAvailableException)
            {
                // A window that disappears during enumeration is not selectable.
            }
            catch (ArgumentException)
            {
                // The process exited between UIA enumeration and inspection.
            }
        }

        return candidates
            .OrderBy(candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ProcessId)
            .ToArray();
    }

    public ExternalTargetSelection Attach(ExternalWindowCandidate candidate)
    {
        var current = IntegrityInspector.ReadCurrent();
        if (!IsWindow(candidate.Handle) || !IsWindowVisible(candidate.Handle))
        {
            return new(ExternalTargetStatus.Stale, null, "The selected window is no longer visible.");
        }

        WindowsInterop.GetWindowThreadProcessId(candidate.Handle, out var processId);
        if (processId != candidate.ProcessId)
        {
            return new(ExternalTargetStatus.Stale, null, "The selected window now belongs to a different process.");
        }

        var actual = Enumerate().Where(item => item.Handle == candidate.Handle).ToArray();
        if (actual.Length == 0)
        {
            return new(ExternalTargetStatus.NotFound, null, "The selected window could not be reacquired.");
        }

        if (actual.Length != 1)
        {
            return new(ExternalTargetStatus.Ambiguous, null, "The selected handle did not resolve to exactly one top-level window.");
        }

        var selected = actual[0];
        if (!string.Equals(selected.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase)
            || (candidate.ExecutablePath is not null
                && selected.ExecutablePath is not null
                && !string.Equals(candidate.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            return new(ExternalTargetStatus.Stale, null, "The selected process identity changed while the window handle was reused.");
        }

        if (!selected.Integrity.Available)
        {
            return new(ExternalTargetStatus.Unsupported, null, $"The selected target integrity was unavailable: {selected.Integrity.Reason}");
        }

        if (!current.Available)
        {
            return new(ExternalTargetStatus.Unsupported, null, $"The recorder integrity was unavailable: {current.Reason}");
        }

        if (selected.Integrity.Level != current.Level)
        {
            return new(
                ExternalTargetStatus.Unsupported,
                null,
                $"Target integrity ({selected.Integrity.Level}) does not match recorder integrity ({current.Level}); higher- or mismatched-integrity targets are unsupported by the MVP.");
        }

        return new(ExternalTargetStatus.Attached, new ExternalTarget(selected, current, true), "The external window was attached safely.");
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

    private static string ReadControlType(AutomationElement element)
    {
        return (element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType)?.ProgrammaticName ?? "Unknown";
    }

    private static bool IsWindow(IntPtr handle) => WindowsInterop.IsWindow(handle);

    private static bool IsWindowVisible(IntPtr handle) => WindowsInterop.IsWindowVisible(handle);

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

public static class IntegrityInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelInformation = 25;

    public static IntegrityInfo ReadCurrent() => ReadProcess(Process.GetCurrentProcess());

    public static IntegrityInfo ReadProcess(Process process)
    {
        try
        {
            using var handle = SafeNativeHandle.OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle.IsInvalid || !OpenProcessToken(handle.DangerousGetHandle(), TokenQuery, out var token))
            {
                return IntegrityInfo.Unavailable($"Could not inspect integrity for process {process.Id}.");
            }

            try
            {
                return ReadToken(token, process.Id);
            }
            finally
            {
                WindowsInterop.CloseHandle(token);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            return IntegrityInfo.Unavailable($"Integrity inspection was denied: {exception.GetType().Name}.");
        }
            catch (Win32Exception exception)
        {
            return IntegrityInfo.Unavailable($"Integrity inspection failed: {exception.NativeErrorCode}.");
        }
    }

    private static IntegrityInfo ReadToken(IntPtr token, int processId)
    {
        GetTokenInformation(token, TokenIntegrityLevelInformation, IntPtr.Zero, 0, out var size);
        if (size <= 0)
        {
            return IntegrityInfo.Unavailable($"Integrity information was unavailable for process {processId}.");
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelInformation, buffer, size, out _))
            {
                return IntegrityInfo.Unavailable($"Integrity information could not be read for process {processId}.");
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return IntegrityInfo.Unavailable($"Integrity SID was unavailable for process {processId}.");
            }

            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0)
            {
                return IntegrityInfo.Unavailable($"Integrity SID was unavailable for process {processId}.");
            }

            var rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            var level = rid switch
            {
                < 0x1000 => IntegrityLevel.Untrusted,
                < 0x2000 => IntegrityLevel.Low,
                < 0x3000 => IntegrityLevel.Medium,
                < 0x4000 => IntegrityLevel.High,
                < 0x5000 => IntegrityLevel.System,
                _ => IntegrityLevel.Protected
            };
            return new IntegrityInfo(level, true, $"Process {processId} is {level} integrity.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);
}

internal sealed class SafeNativeHandle : SafeHandle
{
    private SafeNativeHandle() : base(IntPtr.Zero, true) { }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    protected override bool ReleaseHandle() => WindowsInterop.CloseHandle(handle);

    public static SafeNativeHandle OpenProcess(uint access, bool inherit, int processId)
    {
        var handle = WindowsInterop.OpenProcess(access, inherit, processId);
        var result = new SafeNativeHandle();
        result.SetHandle(handle);
        return result;
    }
}
