using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenPointer.Platform;

/// <summary>One finger on a precision touchpad. X and Y run 0..1 from the top-left corner.</summary>
public readonly record struct TouchContact(int Id, double X, double Y, bool Touching, bool Confident);

/// <summary>Every finger on the pad at one moment.</summary>
public sealed record TouchFrame(string Device, IReadOnlyList<TouchContact> Contacts, bool ButtonDown, long Tick);

/// <summary>
/// Decodes Windows Precision Touchpad input reports (HID page 0x0D, usage 0x05) into contacts.
/// Each finger is its own logical collection holding Contact ID, Tip Switch, Confidence, X and Y.
/// </summary>
internal sealed class TouchpadReportParser
{
    private const int Input = 0, Success = 0x00110000;
    private const ushort Digitizer = 0x0D, GenericDesktop = 0x01, Buttons = 0x09;
    private readonly nint _preparsed;
    private readonly Finger[] _fingers;
    private readonly Axis? _contactCount;
    private readonly List<TouchContact> _pending = [];
    private int _expected;

    private readonly record struct Axis(ushort Collection, int Min, int Max)
    {
        public double Normalize(ulong raw) => Max > Min ? Math.Clamp(((int)raw - Min) / (double)(Max - Min), 0, 1) : 0;
    }

    private readonly record struct Finger(ushort Collection, Axis X, Axis Y);

    /// <summary>Physical pad size from the descriptor, when it reports one.</summary>
    public (double Width, double Height)? SizeMm { get; }

    public TouchpadReportParser(nint preparsed)
    {
        _preparsed = preparsed;
        if (HidP_GetCaps(preparsed, out var caps) != Success) throw new NotSupportedException("Unreadable touchpad descriptor.");
        var count = caps.InputValueCaps;
        var values = new ValueCaps[count];
        if (count > 0 && HidP_GetValueCaps(Input, values, ref count, preparsed) != Success)
            throw new NotSupportedException("Unreadable touchpad values.");
        values = values.Take(count).ToArray();
        Axis? Find(ushort page, ushort usage, ushort? collection = null) =>
            values.Where(v => v.UsagePage == page && v.Usage == usage && (collection == null || v.LinkCollection == collection))
                .Select(v => (Axis?)new Axis(v.LinkCollection, v.LogicalMin, v.LogicalMax)).FirstOrDefault();

        _fingers = values.Where(v => v.UsagePage == Digitizer && v.Usage == 0x51)
            .Select(v => (Collection: v.LinkCollection, X: Find(GenericDesktop, 0x30, v.LinkCollection), Y: Find(GenericDesktop, 0x31, v.LinkCollection)))
            .Where(f => f.X != null && f.Y != null)
            .Select(f => new Finger(f.Collection, f.X!.Value, f.Y!.Value)).ToArray();
        if (_fingers.Length == 0) throw new NotSupportedException("Not a precision touchpad.");
        _contactCount = Find(Digitizer, 0x54);

        var x = values.FirstOrDefault(v => v.UsagePage == GenericDesktop && v.Usage == 0x30 && v.LinkCollection == _fingers[0].Collection);
        var y = values.FirstOrDefault(v => v.UsagePage == GenericDesktop && v.Usage == 0x31 && v.LinkCollection == _fingers[0].Collection);
        if (Millimetres(x) is { } width && Millimetres(y) is { } height) SizeMm = (width, height);
    }

    public int MaxContacts => _fingers.Length;

    private static double? Millimetres(ValueCaps value)
    {
        // HID units: 0x11 is SI linear centimetres, 0x13 English linear inches; the exponent is a signed nibble.
        var exponent = (int)(value.UnitsExp & 0xF);
        if (exponent > 7) exponent -= 16;
        var perUnit = (value.Units & 0xFF) switch { 0x11 => 10.0, 0x13 => 25.4, _ => 0 };
        var span = value.PhysicalMax - value.PhysicalMin;
        return perUnit > 0 && span > 0 ? span * Math.Pow(10, exponent) * perUnit : null;
    }

    /// <summary>
    /// Feeds one input report. Returns a complete frame, or null while a frame is split across
    /// several reports (hybrid mode: the first carries the contact count, the rest report zero).
    /// </summary>
    public (List<TouchContact> Contacts, bool ButtonDown)? Parse(byte[] report)
    {
        var count = _contactCount is { } c && Value(Digitizer, c.Collection, 0x54, report) is { } total ? (int)total : _fingers.Length;
        if (count > 0) { _pending.Clear(); _expected = count; }
        var usages = new ushort[16];
        foreach (var finger in _fingers)
        {
            if (_pending.Count >= _expected) break;
            if (Value(Digitizer, finger.Collection, 0x51, report) is not { } id) continue;
            var length = (uint)usages.Length;
            var tip = false;
            var confident = false;
            if (HidP_GetUsages(Input, Digitizer, finger.Collection, usages, ref length, _preparsed, report, (uint)report.Length) == Success)
            {
                tip = usages.Take((int)length).Contains((ushort)0x42);
                confident = usages.Take((int)length).Contains((ushort)0x47);
            }
            var x = Value(GenericDesktop, finger.Collection, 0x30, report);
            var y = Value(GenericDesktop, finger.Collection, 0x31, report);
            if (x == null || y == null) continue;
            _pending.Add(new TouchContact((int)id, finger.X.Normalize(x.Value), finger.Y.Normalize(y.Value), tip, confident));
        }
        if (_pending.Count < _expected) return null;

        var buttons = new ushort[4];
        var pressed = (uint)buttons.Length;
        var down = HidP_GetUsages(Input, Buttons, 0, buttons, ref pressed, _preparsed, report, (uint)report.Length) == Success &&
            buttons.Take((int)pressed).Contains((ushort)1);
        return ([.. _pending], down);
    }

    private ulong? Value(ushort page, ushort collection, ushort usage, byte[] report) =>
        HidP_GetUsageValue(Input, page, collection, usage, out var value, _preparsed, report, (uint)report.Length) == Success ? value : null;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps { [FieldOffset(48)] public ushort InputValueCaps; }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct ValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort Usage;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int type, [Out] ValueCaps[] caps, ref ushort count, nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int type, ushort page, ushort collection, ushort usage, out ulong value, nint data, byte[] report, uint length);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int type, ushort page, ushort collection, [Out] ushort[] usages, ref uint length, nint data, byte[] report, uint length2);
}

/// <summary>
/// Receives precision touchpad contacts through raw input on its own message loop. Raw input only
/// observes: Windows still moves the pointer and runs its own gestures.
/// </summary>
public sealed class TouchpadMonitor : IDisposable
{
    private readonly Dictionary<nint, (string Path, TouchpadReportParser? Parser, nint Preparsed)> _devices = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Raised on the monitor thread for every complete frame.</summary>
    public event Action<TouchFrame>? Frame;

    /// <summary>Raised once per touchpad when its first report arrives.</summary>
    public event Action<string, int, (double Width, double Height)?>? DeviceFound;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Touchpad input" };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        nint window = 0;
        try
        {
            _threadId = GetCurrentThreadId();
            window = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, GetModuleHandleW(null), 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var device = new RawInputDevice { UsagePage = 0x0D, Usage = 0x05, Flags = 0x100, Target = window };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x00FF)
                {
                    try { OnInput(message.LParam); }
                    catch (Exception ex) { Log.Error("Touchpad input", ex); }
                }
                DispatchMessageW(ref message);
            }
        }
        catch (Exception ex) { _ready.TrySetException(ex); }
        finally
        {
            if (window != 0) DestroyWindow(window);
            foreach (var (_, _, preparsed) in _devices.Values) if (preparsed != 0) Marshal.FreeHGlobal(preparsed);
        }
    }

    private void OnInput(nint handle)
    {
        var headerSize = (uint)(8 + 2 * nint.Size);
        uint size = 0;
        GetRawInputData(handle, 0x10000003, null, ref size, headerSize);
        if (size == 0) return;
        var buffer = new byte[size];
        if (GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue) return;
        if (BitConverter.ToUInt32(buffer, 0) != 2) return; // RIM_TYPEHID
        var source = (nint)(nint.Size == 8 ? BitConverter.ToInt64(buffer, 8) : BitConverter.ToInt32(buffer, 8));
        var reportSize = (int)BitConverter.ToUInt32(buffer, (int)headerSize);
        var reports = (int)BitConverter.ToUInt32(buffer, (int)headerSize + 4);
        if (!_devices.TryGetValue(source, out var device)) _devices[source] = device = Open(source);
        if (device.Parser == null) return;
        for (var i = 0; i < reports; i++)
        {
            var report = buffer.AsSpan((int)headerSize + 8 + i * reportSize, reportSize).ToArray();
            if (device.Parser.Parse(report) is { } frame)
                Frame?.Invoke(new TouchFrame(device.Path, frame.Contacts, frame.ButtonDown, Environment.TickCount64));
        }
    }

    private (string, TouchpadReportParser?, nint) Open(nint source)
    {
        var path = WindowsMouseDiscovery.DeviceName(source) ?? $"touchpad {source:X}";
        uint size = 0;
        if (GetPreparsedData(source, 0x20000005, 0, ref size) == uint.MaxValue || size == 0) return (path, null, 0);
        var data = Marshal.AllocHGlobal((int)size);
        if (GetPreparsedData(source, 0x20000005, data, ref size) == uint.MaxValue) return (path, null, data);
        try
        {
            var parser = new TouchpadReportParser(data);
            DeviceFound?.Invoke(path, parser.MaxContacts, parser.SizeMm);
            return (path, parser, data);
        }
        catch (NotSupportedException ex)
        {
            Log.Warn($"Ignoring touchpad {path}: {ex.Message}");
            return (path, null, data);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, 0, 0);
        if (_thread != Thread.CurrentThread) _thread?.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, [Out] byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetPreparsedData(nint device, uint command, nint buffer, ref uint size);
    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
