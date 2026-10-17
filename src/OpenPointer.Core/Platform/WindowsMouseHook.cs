using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenPointer.Engine;

namespace OpenPointer.Platform;

/// <summary>
/// Runs the Windows mouse hook on its own message loop; actions run on the engine's worker.
/// Raw input on the same thread tells which physical mouse pressed a button (the hook cannot),
/// and notices when Windows has silently dropped the hook.
/// </summary>
internal sealed class WindowsMouseHook(WindowsMouseRouter router, Action<string, string>? buttonSeen = null) : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nint, string?> _deviceNames = [];
    private HookProc? _callback;
    private nint _hook;
    private nint _window;
    private uint _threadId;
    private int _disposed;
    private long _hookTick, _rawTick, _lastReinstallLog = -60_000;

    /// <summary>True when raw input reports which device pressed each button.</summary>
    public bool ObservesDevices { get; private set; }

    public void Start()
    {
        // A dedicated thread keeps WPF rendering and modal dialogs out of the hook timeout.
        var thread = new Thread(Run) { IsBackground = true, Name = "Windows mouse input" };
        _runningThread = thread;
        thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private Thread? _runningThread;

    public void Cancel(ConnectedMouse? mouse = null) => Post(() => router.Cancel(mouse));

    private void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _commands.Enqueue(action);
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x8000, 0, 0);
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessageW(out _, 0, 0, 0, 0); // Ensure PostThreadMessage has a queue to target.
            _callback = OnMouse;
            _hook = SetWindowsHookExW(14, _callback, GetModuleHandleW(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            ObservesDevices = StartRawInput();
            _ready.TrySetResult();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8000)
                {
                    while (_commands.TryDequeue(out var command)) command();
                }
                else if (message.Id == 0x0113 && message.Window == 0)
                {
                    CheckHook();
                }
                else
                {
                    if (message.Id == 0x00FF) OnRawInput(message.LParam);
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            Log.Error("Windows mouse input", ex);
        }
        finally
        {
            if (_hook != 0) UnhookWindowsHookEx(_hook);
            if (_window != 0) DestroyWindow(_window);
            router.Cancel();
        }
    }

    private bool StartRawInput()
    {
        // A message-only window with INPUTSINK receives mouse input whichever app has focus.
        _window = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, GetModuleHandleW(null), 0);
        if (_window == 0) { Log.Warn($"Raw mouse input unavailable (window error {Marshal.GetLastWin32Error()})"); return false; }
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = _window };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            Log.Warn($"Raw mouse input unavailable (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        SetTimer(0, 0, 2000, 0);
        return true;
    }

    private void OnRawInput(nint handle)
    {
        Span<byte> buffer = stackalloc byte[64];
        var headerSize = (uint)(8 + 2 * nint.Size);
        var size = (uint)buffer.Length;
        if (GetRawInputData(handle, 0x10000003, ref MemoryMarshal.GetReference(buffer), ref size, headerSize) == uint.MaxValue) return;
        if (BitConverter.ToUInt32(buffer) != 0) return; // RIM_TYPEMOUSE
        var device = (nint)(nint.Size == 8 ? BitConverter.ToInt64(buffer[8..]) : BitConverter.ToInt32(buffer[8..]));
        if (device == 0) return; // Injected input has no source device.
        _rawTick = Environment.TickCount64;
        var flags = BitConverter.ToUInt16(buffer[((int)headerSize + 4)..]);
        if ((flags & 0x0150) == 0 || buttonSeen == null) return; // No middle, back or forward press.
        if (!_deviceNames.TryGetValue(device, out var path)) _deviceNames[device] = path = WindowsMouseDiscovery.DeviceName(device);
        if (path == null) return;
        if ((flags & 0x0010) != 0) buttonSeen(path, Config.ButtonNames.Middle);
        if ((flags & 0x0040) != 0) buttonSeen(path, Config.ButtonNames.Back);
        if ((flags & 0x0100) != 0) buttonSeen(path, Config.ButtonNames.Forward);
    }

    private void CheckHook()
    {
        // Windows removes a low-level hook without notice if it ever misses its timeout. When a
        // physical mouse is moving but the hook has heard nothing for a while, install it again.
        if (_rawTick - _hookTick < 1500 || Volatile.Read(ref _disposed) != 0) return;
        var replacement = SetWindowsHookExW(14, _callback!, GetModuleHandleW(null), 0);
        if (replacement == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = replacement;
        _hookTick = Environment.TickCount64;
        if (_hookTick - _lastReinstallLog < 60_000) return;
        _lastReinstallLog = _hookTick;
        Log.Warn("Windows mouse hook stopped receiving input; reinstalled it");
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        _hookTick = Environment.TickCount64;
        if (code >= 0)
        {
            try
            {
                var input = Marshal.PtrToStructure<MouseInput>(data);
                if (router.Process((int)message, input.Data, input.X, input.Y, input.Flags, input.ExtraInfo)) return 1;
            }
            catch (Exception ex)
            {
                Log.Error("Windows mouse event", ex);
                router.Cancel();
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, 0, 0);
        if (_runningThread != Thread.CurrentThread) _runningThread?.Join(TimeSpan.FromSeconds(2));
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, ref byte data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
