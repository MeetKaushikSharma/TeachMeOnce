using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace TeachMeOnce.CustomRenderedFallback;

internal static partial class Program
{
    private const uint SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint InputMouse = 0;
    private const uint GaRoot = 2;
    private const int MonitorDefaultToNearest = 2;
    private const int DpiAwarenessContextPerMonitorAwareV2 = -4;
    private static readonly Color TargetNormalColor = Color.FromArgb(40, 180, 120);
    private static readonly Color TargetPressedColor = Color.FromArgb(230, 160, 40);

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
            var baseline = InspectSemanticSurface(window, hwnd, process.Id);
            var cases = new List<CustomCaseReport>();
            var expectedPressed = false;

            try
            {
                ShowAndActivate(hwnd);
                cases.Add(RunValidClickCase(hwnd, "valid initial coordinate click", options.Framework, expectedPressed));
                expectedPressed = !expectedPressed;

                var beforeMove = GetWindowRectValue(hwnd);
                var beforeMoveContract = GetContract(options.Framework, hwnd);
                var oldScreenPoint = ClientToScreenPoint(hwnd, beforeMoveContract.TargetPoint);
                var movedLeft = beforeMove.Left + 180;
                var movedTop = beforeMove.Top + 70;
                MoveWindow(hwnd, movedLeft, movedTop, beforeMove.Width, beforeMove.Height);
                cases.Add(RunStalePointCase(hwnd, "stale coordinate after window move", options.Framework, oldScreenPoint));
                cases.Add(RunValidClickCase(hwnd, "valid coordinate after window move", options.Framework, expectedPressed));
                expectedPressed = !expectedPressed;

                var beforeResize = GetWindowRectValue(hwnd);
                var resizedWidth = Math.Max(640, beforeResize.Width - 80);
                var resizedHeight = Math.Max(460, beforeResize.Height - 40);
                ResizeWindow(hwnd, resizedWidth, resizedHeight);
                cases.Add(RunValidClickCase(hwnd, "valid coordinate after window resize", options.Framework, expectedPressed));
                expectedPressed = !expectedPressed;

                cases.Add(RunRejectedPointCase(hwnd, "point outside current target", options.Framework, RejectionKind.OutsideTarget));
                cases.Add(RunRejectedPointCase(hwnd, "decoy region", options.Framework, RejectionKind.Decoy));
                cases.Add(RunRejectedPointCase(hwnd, "ambiguous visual candidates", options.Framework, RejectionKind.Ambiguous));
            }
            finally
            {
                MoveWindow(hwnd, originalRect.Left, originalRect.Top, originalRect.Width, originalRect.Height);
            }

            var executionPassed = baseline.Passed && cases.All(testCase => testCase.Passed);
            var coverageComplete = baseline.Passed
                && cases.Any(testCase => testCase.Name == "valid initial coordinate click" && testCase.Status == "CoordinatePassed")
                && cases.Any(testCase => testCase.Name == "valid coordinate after window move" && testCase.Status == "CoordinatePassed")
                && cases.Any(testCase => testCase.Name == "valid coordinate after window resize" && testCase.Status == "CoordinatePassed")
                && cases.Where(testCase => testCase.Name is "point outside current target" or "decoy region" or "ambiguous visual candidates")
                    .All(testCase => testCase.Status == "CoordinateRejected" && !testCase.InputAttempted);

            var report = new CustomRenderedReport
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Framework = options.Framework,
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                WindowHandle = hwnd.ToInt64(),
                GroundTruthSource = "fixture-contract",
                SemanticBaseline = baseline,
                Cases = cases,
                ExecutionPassed = executionPassed,
                CoverageComplete = coverageComplete,
                Status = executionPassed ? coverageComplete ? "Passed" : "Partial" : "Failed",
                Passed = executionPassed && coverageComplete
            };

            WriteReport(report, options.OutputPath);
            Console.WriteLine($"POC-07 custom-rendered result: framework={options.Framework}, semantic={baseline.SemanticStatus}, executionPassed={executionPassed}, coverageComplete={coverageComplete}, cases={cases.Count}.");
            return executionPassed ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException or ElementNotAvailableException)
        {
            Console.Error.WriteLine($"Custom-rendered fallback failed ({exception.GetType().Name}): {exception.Message}");
            Console.Error.WriteLine(Arguments.Usage);
            return 2;
        }
    }

    private static SemanticBaselineReport InspectSemanticSurface(AutomationElement window, IntPtr hwnd, int processId)
    {
        var allElements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var semanticMatches = FindByIdentity(window, "CustomTarget", "CUSTOM TARGET");
        var usefulIds = new List<string>();
        for (var index = 0; index < allElements.Count; index++)
        {
            var id = ReadString(allElements[index], AutomationElement.AutomationIdProperty);
            var name = ReadString(allElements[index], AutomationElement.NameProperty);
            if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name))
            {
                usefulIds.Add($"{id ?? ""}|{name ?? ""}");
            }
        }

        var semanticUnavailable = semanticMatches.Count == 0;
        return new SemanticBaselineReport
        {
            Passed = semanticUnavailable,
            SemanticStatus = semanticUnavailable ? "SemanticUnavailable" : "UnexpectedSemanticExposure",
            WindowAccessible = true,
            WindowAutomationId = ReadString(window, AutomationElement.AutomationIdProperty),
            WindowName = ReadString(window, AutomationElement.NameProperty),
            WindowControlType = ReadControlType(window),
            WindowSupportedPatterns = GetSupportedPatterns(window),
            DescendantCount = allElements.Count,
            SemanticTargetMatchCount = semanticMatches.Count,
            UsefulElementIdentities = usefulIds.Distinct(StringComparer.Ordinal).Take(20).ToArray(),
            Reason = semanticUnavailable
                ? "The top-level window was inspectable, but the rendered target exposed no stable semantic identity."
                : "The custom-rendered target unexpectedly exposed a stable semantic identity; coordinate-only assumptions are not valid."
        };
    }

    private static CustomCaseReport RunValidClickCase(IntPtr hwnd, string name, string framework, bool expectedPressed)
    {
        var contract = GetContract(framework, hwnd);
        var preflight = PrepareCoordinate(hwnd, contract, contract.TargetPoint, 1, "fixture-contract-target");
        if (!preflight.Passed)
        {
            return FailedCase(name, preflight, preflight.Reason);
        }

        var beforeColor = ReadScreenColor(preflight.ScreenPoint);
        if (!MatchesColor(beforeColor, expectedPressed ? TargetPressedColor : TargetNormalColor))
        {
            return FailedCase(name, preflight, $"Unexpected pre-click target color {beforeColor}; expected the opposite of pressed={expectedPressed}.");
        }

        var inputAttempted = SendSingleClick(preflight.ScreenPoint);
        if (!inputAttempted)
        {
            return FailedCase(name, preflight, "SendInput did not report one complete click; no retry was attempted.");
        }

        var changed = WaitForColor(preflight.ScreenPoint, expectedPressed ? TargetNormalColor : TargetPressedColor, 1500, out var afterColor);
        return BuildCase(
            name,
            "CoordinatePassed",
            preflight,
            inputAttempted,
            inputAttempted,
            changed,
            changed ? "One guarded coordinate click changed the custom-rendered target state." : $"The click was injected, but the target color was {afterColor} instead of the expected state.");
    }

    private static CustomCaseReport RunStalePointCase(IntPtr hwnd, string name, string framework, NativePoint oldScreenPoint)
    {
        var contract = GetContract(framework, hwnd);
        var staleClientPoint = oldScreenPoint;
        if (!ScreenToClient(hwnd, ref staleClientPoint))
        {
            return BuildCase(name, "CoordinateRejected", new CoordinatePreflight
            {
                Passed = false,
                InputEligible = false,
                PointInsideCurrentTarget = false,
                CandidateCount = 1,
                CandidateSource = "stale-screen-point",
                ScreenPoint = oldScreenPoint,
                NativeWindowCheck = "NotEvaluated",
                UiaPointResolution = "NotEvaluated",
                Reason = "The old screen point could not be converted into the moved window's client coordinates."
            }, false, false, true, "The stale point was rejected because the moved window could not map it into client coordinates.");
        }

        var preflight = PrepareCoordinate(hwnd, contract, staleClientPoint, 1, "stale-screen-point");
        var rejected = !preflight.PointInsideCurrentTarget;
        return BuildCase(
            name,
            rejected ? "CoordinateRejected" : "Failed",
            preflight,
            false,
            false,
            rejected,
            rejected
                ? $"The old screen point ({oldScreenPoint.X},{oldScreenPoint.Y}) was rejected after the window moved; a new point was required."
                : "The old point remained inside the current target after movement; stale coordinate protection failed.");
    }

    private static CustomCaseReport RunRejectedPointCase(IntPtr hwnd, string name, string framework, RejectionKind kind)
    {
        var contract = GetContract(framework, hwnd);
        var point = kind switch
        {
            RejectionKind.OutsideTarget => new NativePoint(20, 20),
            RejectionKind.Decoy => contract.DecoyPoint,
            RejectionKind.Ambiguous => contract.TargetPoint,
            _ => contract.TargetPoint
        };
        var candidateCount = kind == RejectionKind.Ambiguous ? 2 : 1;
        var preflight = PrepareCoordinate(hwnd, contract, point, candidateCount, kind == RejectionKind.Decoy ? "fixture-contract-decoy" : "fixture-contract");
        var expectedReason = kind switch
        {
            RejectionKind.OutsideTarget => "The candidate point is outside the current safe target inset.",
            RejectionKind.Decoy => "The candidate corresponds to the documented decoy region, not the target.",
            RejectionKind.Ambiguous => "Two visual candidates were reported; ambiguous actions are refused.",
            _ => "The candidate was rejected."
        };
        var passed = !preflight.Passed && !preflight.InputEligible;
        return BuildCase(name, passed ? "CoordinateRejected" : "Failed", preflight, false, false, passed, passed ? expectedReason : preflight.Reason);
    }

    private static CoordinatePreflight PrepareCoordinate(
        IntPtr hwnd,
        CoordinateContract contract,
        NativePoint clientPoint,
        int candidateCount,
        string candidateSource)
    {
        var currentRect = GetWindowRectValue(hwnd);
        var clientRect = GetClientRectValue(hwnd);
        var screenPoint = ClientToScreenPoint(hwnd, clientPoint);
        var currentTarget = contract.TargetRect;
        var pointInsideClient = clientPoint.X >= 0 && clientPoint.Y >= 0 && clientPoint.X < clientRect.Width && clientPoint.Y < clientRect.Height;
        var safeTarget = new IntRect(
            currentTarget.Left + contract.SafeInset,
            currentTarget.Top + contract.SafeInset,
            currentTarget.Width - (contract.SafeInset * 2),
            currentTarget.Height - (contract.SafeInset * 2));
        var pointInsideTarget = safeTarget.Contains(clientPoint);
        var visibleWindow = IsWindowVisible(hwnd);
        var enabledWindow = IsWindowEnabled(hwnd);
        var notMinimized = !IsIconic(hwnd);
        var visible = visibleWindow && enabledWindow && notMinimized;
        var owner = WindowFromPoint(screenPoint);
        var root = owner == IntPtr.Zero ? IntPtr.Zero : GetAncestor(owner, GaRoot);
        _ = GetWindowThreadProcessId(root, out var ownerPid);
        var foreground = GetForegroundWindow();
        var foregroundPid = GetWindowThreadProcessId(foreground, out var currentForegroundPid) == 0 ? 0U : currentForegroundPid;
        var uiaPoint = DescribeUiaPoint(screenPoint);
        var reason = candidateCount != 1
            ? "The candidate set was ambiguous; no input was eligible."
            : !visible
                ? "The target window is hidden, disabled, or minimized."
                : !pointInsideClient
                    ? "The candidate point is outside the current client area."
                    : !pointInsideTarget
                        ? "The candidate point is outside the current safe target inset."
                        : root != hwnd || ownerPid != (uint)GetWindowProcessId(hwnd)
                            ? "WindowFromPoint did not resolve to the expected target window and process."
                            : foreground != hwnd || foregroundPid != (uint)GetWindowProcessId(hwnd)
                                ? "The expected target window is not the verified foreground window."
                                : "Coordinate preflight passed.";
        return new CoordinatePreflight
        {
            Passed = candidateCount == 1 && visible && pointInsideClient && pointInsideTarget && root == hwnd && ownerPid == (uint)GetWindowProcessId(hwnd) && foreground == hwnd && foregroundPid == (uint)GetWindowProcessId(hwnd),
            InputEligible = candidateCount == 1 && visible && pointInsideClient && pointInsideTarget && root == hwnd && ownerPid == (uint)GetWindowProcessId(hwnd) && foreground == hwnd && foregroundPid == (uint)GetWindowProcessId(hwnd),
            PointInsideCurrentTarget = pointInsideTarget,
            Visible = visibleWindow,
            Enabled = enabledWindow,
            NotMinimized = notMinimized,
            PointInsideClient = pointInsideClient,
            ForegroundVerified = foreground == hwnd && foregroundPid == (uint)GetWindowProcessId(hwnd),
            ProcessIdentityVerified = root == hwnd && ownerPid == (uint)GetWindowProcessId(hwnd),
            CandidateCount = candidateCount,
            CandidateSource = candidateSource,
            ClientPoint = clientPoint,
            ScreenPoint = screenPoint,
            WindowRect = currentRect,
            ClientRect = clientRect,
            WindowHandle = hwnd,
            NativeWindowCheck = root == hwnd && ownerPid == (uint)GetWindowProcessId(hwnd) ? "Passed" : "Failed",
            UiaPointResolution = uiaPoint,
            Reason = reason
        };
    }

    private static CustomCaseReport BuildCase(
        string name,
        string status,
        CoordinatePreflight preflight,
        bool inputAttempted,
        bool injected,
        bool postClickVerified,
        string reason)
    {
        return new CustomCaseReport
        {
            Name = name,
            Status = status,
            CandidateSource = preflight.CandidateSource,
            CandidateCount = preflight.CandidateCount,
            ClientPoint = new PointReport(preflight.ClientPoint.X, preflight.ClientPoint.Y),
            ScreenPoint = new PointReport(preflight.ScreenPoint.X, preflight.ScreenPoint.Y),
            WindowRect = new RectangleReport(preflight.WindowRect.Left, preflight.WindowRect.Top, preflight.WindowRect.Width, preflight.WindowRect.Height),
            ClientRect = new RectangleReport(preflight.ClientRect.Left, preflight.ClientRect.Top, preflight.ClientRect.Width, preflight.ClientRect.Height),
            Dpi = GetDpiForWindow(preflight.WindowHandle),
            NativeWindowCheck = preflight.NativeWindowCheck,
            UiaPointResolution = preflight.UiaPointResolution,
            PreflightChecks = new PreflightChecksReport
            {
                Visible = preflight.Visible,
                Enabled = preflight.Enabled,
                NotMinimized = preflight.NotMinimized,
                PointInsideClient = preflight.PointInsideClient,
                PointInsideCurrentTarget = preflight.PointInsideCurrentTarget,
                ForegroundVerified = preflight.ForegroundVerified,
                ProcessIdentityVerified = preflight.ProcessIdentityVerified,
                CandidateUnique = preflight.CandidateCount == 1
            },
            InputAttempted = inputAttempted,
            Injected = injected,
            PostClickVerified = inputAttempted && postClickVerified,
            SafetyOutcomeVerified = !inputAttempted && postClickVerified,
            Passed = status == "CoordinatePassed"
                ? inputAttempted && postClickVerified
                : status == "CoordinateRejected" && !inputAttempted && postClickVerified,
            Reason = reason
        };
    }

    private static CustomCaseReport FailedCase(string name, CoordinatePreflight preflight, string reason)
    {
        return BuildCase(name, "Failed", preflight, false, false, false, reason);
    }

    private static CoordinateContract GetContract(string framework, IntPtr hwnd)
    {
        var scale = framework == "WPF" ? GetDpiForWindow(hwnd) / 96.0 : 1.0;
        var target = ScaleRect(new IntRect(60, 90, 220, 110), scale);
        var decoy = ScaleRect(new IntRect(360, 90, 220, 110), scale);
        return new CoordinateContract
        {
            TargetRect = target,
            DecoyRect = decoy,
            TargetPoint = new NativePoint(target.Left + 30, target.Top + 20),
            DecoyPoint = new NativePoint(decoy.Left + 30, decoy.Top + 20),
            SafeInset = Math.Max(12, (int)Math.Round(12 * scale))
        };
    }

    private static IntRect ScaleRect(IntRect rectangle, double scale)
    {
        return new IntRect(
            (int)Math.Round(rectangle.Left * scale),
            (int)Math.Round(rectangle.Top * scale),
            (int)Math.Round(rectangle.Width * scale),
            (int)Math.Round(rectangle.Height * scale));
    }

    private static List<AutomationElement> FindByIdentity(AutomationElement window, string automationId, string name)
    {
        var matches = new List<AutomationElement>();
        var byId = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        for (var index = 0; index < byId.Count; index++)
        {
            matches.Add(byId[index]);
        }

        var byName = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
        for (var index = 0; index < byName.Count; index++)
        {
            if (!matches.Contains(byName[index]))
            {
                matches.Add(byName[index]);
            }
        }

        return matches;
    }

    private static string DescribeUiaPoint(NativePoint point)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            if (element is null)
            {
                return "NoElement";
            }

            var id = ReadString(element, AutomationElement.AutomationIdProperty);
            var name = ReadString(element, AutomationElement.NameProperty);
            var controlType = ReadControlType(element);
            return string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)
                ? $"GenericOrUnidentified:{controlType}"
                : $"Semantic:{controlType}:{id ?? ""}:{name ?? ""}";
        }
        catch (ElementNotAvailableException)
        {
            return "ElementUnavailable";
        }
        catch (COMException exception)
        {
            return $"UIAError:0x{exception.HResult:X8}";
        }
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : value?.ToString();
    }

    private static string ReadControlType(AutomationElement element)
    {
        var value = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty);
        return value is ControlType type ? type.ProgrammaticName : "Unknown";
    }

    private static IReadOnlyList<string> GetSupportedPatterns(AutomationElement element)
    {
        var patterns = new List<string>();
        AddPattern(element, InvokePattern.Pattern, "Invoke", patterns);
        AddPattern(element, ValuePattern.Pattern, "Value", patterns);
        AddPattern(element, TextPattern.Pattern, "Text", patterns);
        AddPattern(element, TogglePattern.Pattern, "Toggle", patterns);
        AddPattern(element, SelectionPattern.Pattern, "Selection", patterns);
        AddPattern(element, ExpandCollapsePattern.Pattern, "ExpandCollapse", patterns);
        AddPattern(element, TransformPattern.Pattern, "Transform", patterns);
        AddPattern(element, WindowPattern.Pattern, "Window", patterns);
        return patterns;
    }

    private static void AddPattern(AutomationElement element, AutomationPattern pattern, string name, ICollection<string> patterns)
    {
        if (element.TryGetCurrentPattern(pattern, out _))
        {
            patterns.Add(name);
        }
    }

    private static AutomationElement WaitForWindow(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            for (var index = 0; index < windows.Count; index++)
            {
                if ((ReadString(windows[index], AutomationElement.NameProperty) ?? string.Empty).Contains("Custom Fixture", StringComparison.OrdinalIgnoreCase))
                {
                    return windows[index];
                }
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"No custom-rendered UI Automation window was found for process {processId}.");
    }

    private static IntPtr GetNativeWindowHandle(AutomationElement window)
    {
        var value = window.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
        return value is int handle && handle != 0
            ? new IntPtr(handle)
            : throw new InvalidOperationException("The custom-rendered fixture did not expose a native HWND.");
    }

    private static void ShowAndActivate(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (GetForegroundWindow() == hwnd)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException("The custom-rendered fixture could not be verified as the foreground window.");
    }

    private static void MoveWindow(IntPtr hwnd, int left, int top, int width, int height)
    {
        if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate))
        {
            throw new ExternalException($"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        ShowAndActivate(hwnd);
    }

    private static void ResizeWindow(IntPtr hwnd, int width, int height)
    {
        var current = GetWindowRectValue(hwnd);
        if (!SetWindowPos(hwnd, IntPtr.Zero, current.Left, current.Top, width, height, SwpNoZOrder | SwpNoActivate))
        {
            throw new ExternalException($"SetWindowPos resize failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        ShowAndActivate(hwnd);
    }

    private static bool SendSingleClick(NativePoint point)
    {
        if (!SetCursorPos(point.X, point.Y))
        {
            return false;
        }

        var inputs = new[]
        {
            new NativeInput { Type = InputMouse, Mouse = new NativeMouseInput { Flags = MouseEventLeftDown } },
            new NativeInput { Type = InputMouse, Mouse = new NativeMouseInput { Flags = MouseEventLeftUp } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
    }

    private static Color ReadScreenColor(NativePoint point)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return Color.Empty;
        }

        try
        {
            var value = GetPixel(dc, point.X, point.Y);
            return Color.FromArgb((int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool WaitForColor(NativePoint point, Color expected, int timeoutMilliseconds, out Color actual)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        actual = Color.Empty;
        while (DateTime.UtcNow < deadline)
        {
            actual = ReadScreenColor(point);
            if (MatchesColor(actual, expected))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static bool MatchesColor(Color actual, Color expected)
    {
        return actual != Color.Empty
            && Math.Abs(actual.R - expected.R) <= 24
            && Math.Abs(actual.G - expected.G) <= 24
            && Math.Abs(actual.B - expected.B) <= 24;
    }

    private static void WriteReport(CustomRenderedReport report, string? outputPath)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Custom-rendered fallback report written to {Path.GetFullPath(outputPath)}");
    }

    private static uint GetDpiForWindow(IntPtr hwnd)
    {
        var dpi = GetDpiForWindowNative(hwnd);
        return dpi == 0 ? 96U : dpi;
    }

    private static RectValue GetWindowRectValue(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new ExternalException($"GetWindowRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new RectValue(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static RectValue GetClientRectValue(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect))
        {
            throw new ExternalException($"GetClientRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new RectValue(0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static NativePoint ClientToScreenPoint(IntPtr hwnd, NativePoint clientPoint)
    {
        if (!ClientToScreen(hwnd, ref clientPoint))
        {
            throw new ExternalException($"ClientToScreen failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return clientPoint;
    }

    private static int GetWindowProcessId(IntPtr hwnd)
    {
        return GetWindowThreadProcessId(hwnd, out var processId) == 0 ? 0 : checked((int)processId);
    }

    private static void SetProcessDpiAwareness()
    {
        _ = SetProcessDpiAwarenessContext(new IntPtr(DpiAwarenessContextPerMonitorAwareV2));
    }

    private enum RejectionKind
    {
        OutsideTarget,
        Decoy,
        Ambiguous
    }

    private sealed class Arguments
    {
        public const string Usage = "Usage: custom-rendered-fallback --pid id --framework WPF|WinForms [--output path]";
        public int ProcessId { get; private init; }
        public string Framework { get; private init; } = string.Empty;
        public string? OutputPath { get; private init; }

        public static Arguments Parse(string[] args)
        {
            int? processId = null;
            string? framework = null;
            string? outputPath = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pid":
                        processId = int.Parse(Next(args, ref index, "--pid"));
                        break;
                    case "--framework":
                        framework = Next(args, ref index, "--framework");
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

            if (framework is not ("WPF" or "WinForms"))
            {
                throw new ArgumentException("--framework must be WPF or WinForms.");
            }

            return new Arguments { ProcessId = processId.Value, Framework = framework!, OutputPath = outputPath };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeMouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindowNative(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private readonly record struct RectValue(int Left, int Top, int Width, int Height);
    private readonly record struct IntRect(int Left, int Top, int Width, int Height)
    {
        public bool Contains(NativePoint point) => point.X >= Left && point.Y >= Top && point.X < Left + Width && point.Y < Top + Height;
    }

    private sealed class CoordinateContract
    {
        public required IntRect TargetRect { get; init; }
        public required IntRect DecoyRect { get; init; }
        public required NativePoint TargetPoint { get; init; }
        public required NativePoint DecoyPoint { get; init; }
        public int SafeInset { get; init; }
    }

    private sealed class CoordinatePreflight
    {
        public bool Passed { get; init; }
        public bool InputEligible { get; init; }
        public bool PointInsideCurrentTarget { get; init; }
        public bool Visible { get; init; }
        public bool Enabled { get; init; }
        public bool NotMinimized { get; init; }
        public bool PointInsideClient { get; init; }
        public bool ForegroundVerified { get; init; }
        public bool ProcessIdentityVerified { get; init; }
        public int CandidateCount { get; init; }
        public required string CandidateSource { get; init; }
        public NativePoint ClientPoint { get; init; }
        public NativePoint ScreenPoint { get; init; }
        public RectValue WindowRect { get; init; }
        public RectValue ClientRect { get; init; }
        public IntPtr WindowHandle { get; init; }
        public required string NativeWindowCheck { get; init; }
        public required string UiaPointResolution { get; init; }
        public required string Reason { get; init; }
    }
}

internal sealed class CustomRenderedReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Framework { get; init; }
    public int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public long WindowHandle { get; init; }
    public required string GroundTruthSource { get; init; }
    public required SemanticBaselineReport SemanticBaseline { get; init; }
    public required IReadOnlyList<CustomCaseReport> Cases { get; init; }
    public bool ExecutionPassed { get; init; }
    public bool CoverageComplete { get; init; }
    public required string Status { get; init; }
    public bool Passed { get; init; }
}

internal sealed class SemanticBaselineReport
{
    public bool Passed { get; init; }
    public required string SemanticStatus { get; init; }
    public bool WindowAccessible { get; init; }
    public string? WindowAutomationId { get; init; }
    public string? WindowName { get; init; }
    public required string WindowControlType { get; init; }
    public required IReadOnlyList<string> WindowSupportedPatterns { get; init; }
    public int DescendantCount { get; init; }
    public int SemanticTargetMatchCount { get; init; }
    public required IReadOnlyList<string> UsefulElementIdentities { get; init; }
    public required string Reason { get; init; }
}

internal sealed class CustomCaseReport
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required string CandidateSource { get; init; }
    public int CandidateCount { get; init; }
    public PointReport ClientPoint { get; init; }
    public PointReport ScreenPoint { get; init; }
    public RectangleReport WindowRect { get; init; }
    public RectangleReport ClientRect { get; init; }
    public uint Dpi { get; init; }
    public required string NativeWindowCheck { get; init; }
    public required string UiaPointResolution { get; init; }
    public required PreflightChecksReport PreflightChecks { get; init; }
    public bool InputAttempted { get; init; }
    public bool Injected { get; init; }
    public bool PostClickVerified { get; init; }
    public bool SafetyOutcomeVerified { get; init; }
    public bool Passed { get; init; }
    public required string Reason { get; init; }
}

internal sealed class PreflightChecksReport
{
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool NotMinimized { get; init; }
    public bool PointInsideClient { get; init; }
    public bool PointInsideCurrentTarget { get; init; }
    public bool ForegroundVerified { get; init; }
    public bool ProcessIdentityVerified { get; init; }
    public bool CandidateUnique { get; init; }
}

internal readonly record struct PointReport(int X, int Y);
internal readonly record struct RectangleReport(int Left, int Top, int Width, int Height);
