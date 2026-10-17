using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenPointer.Engine;

namespace OpenPointer.Platform;

/// <summary>Runs the Windows mouse hook on its own message loop; actions run on the engine's worker.</summary>
internal sealed class WindowsMouseHook(WindowsMouseRouter router) : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HookProc? _callback;
    private nint _hook;
    private uint _threadId;
    private int _disposed;

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
            _ready.TrySetResult();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8000)
                {
                    while (_commands.TryDequeue(out var command)) command();
                }
                else
                {
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
            router.Cancel();
        }
    }

    private nint OnMouse(int code, nint message, nint data)
    {
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
