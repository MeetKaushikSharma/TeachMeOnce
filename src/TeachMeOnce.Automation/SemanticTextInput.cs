using System.Runtime.InteropServices;
using System.Windows.Automation;
using TeachMeOnce.TargetResolution;

namespace TeachMeOnce.Automation;

public static class SemanticTextInput
{
    public static bool TrySetText(AutomationElement element, string text, out string reason)
    {
        if (string.IsNullOrEmpty(text))
        {
            reason = "Text input was empty.";
            return false;
        }

        try
        {
            var targetWindow = FindNativeWindow(element);
            if (targetWindow == IntPtr.Zero)
            {
                reason = "Semantic text input could not identify the target's native window.";
                return false;
            }

            if (!WindowsInterop.IsWindowVisible(targetWindow))
            {
                reason = "Semantic text input refused because the target window is not visible.";
                return false;
            }

            WindowsInterop.GetWindowThreadProcessId(targetWindow, out var targetProcessId);
            var elementProcessId = TargetDescriptorBuilder.ReadInt(element, AutomationElement.ProcessIdProperty);
            if (elementProcessId is null || targetProcessId != elementProcessId.Value)
            {
                reason = "Semantic text input refused because the native window process did not match the UI Automation target.";
                return false;
            }

            if (!WindowsInterop.SetForegroundWindow(targetWindow))
            {
                reason = "Semantic text input could not bring the resolved target window to the foreground.";
                return false;
            }

            element.SetFocus();
            var inputs = new List<WindowsInterop.NativeInput>();
            AddKey(inputs, 0x11, keyUp: false, unicode: false);
            AddKey(inputs, 0x41, keyUp: false, unicode: false);
            AddKey(inputs, 0x41, keyUp: true, unicode: false);
            AddKey(inputs, 0x11, keyUp: true, unicode: false);

            foreach (var character in text)
            {
                AddKey(inputs, character, keyUp: false, unicode: true);
                AddKey(inputs, character, keyUp: true, unicode: true);
            }

            var sent = WindowsInterop.SendInput(
                (uint)inputs.Count,
                inputs.ToArray(),
                Marshal.SizeOf<WindowsInterop.NativeInput>());
            if (sent != inputs.Count)
            {
                reason = $"Windows accepted {sent} of {inputs.Count} text-input events.";
                return false;
            }

            reason = "Semantic text input was sent to the focused target.";
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            reason = $"Semantic text input failed: {exception.GetType().Name}.";
            return false;
        }
    }

    private static void AddKey(
        ICollection<WindowsInterop.NativeInput> inputs,
        ushort key,
        bool keyUp,
        bool unicode)
    {
        inputs.Add(new WindowsInterop.NativeInput
        {
            Type = WindowsInterop.InputKeyboard,
            Keyboard = new WindowsInterop.KeyboardInput
            {
                VirtualKey = unicode ? (ushort)0 : key,
                ScanCode = unicode ? key : (ushort)0,
                Flags = (unicode ? WindowsInterop.KeyEventUnicode : 0)
                    | (keyUp ? WindowsInterop.KeyEventKeyUp : 0)
            }
        });
    }

    private static IntPtr FindNativeWindow(AutomationElement element)
    {
        var current = element;
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; depth < 64 && current is not null; depth++)
        {
            var handle = TargetDescriptorBuilder.ReadInt(current, AutomationElement.NativeWindowHandleProperty);
            if (handle is not null and not 0)
            {
                return new IntPtr(handle.Value);
            }

            current = walker.GetParent(current);
        }

        return IntPtr.Zero;
    }
}
