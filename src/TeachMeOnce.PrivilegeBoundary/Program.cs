using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace TeachMeOnce.PrivilegeBoundary;

internal static class Program
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationTypeInformation = 18;
    private const int TokenElevationInformation = 20;
    private const int TokenIntegrityLevelInformation = 25;
    private const int ErrorInsufficientBuffer = 122;
    private const int UntrustedIntegrityRid = 0x0000;
    private const int LowIntegrityRid = 0x1000;
    private const int MediumIntegrityRid = 0x2000;
    private const int HighIntegrityRid = 0x3000;
    private const int SystemIntegrityRid = 0x4000;
    private const int ProtectedIntegrityRid = 0x5000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            var options = Arguments.Parse(args);
            using var targetProcess = Process.GetProcessById(options.ProcessId);
            using var currentProcess = Process.GetCurrentProcess();
            var currentIntegrity = IntegrityInspector.Read(currentProcess.Handle, currentProcess.Id, currentProcess.ProcessName);
            var targetIntegrity = IntegrityInspector.ReadProcess(targetProcess.Id, targetProcess.ProcessName);
            var boundary = DetermineBoundary(currentIntegrity, targetIntegrity);
            var inspection = Inspect(targetProcess.Id);
            var interaction = boundary == "TargetHigherIntegrity"
                ? UnsupportedInteraction("The target has higher integrity than the standard harness. Interaction was intentionally not attempted; no elevated retry or coordinate fallback was used.")
                : inspection.Status is "Passed"
                ? Interact(inspection)
                : new InteractionReport
                {
                    Status = boundary == "TargetHigherIntegrity" ? "Unsupported" : "NotAttempted",
                    Attempted = false,
                    Passed = false,
                    Reason = "Interaction was not attempted because inspection did not produce a safe target element; no privilege escalation or fallback was used."
                };

            var scenarioMatches = options.Scenario switch
            {
                "same-integrity" => boundary == "SameIntegrity",
                "cross-integrity" => boundary == "TargetHigherIntegrity",
                _ => false
            };
            var testResult = DetermineTestResult(options.Scenario, boundary, scenarioMatches, inspection, interaction);
            var report = new PrivilegeReport
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Scenario = options.Scenario,
                HarnessProcess = currentIntegrity,
                TargetProcess = targetIntegrity,
                ObservedBoundary = boundary,
                Inspection = inspection,
                Interaction = interaction,
                TestResult = testResult,
                ClassificationPassed = testResult is "Passed" or "Supported" or "Unsupported",
                Passed = testResult is "Passed" or "Supported"
            };

            WriteReport(report, options.OutputPath);
            Console.WriteLine($"POC-06 privilege boundary result: scenario={options.Scenario}, boundary={boundary}, inspection={inspection.Status}, interaction={interaction.Status}, result={testResult}.");
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Privilege boundary failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static string DetermineBoundary(IntegrityReport current, IntegrityReport target)
    {
        if (!current.Available || !target.Available)
        {
            return "IntegrityUnavailable";
        }

        if (current.Rid == target.Rid)
        {
            return "SameIntegrity";
        }

        return target.Rid > current.Rid ? "TargetHigherIntegrity" : "TargetLowerIntegrity";
    }

    private static string DetermineTestResult(
        string scenario,
        string boundary,
        bool scenarioMatches,
        InspectionReport inspection,
        InteractionReport interaction)
    {
        if (!scenarioMatches)
        {
            return boundary == "IntegrityUnavailable" ? "Unavailable" : "SetupMismatch";
        }

        if (scenario == "same-integrity")
        {
            return inspection.Status == "Passed" && interaction.Status == "Supported" ? "Passed" : "Failed";
        }

        if (scenario == "cross-integrity")
        {
            return interaction.Status switch
            {
                "Supported" => "Supported",
                "Unsupported" => "Unsupported",
                _ => "Failed"
            };
        }

        return "Failed";
    }

    private static InspectionReport Inspect(int processId)
    {
        try
        {
            var window = WaitForWindow(processId);
            var windowName = ReadString(window, AutomationElement.NameProperty);
            var windowAutomationId = ReadString(window, AutomationElement.AutomationIdProperty);
            var invokeMatches = FindAll(window, "InvokeButton");
            var statusMatches = FindByAutomationIds(window, "StatusText", "StatusLabel");
            if (invokeMatches.Count != 1 || statusMatches.Count != 1)
            {
                return new InspectionReport
                {
                    Status = "Failed",
                    Accessible = true,
                    UiAutomationRootFound = true,
                    TargetFound = false,
                    ProcessId = processId,
                    WindowName = windowName,
                    WindowAutomationId = windowAutomationId,
                    InvokeButtonMatchCount = invokeMatches.Count,
                    StatusTextMatchCount = statusMatches.Count,
                    Reason = $"UI Automation inspection succeeded, but expected unique targets were not found: InvokeButton={invokeMatches.Count}, StatusText={statusMatches.Count}."
                };
            }

            return new InspectionReport
            {
                Status = "Passed",
                Accessible = true,
                UiAutomationRootFound = true,
                TargetFound = true,
                ProcessId = processId,
                WindowName = windowName,
                WindowAutomationId = windowAutomationId,
                InvokeButtonMatchCount = invokeMatches.Count,
                StatusTextMatchCount = statusMatches.Count,
                Reason = "UI Automation exposed the target window and both required semantic elements."
            };
        }
        catch (ElementNotAvailableException exception)
        {
            return UnsupportedInspection(processId, exception);
        }
        catch (COMException exception) when (IsAccessDenied(exception))
        {
            return UnsupportedInspection(processId, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return UnsupportedInspection(processId, exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return new InspectionReport
            {
                Status = "Failed",
                Accessible = false,
                UiAutomationRootFound = false,
                TargetFound = false,
                ProcessId = processId,
                Reason = $"UI Automation inspection failed without an access-denied classification: {exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    private static InspectionReport UnsupportedInspection(int processId, Exception exception)
    {
        return new InspectionReport
        {
            Status = "Unsupported",
            Accessible = false,
            UiAutomationRootFound = false,
            TargetFound = false,
            ProcessId = processId,
            Reason = $"UI Automation inspection was blocked or the target became unavailable: {exception.GetType().Name}: {exception.Message}"
        };
    }

    private static InteractionReport Interact(InspectionReport inspection)
    {
        try
        {
            var window = WaitForWindow(inspection.ProcessId);
            var button = FindAll(window, "InvokeButton");
            var status = FindByAutomationIds(window, "StatusText", "StatusLabel");
            if (button.Count != 1 || status.Count != 1)
            {
                return new InteractionReport
                {
                    Status = "Failed",
                    Attempted = false,
                    Passed = false,
                    Reason = "Interaction was not attempted because the semantic target was no longer unique."
                };
            }

            if (!button[0].TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
                || patternObject is not InvokePattern pattern)
            {
                return new InteractionReport
                {
                    Status = "Failed",
                    Attempted = false,
                    Passed = false,
                    Reason = "InvokeButton did not expose InvokePattern; no coordinate or elevated fallback was attempted."
                };
            }

            pattern.Invoke();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (string.Equals(ReadString(status[0], AutomationElement.NameProperty), "Status: invoked", StringComparison.Ordinal))
                {
                    return new InteractionReport
                    {
                        Status = "Supported",
                        Attempted = true,
                        Passed = true,
                        Reason = "InvokePattern completed and the fixture status changed to 'Status: invoked'."
                    };
                }

                Thread.Sleep(50);
            }

            return new InteractionReport
            {
                Status = "Failed",
                Attempted = true,
                Passed = false,
                Reason = $"InvokePattern returned without the expected status change; observed status '{ReadString(status[0], AutomationElement.NameProperty) ?? "<unavailable>"}'."
            };
        }
        catch (ElementNotAvailableException exception)
        {
            return UnsupportedInteraction(exception);
        }
        catch (COMException exception) when (IsAccessDenied(exception))
        {
            return UnsupportedInteraction(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return UnsupportedInteraction(exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return new InteractionReport
            {
                Status = "Failed",
                Attempted = true,
                Passed = false,
                Reason = $"UI Automation interaction failed without an access-denied classification: {exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    private static InteractionReport UnsupportedInteraction(Exception exception)
    {
        return UnsupportedInteraction($"Cross-integrity interaction was blocked or the element became unavailable: {exception.GetType().Name}: {exception.Message}. No elevated retry was attempted.");
    }

    private static InteractionReport UnsupportedInteraction(string reason)
    {
        return new InteractionReport
        {
            Status = "Unsupported",
            Attempted = false,
            Passed = false,
            Reason = reason
        };
    }

    private static bool IsAccessDenied(COMException exception)
    {
        return exception.HResult == unchecked((int)0x80070005) || exception.HResult == unchecked((int)0x80004005);
    }

    private static AutomationElement WaitForWindow(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
                if (window is not null)
                {
                    return window;
                }
            }
            catch (ElementNotAvailableException)
            {
                throw;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"No UI Automation top-level window was found for process {processId}.");
    }

    private static List<AutomationElement> FindAll(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var matches = window.FindAll(TreeScope.Descendants, condition);
        var result = new List<AutomationElement>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            result.Add(matches[index]);
        }

        return result;
    }

    private static List<AutomationElement> FindByAutomationIds(AutomationElement window, params string[] automationIds)
    {
        foreach (var automationId in automationIds)
        {
            var matches = FindAll(window, automationId);
            if (matches.Count > 0)
            {
                return matches;
            }
        }

        return new List<AutomationElement>();
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : value?.ToString();
    }

    private static void WriteReport(PrivilegeReport report, string? outputPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Privilege boundary report written to {Path.GetFullPath(outputPath)}");
    }

    private sealed class Arguments
    {
        public const string Usage = "Usage: privilege-boundary --pid id --scenario same-integrity|cross-integrity [--output path]";
        public int ProcessId { get; private init; }
        public string Scenario { get; private init; } = string.Empty;
        public string? OutputPath { get; private init; }

        public static Arguments Parse(string[] args)
        {
            int? processId = null;
            string? scenario = null;
            string? outputPath = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pid":
                        processId = int.Parse(Next(args, ref index, "--pid"));
                        break;
                    case "--scenario":
                        scenario = Next(args, ref index, "--scenario");
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

            if (scenario is not ("same-integrity" or "cross-integrity"))
            {
                throw new ArgumentException("--scenario must be same-integrity or cross-integrity.");
            }

            return new Arguments { ProcessId = processId.Value, Scenario = scenario!, OutputPath = outputPath };
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

    private static class IntegrityInspector
    {
        public static IntegrityReport ReadProcess(int processId, string processName)
        {
            var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return Unavailable(processId, processName, $"OpenProcess failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                return Read(processHandle, processId, processName);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        public static IntegrityReport Read(IntPtr processHandle, int processId, string processName)
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                return Unavailable(processId, processName, $"OpenProcessToken failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                var rid = ReadIntegrityRid(tokenHandle);
                var elevated = ReadBoolean(tokenHandle, TokenElevationInformation);
                var elevationType = ReadElevationType(tokenHandle);
                return new IntegrityReport
                {
                    ProcessId = processId,
                    ProcessName = processName,
                    Available = true,
                    Rid = rid,
                    Level = NameForRid(rid),
                    Elevated = elevated,
                    ElevationType = elevationType,
                    Reason = "Process token integrity information was read successfully."
                };
            }
            catch (Win32Exception exception)
            {
                return Unavailable(processId, processName, exception.Message);
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }

        private static int ReadIntegrityRid(IntPtr tokenHandle)
        {
            var buffer = AllocateTokenBuffer(tokenHandle, TokenIntegrityLevelInformation);
            try
            {
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool ReadBoolean(IntPtr tokenHandle, int informationClass)
        {
            var buffer = AllocateTokenBuffer(tokenHandle, informationClass);
            try
            {
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadElevationType(IntPtr tokenHandle)
        {
            var buffer = AllocateTokenBuffer(tokenHandle, TokenElevationTypeInformation);
            try
            {
                return Marshal.ReadInt32(buffer) switch
                {
                    1 => "Default",
                    2 => "Full",
                    3 => "Limited",
                    _ => "Unknown"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr AllocateTokenBuffer(IntPtr tokenHandle, int informationClass)
        {
            _ = GetTokenInformation(tokenHandle, informationClass, IntPtr.Zero, 0, out var required);
            if (required <= 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation did not report a required buffer size.");
            }

            var buffer = Marshal.AllocHGlobal((int)required);
            if (!GetTokenInformation(tokenHandle, informationClass, buffer, required, out _))
            {
                Marshal.FreeHGlobal(buffer);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation failed.");
            }

            return buffer;
        }

        private static IntegrityReport Unavailable(int processId, string processName, string reason)
        {
            return new IntegrityReport
            {
                ProcessId = processId,
                ProcessName = processName,
                Available = false,
                Level = "Unavailable",
                ElevationType = "Unavailable",
                Reason = reason
            };
        }

        private static string NameForRid(int rid) => rid switch
        {
            UntrustedIntegrityRid => "Untrusted",
            LowIntegrityRid => "Low",
            MediumIntegrityRid => "Medium",
            HighIntegrityRid => "High",
            SystemIntegrityRid => "System",
            ProtectedIntegrityRid => "Protected",
            _ => $"Unknown({rid})"
        };

    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class PrivilegeReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Scenario { get; init; }
    public required IntegrityReport HarnessProcess { get; init; }
    public required IntegrityReport TargetProcess { get; init; }
    public required string ObservedBoundary { get; init; }
    public required InspectionReport Inspection { get; init; }
    public required InteractionReport Interaction { get; init; }
    public required string TestResult { get; init; }
    public bool ClassificationPassed { get; init; }
    public bool Passed { get; init; }
}

internal sealed class IntegrityReport
{
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public bool Available { get; init; }
    public int Rid { get; init; }
    public required string Level { get; init; }
    public bool Elevated { get; init; }
    public required string ElevationType { get; init; }
    public required string Reason { get; init; }
}

internal sealed class InspectionReport
{
    public required string Status { get; init; }
    public bool Accessible { get; init; }
    public bool UiAutomationRootFound { get; init; }
    public bool TargetFound { get; init; }
    public int ProcessId { get; init; }
    public string? WindowName { get; init; }
    public string? WindowAutomationId { get; init; }
    public int InvokeButtonMatchCount { get; init; }
    public int StatusTextMatchCount { get; init; }
    public required string Reason { get; init; }
}

internal sealed class InteractionReport
{
    public required string Status { get; init; }
    public bool Attempted { get; init; }
    public bool Passed { get; init; }
    public required string Reason { get; init; }
}
