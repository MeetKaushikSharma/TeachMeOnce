using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Automation;

public enum RawInputKind
{
    Mouse,
    Keyboard
}

public sealed record RawInputEvent(
    long Sequence,
    RawInputKind Kind,
    uint Message,
    DateTimeOffset TimestampUtc,
    long MonotonicTimestamp,
    bool Injected,
    int X,
    int Y,
    uint VirtualKey,
    uint ScanCode,
    uint Flags,
    IntPtr WindowHandle,
    int ProcessId,
    IntPtr FocusedWindowHandle);

public sealed record InputCaptureResult(
    IReadOnlyList<RawInputEvent> Events,
    long DroppedCount,
    bool Overflowed);

public sealed class BoundedInputEventQueue : IDisposable
{
    private readonly BlockingCollection<RawInputEvent> _events;
    private long _dropped;

    public BoundedInputEventQueue(int capacity = 2048)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _events = new BlockingCollection<RawInputEvent>(new ConcurrentQueue<RawInputEvent>(), capacity);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(RawInputEvent input)
    {
        try
        {
            if (_events.TryAdd(input))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Completion is an expected lifecycle boundary.
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public IEnumerable<RawInputEvent> ReadAll() => _events.GetConsumingEnumerable();

    public void Complete() => _events.CompleteAdding();

    public void Dispose() => _events.Dispose();
}

public sealed class InputHookSession : IDisposable
{
    private readonly BoundedInputEventQueue _events;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly WindowsInterop.HookProc _keyboardProc;
    private readonly WindowsInterop.HookProc _mouseProc;
    private readonly Action<RawInputEvent>? _eventObserver;
    private readonly List<RawInputEvent> _captured = new();
    private readonly object _gate = new();
    private Exception? _startupException;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private long _sequence;
    private int _started;
    private int _stopped;
    private Task? _drainTask;
    private bool _startupExceptionReported;

    public InputHookSession(int capacity = 2048, Action<RawInputEvent>? eventObserver = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _events = new BoundedInputEventQueue(capacity);
        _eventObserver = eventObserver;
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "TeachMeOnce.ExternalInputHooks"
        };
    }

    public long DroppedCount => _events.DroppedCount;

    public bool IsRunning => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _stopped) == 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The input hook session has already been started.");
        }

        _thread.Start();
        _drainTask = Task.Run(DrainEvents);
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The input hook thread did not initialize within five seconds.");
        }

        if (_startupException is not null)
        {
            _startupExceptionReported = true;
            throw new InvalidOperationException("The input hooks could not be installed.", _startupException);
        }
    }

    public async Task<InputCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _started) == 0)
        {
            return new InputCaptureResult(Array.Empty<RawInputEvent>(), DroppedCount, false);
        }

        if (Interlocked.Exchange(ref _stopped, 1) == 0 && _threadId != 0 && _thread.IsAlive)
        {
            if (!WindowsInterop.PostThreadMessage(_threadId, WindowsInterop.WmQuit, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The input hook thread could not be stopped.");
            }
        }

        await Task.Run(() =>
        {
            if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("The input hook thread did not stop within five seconds.");
            }
        }, CancellationToken.None).ConfigureAwait(false);

        _events.Complete();
        if (_drainTask is not null)
        {
            await _drainTask.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_startupException is not null && !_startupExceptionReported)
        {
            throw new InvalidOperationException("The input hook session stopped because its message loop failed.", _startupException);
        }

        return SnapshotResult();
    }

    public InputCaptureResult SnapshotResult()
    {
        lock (_gate)
        {
            return new InputCaptureResult(_captured.ToArray(), DroppedCount, DroppedCount > 0);
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _started) == 1 && Volatile.Read(ref _stopped) == 0)
        {
            StopAsync().GetAwaiter().GetResult();
        }

        _events.Dispose();
        _ready.Dispose();
    }

    private void RunMessageLoop()
    {
        _threadId = WindowsInterop.GetCurrentThreadId();
        try
        {
            _keyboardHook = WindowsInterop.SetWindowsHookEx(WindowsInterop.WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed.");
            }

            _mouseHook = WindowsInterop.SetWindowsHookEx(WindowsInterop.WhMouseLl, _mouseProc, IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) failed.");
            }
        }
        catch (Exception exception)
        {
            _startupException = exception;
        }
        finally
        {
            _ready.Set();
        }

        if (_startupException is not null)
        {
            UninstallHooks();
            _events.Complete();
            return;
        }

        try
        {
            while (true)
            {
                var result = WindowsInterop.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed for the input hook thread.");
                }

                WindowsInterop.TranslateMessage(ref message);
                WindowsInterop.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupException ??= exception;
        }
        finally
        {
            UninstallHooks();
        }
    }

    private void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            WindowsInterop.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            WindowsInterop.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (message is WindowsInterop.WmKeyDown or WindowsInterop.WmKeyUp or WindowsInterop.WmSysKeyDown or WindowsInterop.WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<WindowsInterop.KeyboardHookData>(lParam);
                Enqueue(RawInputKind.Keyboard, message, (data.Flags & WindowsInterop.LlkhfInjected) != 0, 0, 0, data.VirtualKey, data.ScanCode, data.Flags);
            }
        }

        return WindowsInterop.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (message is WindowsInterop.WmLButtonDown or WindowsInterop.WmLButtonUp or WindowsInterop.WmRButtonDown or WindowsInterop.WmRButtonUp
                or WindowsInterop.WmMButtonDown or WindowsInterop.WmMButtonUp or WindowsInterop.WmXButtonDown or WindowsInterop.WmXButtonUp)
            {
                var data = Marshal.PtrToStructure<WindowsInterop.MouseHookData>(lParam);
                Enqueue(RawInputKind.Mouse, message, (data.Flags & WindowsInterop.LlmhfInjected) != 0, data.Point.X, data.Point.Y, 0, 0, data.Flags);
            }
        }

        return WindowsInterop.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Enqueue(RawInputKind kind, uint message, bool injected, int x, int y, uint virtualKey, uint scanCode, uint flags)
    {
        var handle = kind == RawInputKind.Mouse
            ? WindowsInterop.GetAncestor(WindowsInterop.WindowFromPoint(new WindowsInterop.NativePoint { X = x, Y = y }), WindowsInterop.AncestorRoot)
            : WindowsInterop.GetAncestor(WindowsInterop.GetForegroundWindow(), WindowsInterop.AncestorRoot);
        WindowsInterop.GetWindowThreadProcessId(handle, out var processId);
        var focusedHandle = IntPtr.Zero;
        if (kind == RawInputKind.Keyboard && handle != IntPtr.Zero)
        {
            var threadInfo = new WindowsInterop.GuiThreadInfo { Size = (uint)Marshal.SizeOf<WindowsInterop.GuiThreadInfo>() };
            if (WindowsInterop.GetGUIThreadInfo(WindowsInterop.GetWindowThreadProcessId(handle, out _), ref threadInfo))
            {
                focusedHandle = threadInfo.FocusWindow;
            }
        }
        var input = new RawInputEvent(
            Interlocked.Increment(ref _sequence),
            kind,
            message,
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            injected,
            x,
            y,
            virtualKey,
            scanCode,
            flags,
            handle,
            unchecked((int)processId),
            focusedHandle);

        _events.TryEnqueue(input);
    }

    private void DrainEvents()
    {
        foreach (var input in _events.ReadAll())
        {
            lock (_gate)
            {
                _captured.Add(input);
            }

            try
            {
                _eventObserver?.Invoke(input);
            }
            catch
            {
                // Observers cannot be allowed to terminate the bounded capture drain.
            }
        }
    }
}

public sealed record CorrelatedInputEvent(
    RawInputEvent Input,
    ExternalWindowCandidate? Window,
    ElementDescriptor? TargetElement,
    ElementDescriptor? FocusedElement,
    TargetDescriptor? TargetDescriptor,
    bool BelongsToSelectedTarget,
    string? Failure)
{
    public bool ValuePatternSupported { get; init; }
    public bool TextPatternSupported { get; init; }
    public string? TextSnapshot { get; init; }
}

public sealed class UiAutomationInputCorrelator
{
    private readonly IReadOnlySet<string> _sensitiveNames;

    public UiAutomationInputCorrelator(IReadOnlySet<string>? sensitiveNames = null)
    {
        _sensitiveNames = sensitiveNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwordbox", "passwd", "pwd", "token", "secret", "authorization", "credential"
        };
    }

    public CorrelatedInputEvent Correlate(ExternalTarget target, RawInputEvent input)
    {
        var failures = new List<string>();
        ExternalWindowCandidate? currentWindow = null;
        ElementDescriptor? targetElement = null;
        ElementDescriptor? focusedElement = null;
        TargetDescriptor? descriptor = null;
        var belongs = input.ProcessId == target.Window.ProcessId && input.WindowHandle == target.Window.Handle;

        if (!belongs)
        {
            return new CorrelatedInputEvent(
                input,
                null,
                null,
                null,
                null,
                false,
                "Input belonged to a different window or process.");
        }

        try
        {
            if (input.WindowHandle != IntPtr.Zero)
            {
                var window = AutomationElement.FromHandle(input.WindowHandle);
                currentWindow = SnapshotWindow(input.WindowHandle, window, target.Window.Integrity);

                AutomationElement? focused = null;
                if (input.Kind == RawInputKind.Keyboard)
                {
                    focused = TryGetFocusedElement(window, input.FocusedWindowHandle);
                    if (focused is not null && IsDescendantOf(focused, window))
                    {
                        focusedElement = TargetDescriptorBuilder.BuildElementDescriptor(focused);
                        var valuePatternSupported = focused.TryGetCurrentPattern(ValuePattern.Pattern, out _);
                        var textPatternSupported = focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject)
                            && textPatternObject is TextPattern;
                        string? textSnapshot = null;
                        if (textPatternSupported
                            && !IsSensitive(focusedElement)
                            && textPatternObject is TextPattern textPattern)
                        {
                            textSnapshot = TryReadTextSnapshot(textPattern);
                        }
                        var focusedDescriptor = WithIntegrity(
                            TargetDescriptorBuilder.Build(
                                window,
                                focused,
                                ProcessIdentityPolicy.ProcessName,
                                matchWindowName: false,
                                matchElementName: false,
                                allowTargetWithoutControlAncestor: true),
                            target.Window.Integrity.Level);
                        if (focusedDescriptor.Process.ProcessId == target.Window.ProcessId)
                        {
                            descriptor = focusedDescriptor;
                        }
                        else
                        {
                            failures.Add("The focused UI Automation element belonged to a different process than the selected window.");
                        }

                        var correlated = new CorrelatedInputEvent(
                            input,
                            currentWindow,
                            targetElement,
                            focusedElement,
                            descriptor,
                            belongs,
                            failures.Count == 0 ? null : string.Join(" ", failures));
                        return WithPatternMetadata(correlated, valuePatternSupported, textPatternSupported, textSnapshot);
                    }
                    else
                    {
                        failures.Add("The keyboard event did not have a focused element inside the selected window.");
                    }
                }

                if (input.Kind == RawInputKind.Mouse)
                {
                    var point = AutomationElement.FromPoint(new Point(input.X, input.Y));
                    var actionable = PromoteToActionableElement(point);
                    if (actionable is not null && IsDescendantOf(actionable, window))
                    {
                        targetElement = TargetDescriptorBuilder.BuildElementDescriptor(actionable);
                        var mouseDescriptor = WithIntegrity(
                            TargetDescriptorBuilder.Build(
                                window,
                                actionable,
                                ProcessIdentityPolicy.ProcessName,
                                matchWindowName: false),
                            target.Window.Integrity.Level);
                        if (mouseDescriptor.Process.ProcessId == target.Window.ProcessId)
                        {
                            descriptor = mouseDescriptor;
                        }
                        else
                        {
                            failures.Add("The pointed UI Automation element belonged to a different process than the selected window.");
                        }
                    }
                    else
                    {
                        failures.Add("The mouse point did not resolve to a target inside the selected window.");
                    }
                }
            }
            else
            {
                failures.Add("The input event did not identify a window.");
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            failures.Add($"UI Automation correlation failed: {exception.GetType().Name}.");
        }

        return new CorrelatedInputEvent(input, currentWindow, targetElement, focusedElement, descriptor, belongs, failures.Count == 0 ? null : string.Join(" ", failures));
    }

    private static CorrelatedInputEvent WithPatternMetadata(
        CorrelatedInputEvent correlated,
        bool valuePatternSupported,
        bool textPatternSupported,
        string? textSnapshot)
    {
        return correlated with
        {
            ValuePatternSupported = valuePatternSupported,
            TextPatternSupported = textPatternSupported,
            TextSnapshot = textSnapshot
        };
    }

    private bool IsSensitive(ElementDescriptor element)
    {
        return element.IsPassword == true
            || new[] { element.AutomationId, element.Name, element.ClassName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => _sensitiveNames.Any(name => value!.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? TryReadTextSnapshot(TextPattern pattern)
    {
        try
        {
            return pattern.DocumentRange.GetText(-1);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor)
    {
        var current = element;
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; depth < 64 && current is not null; depth++)
        {
            if (current.Equals(ancestor))
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private static AutomationElement? TryGetFocusedElement(AutomationElement window, IntPtr focusedWindowHandle)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null && IsDescendantOf(focused, window) && !focused.Equals(window))
            {
                return focused;
            }
        }
        catch (ElementNotAvailableException)
        {
            // Fall back to the native focus HWND captured at hook time.
        }

        if (focusedWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var focused = AutomationElement.FromHandle(focusedWindowHandle);
            return focused is not null && IsDescendantOf(focused, window) && !focused.Equals(window)
                ? focused
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static AutomationElement? PromoteToActionableElement(AutomationElement? element)
    {
        var current = element;
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            var automationId = TargetDescriptorBuilder.ReadString(current, AutomationElement.AutomationIdProperty);
            var type = TargetDescriptorBuilder.ReadControlType(current);
            if (!string.IsNullOrWhiteSpace(automationId) || type is "ControlType.Button" or "ControlType.CheckBox" or "ControlType.Edit" or "ControlType.ListItem" or "ControlType.MenuItem")
            {
                return current;
            }

            current = walker.GetParent(current);
        }

        return null;
    }

    private static ExternalWindowCandidate SnapshotWindow(IntPtr handle, AutomationElement element, IntegrityInfo integrity)
    {
        WindowsInterop.GetWindowThreadProcessId(handle, out var processId);
        using var process = Process.GetProcessById(unchecked((int)processId));
        return new ExternalWindowCandidate(
            handle,
            unchecked((int)processId),
            process.ProcessName,
            TryGetExecutablePath(process),
            TargetDescriptorBuilder.ReadString(element, AutomationElement.NameProperty) ?? "(untitled window)",
            TargetDescriptorBuilder.ReadString(element, AutomationElement.AutomationIdProperty),
            TargetDescriptorBuilder.ReadString(element, AutomationElement.ClassNameProperty),
            TargetDescriptorBuilder.ReadString(element, AutomationElement.FrameworkIdProperty),
            TargetDescriptorBuilder.ReadControlType(element),
            integrity);
    }

    private static TargetDescriptor WithIntegrity(TargetDescriptor descriptor, IntegrityLevel integrity)
    {
        return new TargetDescriptor
        {
            Process = new ProcessContext
            {
                ProcessId = descriptor.Process.ProcessId,
                ProcessName = descriptor.Process.ProcessName,
                ExecutablePath = descriptor.Process.ExecutablePath,
                IntegrityLevel = integrity.ToString(),
                IdentityPolicy = descriptor.Process.IdentityPolicy
            },
            Window = descriptor.Window,
            Element = descriptor.Element,
            Ancestors = descriptor.Ancestors
        };
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
