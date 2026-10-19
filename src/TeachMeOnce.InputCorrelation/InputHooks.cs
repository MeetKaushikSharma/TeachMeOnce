using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeachMeOnce.InputCorrelation;

internal enum RawInputKind
{
    Mouse,
    Keyboard
}

internal sealed record RawInputEvent(
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
    uint Flags);

internal sealed class InputEventBuffer
{
    private readonly BlockingCollection<RawInputEvent> _events = new(
        new ConcurrentQueue<RawInputEvent>(),
        boundedCapacity: 2048);
    private long _sequence;
    private long _dropped;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(
        RawInputKind kind,
        uint message,
        bool injected,
        int x,
        int y,
        uint virtualKey,
        uint scanCode,
        uint flags)
    {
        var input = new RawInputEvent(
            Sequence: Interlocked.Increment(ref _sequence),
            Kind: kind,
            Message: message,
            TimestampUtc: DateTimeOffset.UtcNow,
            MonotonicTimestamp: Stopwatch.GetTimestamp(),
            Injected: injected,
            X: x,
            Y: y,
            VirtualKey: virtualKey,
            ScanCode: scanCode,
            Flags: flags);

        try
        {
            if (_events.TryAdd(input))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public IEnumerable<RawInputEvent> ReadAll() => _events.GetConsumingEnumerable();

    public void Complete() => _events.CompleteAdding();
}

internal sealed class InputHookHost : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmQuit = 0x0012;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlmhfInjected = 0x00000001;

    private readonly InputEventBuffer _buffer;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private Exception? _startupException;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public InputHookHost(InputEventBuffer buffer)
    {
        _buffer = buffer;
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "TeachMeOnce.InputHooks"
        };
    }

    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The input hook thread did not initialize within five seconds.");
        }

        if (_startupException is not null)
        {
            throw new InvalidOperationException("The input hooks could not be installed.", _startupException);
        }
    }

    public void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The input hook thread did not stop within five seconds.");
        }
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private void RunMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed.");
            }

            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
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
            _buffer.Complete();
            return;
        }

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
            }

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
            }

            _buffer.Complete();
        }
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                _buffer.TryEnqueue(
                    RawInputKind.Keyboard,
                    message,
                    (data.Flags & LlkhfInjected) != 0,
                    0,
                    0,
                    data.VirtualKey,
                    data.ScanCode,
                    data.Flags);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (message is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp
                or WmMButtonDown or WmMButtonUp or WmXButtonDown or WmXButtonUp)
            {
                var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                _buffer.TryEnqueue(
                    RawInputKind.Mouse,
                    message,
                    (data.Flags & LlmhfInjected) != 0,
                    data.Point.X,
                    data.Point.Y,
                    0,
                    0,
                    data.Flags);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public WindowsPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr WindowHandle;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public WindowsPoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr windowHandle, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
