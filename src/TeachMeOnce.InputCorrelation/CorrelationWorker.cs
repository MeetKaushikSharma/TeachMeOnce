using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace TeachMeOnce.InputCorrelation;

internal sealed class CorrelationWorker
{
    private readonly InputEventBuffer _buffer;
    private readonly uint _targetProcessId;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<CorrelationReport> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public CorrelationWorker(InputEventBuffer buffer, uint targetProcessId)
    {
        _buffer = buffer;
        _targetProcessId = targetProcessId;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TeachMeOnce.UIAutomationCorrelation"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public Task<CorrelationReport> Start()
    {
        _thread.Start();
        return _completion.Task;
    }

    private void Run()
    {
        try
        {
            var events = new List<CorrelationRecord>();
            foreach (var input in _buffer.ReadAll())
            {
                events.Add(Correlate(input));
            }

            _completion.SetResult(new CorrelationReport
            {
                SchemaVersion = 1,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                TargetProcessId = _targetProcessId,
                DroppedInputEvents = _buffer.DroppedCount,
                EventCount = events.Count,
                Events = events
            });
        }
        catch (Exception exception)
        {
            _completion.SetException(exception);
        }
    }

    private CorrelationRecord Correlate(RawInputEvent input)
    {
        var failures = new List<string>();
        WindowSnapshot? window = null;
        ElementSnapshot? targetElement = null;
        ElementSnapshot? focusedElement = null;

        try
        {
            var windowHandle = ResolveWindowHandle(input);
            window = SnapshotWindow(windowHandle, failures);

            var focused = TryGetFocusedElement(failures);
            focusedElement = SnapshotElement(focused, failures);

            if (input.Kind == RawInputKind.Mouse)
            {
                var pointElement = AutomationElement.FromPoint(new Point(input.X, input.Y));
                var semanticTarget = PromoteToActionableElement(pointElement, failures);
                targetElement = SnapshotElement(semanticTarget, failures);
            }
            else
            {
                targetElement = focusedElement;
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or ArgumentException)
        {
            failures.Add($"{exception.GetType().Name}: {exception.Message}");
        }

        return new CorrelationRecord
        {
            Sequence = input.Sequence,
            InputKind = input.Kind.ToString(),
            Message = DescribeMessage(input),
            TimestampUtc = input.TimestampUtc,
            MonotonicTimestamp = input.MonotonicTimestamp,
            Injected = input.Injected,
            IsDown = IsDownMessage(input.Message),
            X = input.Kind == RawInputKind.Mouse ? input.X : null,
            Y = input.Kind == RawInputKind.Mouse ? input.Y : null,
            VirtualKey = input.Kind == RawInputKind.Keyboard ? input.VirtualKey : null,
            ScanCode = input.Kind == RawInputKind.Keyboard ? input.ScanCode : null,
            Window = window,
            TargetElement = targetElement,
            FocusedElement = focusedElement,
            TargetProcessMatch = window?.ProcessId == _targetProcessId,
            Failure = failures.Count == 0 ? null : string.Join(" | ", failures)
        };
    }

    private static IntPtr ResolveWindowHandle(RawInputEvent input)
    {
        if (input.Kind == RawInputKind.Mouse)
        {
            var pointWindow = WindowsInterop.WindowFromPoint(new WindowsInterop.NativePoint
            {
                X = input.X,
                Y = input.Y
            });
            return pointWindow == IntPtr.Zero
                ? IntPtr.Zero
                : WindowsInterop.GetAncestor(pointWindow, WindowsInterop.AncestorRoot);
        }

        var foreground = WindowsInterop.GetForegroundWindow();
        return foreground == IntPtr.Zero
            ? IntPtr.Zero
            : WindowsInterop.GetAncestor(foreground, WindowsInterop.AncestorRoot);
    }

    private static AutomationElement? TryGetFocusedElement(ICollection<string> failures)
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException exception)
        {
            failures.Add($"FocusedElement: {exception.Message}");
            return null;
        }
    }

    private static AutomationElement? PromoteToActionableElement(
        AutomationElement? element,
        ICollection<string> failures)
    {
        if (element is null)
        {
            failures.Add("Mouse target: AutomationElement.FromPoint returned no element.");
            return null;
        }

        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            var automationId = ReadString(current, AutomationElement.AutomationIdProperty);
            var controlType = ReadControlType(current);
            if (!string.IsNullOrWhiteSpace(automationId) || IsActionableControlType(controlType))
            {
                return current;
            }

            try
            {
                current = walker.GetParent(current);
            }
            catch (ElementNotAvailableException exception)
            {
                failures.Add($"Mouse target ancestry: {exception.Message}");
                return element;
            }
        }

        return element;
    }

    private static bool IsActionableControlType(string? controlType)
    {
        return controlType is "ControlType.Button"
            or "ControlType.CheckBox"
            or "ControlType.ComboBox"
            or "ControlType.Edit"
            or "ControlType.List"
            or "ControlType.ListItem"
            or "ControlType.MenuItem";
    }

    private static WindowSnapshot? SnapshotWindow(IntPtr windowHandle, ICollection<string> failures)
    {
        if (windowHandle == IntPtr.Zero)
        {
            failures.Add("WindowFromInput: no top-level window was found.");
            return null;
        }

        WindowsInterop.GetWindowThreadProcessId(windowHandle, out var processId);
        string? processName = null;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            failures.Add($"Process: process {processId} no longer exists.");
        }

        try
        {
            var element = AutomationElement.FromHandle(windowHandle);
            return new WindowSnapshot
            {
                Handle = windowHandle.ToInt64(),
                ProcessId = processId,
                ProcessName = processName,
                Name = ReadString(element, AutomationElement.NameProperty),
                AutomationId = ReadString(element, AutomationElement.AutomationIdProperty),
                ClassName = ReadString(element, AutomationElement.ClassNameProperty),
                FrameworkId = ReadString(element, AutomationElement.FrameworkIdProperty)
            };
        }
        catch (ElementNotAvailableException exception)
        {
            failures.Add($"Window UI Automation: {exception.Message}");
            return new WindowSnapshot
            {
                Handle = windowHandle.ToInt64(),
                ProcessId = processId,
                ProcessName = processName
            };
        }
    }

    private static ElementSnapshot? SnapshotElement(AutomationElement? element, ICollection<string> failures)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return new ElementSnapshot
            {
                Name = ReadString(element, AutomationElement.NameProperty),
                AutomationId = ReadString(element, AutomationElement.AutomationIdProperty),
                ControlType = ReadControlType(element),
                ClassName = ReadString(element, AutomationElement.ClassNameProperty),
                FrameworkId = ReadString(element, AutomationElement.FrameworkIdProperty),
                IsEnabled = ReadBool(element, AutomationElement.IsEnabledProperty),
                IsPassword = ReadBool(element, AutomationElement.IsPasswordProperty)
            };
        }
        catch (ElementNotAvailableException exception)
        {
            failures.Add($"Element UI Automation: {exception.Message}");
            return null;
        }
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value == AutomationElement.NotSupported ? null : value?.ToString();
    }

    private static string? ReadControlType(AutomationElement element)
    {
        return (element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType)
            ?.ProgrammaticName;
    }

    private static bool? ReadBool(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property);
        return value is bool boolean ? boolean : null;
    }

    private static string DescribeMessage(RawInputEvent input)
    {
        return input.Kind == RawInputKind.Mouse
            ? input.Message switch
            {
                0x0201 => "LeftButtonDown",
                0x0202 => "LeftButtonUp",
                0x0204 => "RightButtonDown",
                0x0205 => "RightButtonUp",
                0x0207 => "MiddleButtonDown",
                0x0208 => "MiddleButtonUp",
                0x020B => "XButtonDown",
                0x020C => "XButtonUp",
                _ => $"MouseMessage0x{input.Message:X4}"
            }
            : input.Message switch
            {
                0x0100 => $"KeyDown(VK_{input.VirtualKey:X2})",
                0x0101 => $"KeyUp(VK_{input.VirtualKey:X2})",
                0x0104 => $"SysKeyDown(VK_{input.VirtualKey:X2})",
                0x0105 => $"SysKeyUp(VK_{input.VirtualKey:X2})",
                _ => $"KeyboardMessage0x{input.Message:X4}"
            };
    }

    private static bool IsDownMessage(uint message)
    {
        return message is 0x0100 or 0x0104 or 0x0201 or 0x0204 or 0x0207 or 0x020B;
    }
}

internal sealed class CorrelationReport
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public uint TargetProcessId { get; init; }
    public long DroppedInputEvents { get; init; }
    public int EventCount { get; init; }
    public required IReadOnlyList<CorrelationRecord> Events { get; init; }
}

internal sealed class CorrelationRecord
{
    public long Sequence { get; init; }
    public required string InputKind { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public long MonotonicTimestamp { get; init; }
    public bool Injected { get; init; }
    public bool IsDown { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public uint? VirtualKey { get; init; }
    public uint? ScanCode { get; init; }
    public WindowSnapshot? Window { get; init; }
    public ElementSnapshot? TargetElement { get; init; }
    public ElementSnapshot? FocusedElement { get; init; }
    public bool TargetProcessMatch { get; init; }
    public string? Failure { get; init; }
}

internal sealed class WindowSnapshot
{
    public long Handle { get; init; }
    public uint ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
}

internal sealed class ElementSnapshot
{
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsPassword { get; init; }
}
