using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using FormsScreen = System.Windows.Forms.Screen;

namespace TeachMeOnce.DisplayRobustness;

internal static class Program
{
    private const uint SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int DpiAwarenessContextPerMonitorAwareV2 = -4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            SetProcessDpiAwareness();
            var options = Arguments.Parse(args);
            using var process = Process.GetProcessById(options.ProcessId);
            var window = WaitForWindow(process.Id);
            var hwnd = GetNativeWindowHandle(window);
            var originalRect = GetWindowRectValue(hwnd);
            var screens = FormsScreen.AllScreens;
            var monitorInventory = screens.Select(screen => new MonitorReport
            {
                DeviceName = screen.DeviceName,
                BoundsLeft = screen.Bounds.Left,
                BoundsTop = screen.Bounds.Top,
                BoundsWidth = screen.Bounds.Width,
                BoundsHeight = screen.Bounds.Height,
                WorkAreaLeft = screen.WorkingArea.Left,
                WorkAreaTop = screen.WorkingArea.Top,
                WorkAreaWidth = screen.WorkingArea.Width,
                WorkAreaHeight = screen.WorkingArea.Height,
                MonitorDpi = GetMonitorDpi(screen)
            }).ToArray();
            var cases = new List<DisplayCaseReport>();
            var dpiValues = new HashSet<uint>();
            var monitorDpiValues = new HashSet<uint>();
            var negativeCoordinateEnvironmentAvailable = monitorInventory.Any(monitor => monitor.WorkAreaLeft < 0 || monitor.WorkAreaTop < 0);

            try
            {
                ShowAndActivate(hwnd);
                cases.Add(TestPosition(hwnd, "initial", "initial monitor", null, process.Id, dpiValues, monitorDpiValues));

                foreach (var screen in screens)
                {
                    var workArea = screen.WorkingArea;
                    var width = Math.Min(originalRect.Width, Math.Max(320, workArea.Width - 40));
                    var height = Math.Min(originalRect.Height, Math.Max(260, workArea.Height - 40));
                    MoveWindow(hwnd, workArea.Left + 20, workArea.Top + 20, width, height);
                    cases.Add(TestPosition(hwnd, "monitor", screen.DeviceName, screen.DeviceName, process.Id, dpiValues, monitorDpiValues));
                }

                var currentScreen = FormsScreen.FromHandle(hwnd);
                var resizedWidth = Math.Max(320, currentScreen.WorkingArea.Width / 2);
                var resizedHeight = Math.Max(260, currentScreen.WorkingArea.Height / 2);
                MoveWindow(
                    hwnd,
                    currentScreen.WorkingArea.Left + 10,
                    currentScreen.WorkingArea.Top + 10,
                    Math.Min(resizedWidth, currentScreen.WorkingArea.Width - 20),
                    Math.Min(resizedHeight, currentScreen.WorkingArea.Height - 20));
                cases.Add(TestPosition(hwnd, "resize", currentScreen.DeviceName, currentScreen.DeviceName, process.Id, dpiValues, monitorDpiValues));
            }
            finally
            {
                MoveWindow(hwnd, originalRect.Left, originalRect.Top, originalRect.Width, originalRect.Height);
            }

            var multiMonitorTested = screens.Length > 1;
            var mixedDpiObserved = monitorDpiValues.Count > 1;
            var negativeCoordinatesObserved = cases.Any(testCase => testCase.CoordinateX < 0 || testCase.CoordinateY < 0);
            var negativeCoordinatesTested = negativeCoordinatesObserved;
            cases.Add(new DisplayCaseReport
            {
                Name = "multiple monitors",
                Location = "system",
                Dpi = 0,
                Passed = multiMonitorTested,
                Skipped = !multiMonitorTested,
                Status = multiMonitorTested ? "Passed" : "Unavailable",
                Reason = multiMonitorTested
                    ? $"Tested {screens.Length} monitors."
                    : "Only one monitor was available; multi-monitor movement was not exercised."
            });
            cases.Add(new DisplayCaseReport
            {
                Name = "mixed DPI settings",
                Location = "system",
                Dpi = 0,
                Passed = mixedDpiObserved,
                Skipped = !mixedDpiObserved,
                Status = mixedDpiObserved ? "Passed" : "Unavailable",
                Reason = mixedDpiObserved
                    ? $"Observed {monitorDpiValues.Count} distinct monitor DPI values."
                    : "Only one distinct monitor DPI value was observed; mixed-DPI behavior was not exercised."
            });
            cases.Add(new DisplayCaseReport
            {
                Name = "negative coordinates",
                Location = "system",
                Dpi = 0,
                Passed = negativeCoordinatesTested,
                Skipped = !negativeCoordinateEnvironmentAvailable,
                Status = negativeCoordinatesTested
                    ? "Passed"
                    : negativeCoordinateEnvironmentAvailable ? "Failed" : "Unavailable",
                Reason = negativeCoordinatesTested
                    ? "A clickable screen point was observed with a negative coordinate."
                    : negativeCoordinateEnvironmentAvailable
                        ? "A monitor with negative work-area coordinates was available, but no tested clickable point had a negative coordinate."
                        : "No monitor with negative work-area coordinates was available."
            });

            var executionPassed = cases.Where(testCase => !testCase.Skipped).All(testCase => testCase.Passed);
            var coverageComplete = cases
                .Where(testCase => testCase.Name is "multiple monitors" or "mixed DPI settings" or "negative coordinates")
                .All(testCase => !testCase.Skipped && testCase.Passed);

            var report = new DisplayReport
            {
                SchemaVersion = 3,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                MonitorCount = screens.Length,
                Monitors = monitorInventory,
                ObservedDpiValues = dpiValues.OrderBy(value => value).ToArray(),
                ObservedMonitorDpiValues = monitorDpiValues.OrderBy(value => value).ToArray(),
                NegativeCoordinatesObserved = negativeCoordinatesObserved,
                MultiMonitorTested = multiMonitorTested,
                MixedDpiObserved = mixedDpiObserved,
                Cases = cases,
                ExecutionPassed = executionPassed,
                CoverageComplete = coverageComplete,
                Status = !executionPassed ? "Failed" : coverageComplete ? "Passed" : "Partial",
                Passed = executionPassed && coverageComplete
            };

            WriteReport(report, options.OutputPath);
            if (!report.ExecutionPassed)
            {
                Console.Error.WriteLine("POC-05 display robustness checks failed.");
                return 1;
            }

            Console.WriteLine($"POC-05 display robustness execution passed: status={report.Status}, coverageComplete={report.CoverageComplete}, cases={cases.Count}, monitors={screens.Length}, window DPI values={dpiValues.Count}, monitor DPI values={monitorDpiValues.Count}.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException)
        {
            Console.Error.WriteLine($"Display robustness failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static DisplayCaseReport TestPosition(
        IntPtr hwnd,
        string name,
        string location,
        string? expectedScreenDeviceName,
        int processId,
        ISet<uint> dpiValues,
        ISet<uint> monitorDpiValues)
    {
        Thread.Sleep(250);
        var window = AutomationElement.FromHandle(hwnd)
            ?? throw new InvalidOperationException("The moved window could not be reacquired through UI Automation.");
        var windowProcessId = ReadInt(window, AutomationElement.ProcessIdProperty);
        if (windowProcessId != processId)
        {
            return Failed(name, location, 0, $"Window process mismatch: expected {processId}, got {windowProcessId?.ToString() ?? "none"}.");
        }

        var actualScreen = FormsScreen.FromHandle(hwnd);
        if (expectedScreenDeviceName is not null && !string.Equals(actualScreen.DeviceName, expectedScreenDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(name, location, 0, $"Window landed on '{actualScreen.DeviceName}' instead of expected monitor '{expectedScreenDeviceName}'.");
        }

        var dpi = GetDpiForWindow(hwnd);
        dpiValues.Add(dpi);
        var monitorDpi = GetMonitorDpi(actualScreen);
        monitorDpiValues.Add(monitorDpi);
        var semanticTarget = FindUnique(window, "ValueTextBox");
        var semanticButton = FindUnique(window, "InvokeButton");
        var rect = GetWindowRectValue(hwnd);

        if (semanticTarget is null || semanticButton is null)
        {
            return Failed(name, location, dpi, "Semantic UI Automation targets were not uniquely reacquired.");
        }

        var coordinateResult = ValidateCoordinateFallback(semanticButton);
        if (!coordinateResult.Passed)
        {
            return Failed(name, location, dpi, coordinateResult.Reason);
        }

        return new DisplayCaseReport
        {
            Name = name,
            Location = location,
            Dpi = dpi,
            MonitorDpi = monitorDpi,
            WindowLeft = rect.Left,
            WindowTop = rect.Top,
            WindowWidth = rect.Width,
            WindowHeight = rect.Height,
            SemanticTargetAutomationId = ReadString(semanticTarget, AutomationElement.AutomationIdProperty),
            CoordinateTargetAutomationId = coordinateResult.AutomationId,
            CoordinateX = coordinateResult.X,
            CoordinateY = coordinateResult.Y,
            Passed = true,
            Status = "Passed",
            Reason = "Semantic target reacquired and screen-coordinate fallback mapped back to InvokeButton."
        };
    }

    private static CoordinateResult ValidateCoordinateFallback(AutomationElement element)
    {
        if (!element.TryGetClickablePoint(out var point))
        {
            return new CoordinateResult(false, null, 0, 0, "InvokeButton did not expose a clickable screen point.");
        }

        var pointElement = AutomationElement.FromPoint(point);
        var actionable = PromoteToActionableElement(pointElement);
        var automationId = actionable is null
            ? null
            : ReadString(actionable, AutomationElement.AutomationIdProperty);
        if (!string.Equals(automationId, "InvokeButton", StringComparison.Ordinal))
        {
            return new CoordinateResult(
                false,
                automationId,
                (int)Math.Round(point.X),
                (int)Math.Round(point.Y),
                $"Screen point mapped to '{automationId ?? "no element"}' instead of InvokeButton.");
        }

        return new CoordinateResult(true, automationId, (int)Math.Round(point.X), (int)Math.Round(point.Y), string.Empty);
    }

    private static AutomationElement? PromoteToActionableElement(AutomationElement? element)
    {
        var current = element;
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            if (!string.IsNullOrWhiteSpace(ReadString(current, AutomationElement.AutomationIdProperty)))
            {
                return current;
            }

            current = walker.GetParent(current);
        }

        return element;
    }

    private static AutomationElement? FindUnique(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var matches = window.FindAll(TreeScope.Descendants, condition);
        return matches.Count == 1 ? matches[0] : null;
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

    private static IntPtr GetNativeWindowHandle(AutomationElement window)
    {
        var value = window.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
        return value is int handle && handle != 0
            ? new IntPtr(handle)
            : throw new InvalidOperationException("The fixture window did not expose a native HWND.");
    }

    private static void MoveWindow(IntPtr hwnd, int left, int top, int width, int height)
    {
        if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate))
        {
            throw new ExternalException($"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private static void ShowAndActivate(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    private static RectValue GetWindowRectValue(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new ExternalException($"GetWindowRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new RectValue(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static uint GetDpiForWindow(IntPtr hwnd)
    {
        var dpi = GetDpiForWindowNative(hwnd);
        return dpi == 0 ? 96U : dpi;
    }

    private static uint GetMonitorDpi(FormsScreen screen)
    {
        var point = new NativePoint(screen.Bounds.Left + 1, screen.Bounds.Top + 1);
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _) != 0)
        {
            return 96U;
        }

        return dpiX == 0 ? 96U : dpiX;
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

    private static DisplayCaseReport Failed(string name, string location, uint dpi, string reason)
    {
        return new DisplayCaseReport
        {
            Name = name,
            Location = location,
            Dpi = dpi,
            Passed = false,
            Status = "Failed",
            Reason = reason
        };
    }

    private static void WriteReport(DisplayReport report, string? outputPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Display robustness report written to {Path.GetFullPath(outputPath)}");
    }

    private static void SetProcessDpiAwareness()
    {
        _ = SetProcessDpiAwarenessContext(new IntPtr(DpiAwarenessContextPerMonitorAwareV2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    private readonly record struct RectValue(int Left, int Top, int Width, int Height);
    private readonly record struct CoordinateResult(bool Passed, string? AutomationId, int X, int Y, string Reason);

    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiTypeEffective = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindowNative(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private sealed class Arguments
    {
        public const string Usage = "Usage: display-robustness --pid id [--output path]";
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

internal sealed class DisplayReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public int MonitorCount { get; init; }
    public required IReadOnlyList<MonitorReport> Monitors { get; init; }
    public required IReadOnlyList<uint> ObservedDpiValues { get; init; }
    public required IReadOnlyList<uint> ObservedMonitorDpiValues { get; init; }
    public bool NegativeCoordinatesObserved { get; init; }
    public bool MultiMonitorTested { get; init; }
    public bool MixedDpiObserved { get; init; }
    public required IReadOnlyList<DisplayCaseReport> Cases { get; init; }
    public bool ExecutionPassed { get; init; }
    public bool CoverageComplete { get; init; }
    public required string Status { get; init; }
    public bool Passed { get; init; }
}

internal sealed class MonitorReport
{
    public required string DeviceName { get; init; }
    public int BoundsLeft { get; init; }
    public int BoundsTop { get; init; }
    public int BoundsWidth { get; init; }
    public int BoundsHeight { get; init; }
    public int WorkAreaLeft { get; init; }
    public int WorkAreaTop { get; init; }
    public int WorkAreaWidth { get; init; }
    public int WorkAreaHeight { get; init; }
    public uint MonitorDpi { get; init; }
}

internal sealed class DisplayCaseReport
{
    public required string Name { get; init; }
    public required string Location { get; init; }
    public uint Dpi { get; init; }
    public uint MonitorDpi { get; init; }
    public int WindowLeft { get; init; }
    public int WindowTop { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public string? SemanticTargetAutomationId { get; init; }
    public string? CoordinateTargetAutomationId { get; init; }
    public int CoordinateX { get; init; }
    public int CoordinateY { get; init; }
    public bool Passed { get; init; }
    public bool Skipped { get; init; }
    public required string Status { get; init; }
    public required string Reason { get; init; }
}
