using System.Runtime.InteropServices;

namespace TeachMeOnce.InputCorrelation;

internal static class WindowsInterop
{
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint KeyEventUnicode = 0x0004;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint ShowNormal = 1;
    internal const uint AncestorRoot = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
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
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }
}
