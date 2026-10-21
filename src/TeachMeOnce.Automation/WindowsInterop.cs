using System.Runtime.InteropServices;

namespace TeachMeOnce.Automation;

internal static class WindowsInterop
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmQuit = 0x0012;
    internal const uint LlkhfInjected = 0x00000010;
    internal const uint LlmhfInjected = 0x00000001;
    internal const uint AncestorRoot = 2;
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventUnicode = 0x0004;
    internal const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo threadInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct NativeInput
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }

    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
}
