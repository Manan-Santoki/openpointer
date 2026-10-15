using System.Text;
using OpenPointer.Config;
using static OpenPointer.Platform.NativeMethods;

namespace OpenPointer.Platform;

/// <summary>
/// Follows the foreground window. Must be started on a thread with a message loop (the UI thread).
/// Tracks the last "real" app separately so the tray menu can target the app the user came from.
/// </summary>
public sealed class FocusTracker : IDisposable
{
    private readonly WinEventDelegate _callback;
    private nint _hook;

    public FocusTracker()
    {
        _callback = OnWinEvent;
    }

    public FocusContext Current { get; private set; } = FocusContext.Unknown;

    /// <summary>The most recent foreground context that was not our own window or the taskbar/tray.</summary>
    public FocusContext LastApp { get; private set; } = FocusContext.Unknown;

    public event Action<FocusContext>? Changed;

    public void Start()
    {
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Update(GetForegroundWindow());
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time) => Update(hwnd);

    private void Update(nint hwnd)
    {
        var context = Describe(hwnd);
        if (context == Current) return;
        Current = context;
        if (!context.IsTaskbar && context.ProcessName.Length > 0) LastApp = context;
        Changed?.Invoke(context);
    }

    /// <summary>Reads the foreground window right now (used at button-press time).</summary>
    public static FocusContext Snapshot() => Describe(GetForegroundWindow());

    public static FocusContext Describe(nint hwnd)
    {
        if (hwnd == 0) return FocusContext.Unknown;
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        return new FocusContext(ProcessName(pid), className.ToString(), title.ToString());
    }

    private static string ProcessName(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileName(buffer.ToString()).ToLowerInvariant()
                : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
    }
}
