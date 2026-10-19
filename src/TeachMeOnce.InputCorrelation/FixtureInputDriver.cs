using System.Windows;
using System.Windows.Automation;

namespace TeachMeOnce.InputCorrelation;

internal static class FixtureInputDriver
{
    public static void Run(uint processId)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnMta(processId);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "TeachMeOnce.ScriptedInputDriver"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("The scripted input driver failed.", failure);
        }
    }

    private static void RunOnMta(uint processId)
    {
        var window = WaitForWindow(processId);
        var windowHandleValue = window.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
        if (windowHandleValue is not int windowHandleNumber || windowHandleNumber == 0)
        {
            throw new InvalidOperationException("The fixture window did not expose a native HWND.");
        }

        var windowHandle = new IntPtr(windowHandleNumber);
        ActivateWindow(windowHandle, processId);

        Click(FindElement(window, "InvokeButton"));
        Thread.Sleep(150);

        Click(FindElement(window, "ValueTextBox"));
        Thread.Sleep(150);
        SendUnicodeText("poc02");
        Thread.Sleep(150);

        Click(FindElement(window, "ToggleCheckBox"));
        Thread.Sleep(250);
    }

    private static AutomationElement WaitForWindow(uint processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, (int)processId);
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

    private static AutomationElement FindElement(AutomationElement window, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return window.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException($"Fixture element '{automationId}' was not found.");
    }

    private static void Click(AutomationElement element)
    {
        if (!element.TryGetClickablePoint(out var point))
        {
            throw new InvalidOperationException($"Fixture element '{element.Current.AutomationId}' has no clickable point.");
        }

        WindowsInterop.SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        var inputs = new[]
        {
            new WindowsInterop.NativeInput
            {
                Type = WindowsInterop.InputMouse,
                Mouse = new WindowsInterop.MouseInput { Flags = WindowsInterop.MouseEventLeftDown }
            },
            new WindowsInterop.NativeInput
            {
                Type = WindowsInterop.InputMouse,
                Mouse = new WindowsInterop.MouseInput { Flags = WindowsInterop.MouseEventLeftUp }
            }
        };

        Send(inputs, 2, "mouse click");
    }

    private static void ActivateWindow(IntPtr windowHandle, uint processId)
    {
        WindowsInterop.ShowWindow(windowHandle, (int)WindowsInterop.ShowNormal);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var foreground = WindowsInterop.GetForegroundWindow();
            WindowsInterop.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            if (foregroundProcessId == processId)
            {
                return;
            }

            var foregroundThread = foreground == IntPtr.Zero
                ? 0U
                : WindowsInterop.GetWindowThreadProcessId(foreground, out _);
            var targetThread = WindowsInterop.GetWindowThreadProcessId(windowHandle, out _);
            var currentThread = GetCurrentThreadId();
            var attachedToForeground = foregroundThread != 0
                && foregroundThread != currentThread
                && WindowsInterop.AttachThreadInput(currentThread, foregroundThread, true);
            var attachedToTarget = targetThread != 0
                && targetThread != currentThread
                && WindowsInterop.AttachThreadInput(currentThread, targetThread, true);

            try
            {
                WindowsInterop.BringWindowToTop(windowHandle);
                WindowsInterop.SetActiveWindow(windowHandle);
                WindowsInterop.SetFocus(windowHandle);
                WindowsInterop.SetForegroundWindow(windowHandle);
            }
            finally
            {
                if (attachedToTarget)
                {
                    WindowsInterop.AttachThreadInput(currentThread, targetThread, false);
                }

                if (attachedToForeground)
                {
                    WindowsInterop.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            Thread.Sleep(100);
        }

        var actualForeground = WindowsInterop.GetForegroundWindow();
        WindowsInterop.GetWindowThreadProcessId(actualForeground, out var actualProcessId);
        throw new InvalidOperationException($"Could not activate fixture process {processId}; foreground process is {actualProcessId}.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<WindowsInterop.NativeInput>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(new WindowsInterop.NativeInput
            {
                Type = WindowsInterop.InputKeyboard,
                Keyboard = new WindowsInterop.KeyboardInput
                {
                    ScanCode = character,
                    Flags = WindowsInterop.KeyEventUnicode
                }
            });
            inputs.Add(new WindowsInterop.NativeInput
            {
                Type = WindowsInterop.InputKeyboard,
                Keyboard = new WindowsInterop.KeyboardInput
                {
                    ScanCode = character,
                    Flags = WindowsInterop.KeyEventUnicode | WindowsInterop.KeyEventKeyUp
                }
            });
        }

        Send(inputs.ToArray(), (uint)inputs.Count, "Unicode keyboard input");
    }

    private static void Send(WindowsInterop.NativeInput[] inputs, uint expectedCount, string description)
    {
        var sent = WindowsInterop.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsInterop.NativeInput>());
        if (sent != expectedCount)
        {
            throw new InvalidOperationException($"SendInput sent {sent} of {expectedCount} events for {description}.");
        }
    }
}
