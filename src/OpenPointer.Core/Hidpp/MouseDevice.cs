using System.Buffers.Binary;
using System.Text;

namespace OpenPointer.Hidpp;

public enum WheelRatchetMode
{
    Freespin = 1,
    Ratchet = 2,
}

public sealed record BatteryInfo(int Percent, bool Charging, string Status);

public sealed record SmartShiftState(WheelRatchetMode Mode, int AutoDisengage, int DefaultAutoDisengage)
{
    /// <summary>The device treats an auto-disengage value of 0xFF as "always ratchet" (SmartShift off).</summary>
    public bool Enabled => AutoDisengage is > 0 and < 0xFF;
}

public sealed record DpiInfo(int Current, int Default, IReadOnlyList<int> Supported);

public sealed record HostInfo(int Count, int Current);

[Flags]
public enum ControlFlags
{
    Mouse = 0x01,
    FKey = 0x02,
    Hotkey = 0x04,
    FnToggle = 0x08,
    Reprogrammable = 0x10,
    Divertable = 0x20,
    PersistentlyDivertable = 0x40,
    Virtual = 0x80,
    RawXY = 0x100,
    ForceRawXY = 0x200,
    Analytics = 0x400,
    RawWheel = 0x800,
}

/// <summary>A reprogrammable control reported by feature 0x1B04.</summary>
public sealed record ButtonControl(ushort Cid, ushort TaskId, ControlFlags Flags, int Position, int Group, int GroupMask)
{
    public bool IsDivertable => Flags.HasFlag(ControlFlags.Divertable);
    public bool SupportsRawXY => Flags.HasFlag(ControlFlags.RawXY);
}

public enum ThumbWheelPhase
{
    Inactive = 0,
    Start = 1,
    Active = 2,
    Stop = 3,
}

/// <summary>
/// High-level access to a HID++ 2.0 mouse: buttons (0x1B04), DPI (0x2201), SmartShift (0x2110),
/// wheels (0x2121/0x2150), battery (0x1000/0x1004) and Easy-Switch (0x1814).
/// </summary>
public sealed class MouseDevice
{
    private HashSet<ushort> _pressed = [];

    public MouseDevice(HidppDevice device)
    {
        Device = device;
        device.FeatureNotification += OnFeatureNotification;
    }

    public HidppDevice Device { get; }
    public string Name { get; private set; } = "Unknown device";
    public int DeviceType { get; private set; } = -1;
    public Version? Protocol { get; private set; }
    public IReadOnlyList<ButtonControl> Controls { get; private set; } = [];

    /// <summary>Stable identity for settings: receiver PID, slot and model name.</summary>
    public string Key => $"{Device.Transport.ProductId:X4}:{Device.DeviceIndex:X2}:{Name}";

    public bool IsMouseLike => DeviceType is 3 or 4 or 5 || Controls.Any(c => c.Cid is 0x50 or 0x51);

    /// <summary>A diverted control went down.</summary>
    public event Action<ushort>? ButtonDown;

    /// <summary>A diverted control went up.</summary>
    public event Action<ushort>? ButtonUp;

    /// <summary>Raw pointer movement while a control diverted with raw XY is held.</summary>
    public event Action<int, int>? RawMove;

    /// <summary>Thumb wheel rotation while the thumb wheel is diverted.</summary>
    public event Action<int, ThumbWheelPhase>? ThumbWheel;

    /// <summary>Wheel rotation while the high-resolution wheel is diverted.</summary>
    public event Action<int>? Wheel;

    /// <summary>Reads identity and the control list. Call again after the device reconnects.</summary>
    public async Task InitializeAsync()
    {
        Protocol = await Device.PingAsync().ConfigureAwait(false);
        Name = await ReadNameAsync().ConfigureAwait(false) ?? Name;
        if (await Device.HasFeatureAsync(FeatureId.DeviceNameType).ConfigureAwait(false))
        {
            try { DeviceType = (await Device.CallAsync(FeatureId.DeviceNameType, 2).ConfigureAwait(false))[0]; }
            catch (HidppException) { }
        }

        Controls = await ReadControlsAsync().ConfigureAwait(false);
        _pressed = [];

        // Make sure every feature that sends notifications is resolved so events can be routed.
        foreach (var id in new[] { FeatureId.ReprogControlsV4, FeatureId.ThumbWheel, FeatureId.HiResWheel, FeatureId.BatteryStatus, FeatureId.UnifiedBattery })
            await Device.GetFeatureIndexAsync(id).ConfigureAwait(false);
    }

    private async Task<string?> ReadNameAsync()
    {
        if (!await Device.HasFeatureAsync(FeatureId.DeviceNameType).ConfigureAwait(false)) return null;
        var length = (await Device.CallAsync(FeatureId.DeviceNameType, 0).ConfigureAwait(false))[0];
        var bytes = new List<byte>();
        while (bytes.Count < length)
        {
            var chunk = await Device.CallAsync(FeatureId.DeviceNameType, 1, (byte)bytes.Count).ConfigureAwait(false);
            var take = Math.Min(chunk.Length, length - bytes.Count);
            if (take <= 0) break;
            bytes.AddRange(chunk.Take(take));
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
    }

    private async Task<IReadOnlyList<ButtonControl>> ReadControlsAsync()
    {
        if (!await Device.HasFeatureAsync(FeatureId.ReprogControlsV4).ConfigureAwait(false)) return [];
        var count = (await Device.CallAsync(FeatureId.ReprogControlsV4, 0).ConfigureAwait(false))[0];
        var list = new List<ButtonControl>(count);
        for (var i = 0; i < count; i++)
        {
            var p = await Device.CallAsync(FeatureId.ReprogControlsV4, 1, (byte)i).ConfigureAwait(false);
            var flags = (ControlFlags)(p[4] | (p[8] << 8));
            list.Add(new ButtonControl(
                BinaryPrimitives.ReadUInt16BigEndian(p),
                BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)),
                flags, p[5], p[6], p[7]));
        }

        return list;
    }

    /// <summary>
    /// Routes a control to OpenPointer (diverted) or back to its native behavior. Diversion is
    /// temporary: the device forgets it on power cycle or reconnect, so it fails safe.
    /// </summary>
    public async Task SetDivertAsync(ushort cid, bool divert, bool rawXY = false)
    {
        var control = Controls.FirstOrDefault(c => c.Cid == cid);
        if (control == null || !control.IsDivertable) return;
        byte flags = 0x02; // divert valid
        if (divert) flags |= 0x01;
        if (control.SupportsRawXY)
        {
            flags |= 0x20; // raw XY valid
            if (divert && rawXY) flags |= 0x10;
        }

        await Device.CallAsync(FeatureId.ReprogControlsV4, 3, (byte)(cid >> 8), (byte)cid, flags, 0, 0).ConfigureAwait(false);
    }

    public async Task<(bool Diverted, bool RawXY)> GetDivertAsync(ushort cid)
    {
        var p = await Device.CallAsync(FeatureId.ReprogControlsV4, 2, (byte)(cid >> 8), (byte)cid).ConfigureAwait(false);
        return ((p[2] & 0x01) != 0, (p[2] & 0x10) != 0);
    }

    public async Task<BatteryInfo?> GetBatteryAsync()
    {
        if (await Device.HasFeatureAsync(FeatureId.UnifiedBattery).ConfigureAwait(false))
        {
            var p = await Device.CallAsync(FeatureId.UnifiedBattery, 1).ConfigureAwait(false);
            var percent = p[0] != 0 ? p[0] : LevelToPercent(p[1]);
            var status = p[2] switch { 0 => "Discharging", 1 => "Charging", 2 => "Charging slowly", 3 => "Full", _ => "Error" };
            return new BatteryInfo(percent, p[2] is 1 or 2, status);
        }

        if (await Device.HasFeatureAsync(FeatureId.BatteryStatus).ConfigureAwait(false))
        {
            var p = await Device.CallAsync(FeatureId.BatteryStatus, 0).ConfigureAwait(false);
            var status = p[2] switch
            {
                0 => "Discharging",
                1 => "Charging",
                2 => "Almost full",
                3 => "Full",
                4 => "Charging slowly",
                5 => "Invalid battery",
                6 => "Thermal error",
                _ => "Unknown",
            };
            return new BatteryInfo(p[0], p[2] is 1 or 2 or 4, status);
        }

        return null;
    }

    private static int LevelToPercent(byte levelFlags) => levelFlags switch
    {
        _ when (levelFlags & 0x08) != 0 => 100,
        _ when (levelFlags & 0x04) != 0 => 50,
        _ when (levelFlags & 0x02) != 0 => 20,
        _ when (levelFlags & 0x01) != 0 => 5,
        _ => 0,
    };

    public async Task<DpiInfo?> GetDpiAsync(int sensor = 0)
    {
        if (!await Device.HasFeatureAsync(FeatureId.AdjustableDpi).ConfigureAwait(false)) return null;
        var supported = new List<int>();
        int? step = null;
        var done = false;
        for (var page = 0; page < 8 && !done; page++)
        {
            // The list continues across calls when a sensor has many discrete values.
            var p = await Device.CallAsync(FeatureId.AdjustableDpi, 1, (byte)sensor, (byte)page).ConfigureAwait(false);
            for (var i = 1; i + 1 < p.Length; i += 2)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i));
                if (value == 0)
                {
                    done = true;
                    break;
                }

                if (value >> 13 == 0b111)
                {
                    step = value & 0x1FFF;
                    continue;
                }

                if (step is > 0 && supported.Count > 0)
                {
                    for (var dpi = supported[^1] + step.Value; dpi < value; dpi += step.Value) supported.Add(dpi);
                    step = null;
                }

                supported.Add(value);
            }

            if (p.Length < 16) break;
        }

        var current = await Device.CallAsync(FeatureId.AdjustableDpi, 2, (byte)sensor).ConfigureAwait(false);
        var now = BinaryPrimitives.ReadUInt16BigEndian(current.AsSpan(1));
        var def = BinaryPrimitives.ReadUInt16BigEndian(current.AsSpan(3));
        return new DpiInfo(now, def == 0 ? now : def, supported);
    }

    public async Task SetDpiAsync(int dpi, int sensor = 0)
    {
        await Device.CallAsync(FeatureId.AdjustableDpi, 3, (byte)sensor, (byte)(dpi >> 8), (byte)dpi).ConfigureAwait(false);
    }

    public async Task<SmartShiftState?> GetSmartShiftAsync()
    {
        if (await Device.HasFeatureAsync(FeatureId.SmartShift).ConfigureAwait(false))
        {
            var p = await Device.CallAsync(FeatureId.SmartShift, 0).ConfigureAwait(false);
            return new SmartShiftState((WheelRatchetMode)p[0], p[1], p[2]);
        }

        if (await Device.HasFeatureAsync(FeatureId.SmartShiftEnhanced).ConfigureAwait(false))
        {
            var p = await Device.CallAsync(FeatureId.SmartShiftEnhanced, 1).ConfigureAwait(false);
            return new SmartShiftState((WheelRatchetMode)p[0], p[1], p[2]);
        }

        return null;
    }

    /// <summary>Sets wheel mode and/or the SmartShift threshold. Pass null to leave a value unchanged.</summary>
    public async Task SetSmartShiftAsync(WheelRatchetMode? mode, int? autoDisengage)
    {
        byte modeByte = mode.HasValue ? (byte)mode.Value : (byte)0;
        byte threshold = autoDisengage.HasValue ? (byte)Math.Clamp(autoDisengage.Value, 1, 0xFF) : (byte)0;
        if (await Device.HasFeatureAsync(FeatureId.SmartShift).ConfigureAwait(false))
            await Device.CallAsync(FeatureId.SmartShift, 1, modeByte, threshold, 0).ConfigureAwait(false);
        else if (await Device.HasFeatureAsync(FeatureId.SmartShiftEnhanced).ConfigureAwait(false))
            await Device.CallAsync(FeatureId.SmartShiftEnhanced, 2, modeByte, threshold, 0).ConfigureAwait(false);
    }

    /// <summary>Wheel flags: 0x01 divert to HID++, 0x02 high resolution, 0x04 inverted.</summary>
    public async Task<int?> GetWheelModeAsync()
    {
        if (!await Device.HasFeatureAsync(FeatureId.HiResWheel).ConfigureAwait(false)) return null;
        return (await Device.CallAsync(FeatureId.HiResWheel, 1).ConfigureAwait(false))[0];
    }

    public async Task SetWheelModeAsync(int flags)
    {
        await Device.CallAsync(FeatureId.HiResWheel, 2, (byte)(flags & 0x07)).ConfigureAwait(false);
    }

    public async Task<(bool Diverted, bool Inverted)?> GetThumbWheelAsync()
    {
        if (!await Device.HasFeatureAsync(FeatureId.ThumbWheel).ConfigureAwait(false)) return null;
        var p = await Device.CallAsync(FeatureId.ThumbWheel, 1).ConfigureAwait(false);
        return ((p[0] & 0x01) != 0, (p[1] & 0x01) != 0);
    }

    public async Task SetThumbWheelAsync(bool diverted, bool inverted)
    {
        if (!await Device.HasFeatureAsync(FeatureId.ThumbWheel).ConfigureAwait(false)) return;
        await Device.CallAsync(FeatureId.ThumbWheel, 2, diverted ? (byte)1 : (byte)0, inverted ? (byte)1 : (byte)0).ConfigureAwait(false);
    }

    public async Task<HostInfo?> GetHostsAsync()
    {
        if (!await Device.HasFeatureAsync(FeatureId.ChangeHost).ConfigureAwait(false)) return null;
        var p = await Device.CallAsync(FeatureId.ChangeHost, 0).ConfigureAwait(false);
        return new HostInfo(p[0], p[1]);
    }

    /// <summary>Moves the device to another Easy-Switch host (0-based). The device disconnects from this PC.</summary>
    public async Task SwitchHostAsync(int host)
    {
        var index = await Device.GetFeatureIndexAsync(FeatureId.ChangeHost).ConfigureAwait(false)
                    ?? throw new HidppException("Device does not support Easy-Switch.");
        // The device leaves immediately and never replies, so fire and forget.
        Device.Transport.Write(HidppMessage.Long(Device.DeviceIndex, index, 1, HidppDevice.SoftwareId, [(byte)host]));
    }

    private void OnFeatureNotification(FeatureEvent e)
    {
        switch (e.FeatureId)
        {
            case FeatureId.ReprogControlsV4 when e.EventId == 0:
                HandleDivertedButtons(e.Params);
                break;
            case FeatureId.ReprogControlsV4 when e.EventId == 1:
                RawMove?.Invoke(BinaryPrimitives.ReadInt16BigEndian(e.Params), BinaryPrimitives.ReadInt16BigEndian(e.Params.AsSpan(2)));
                break;
            case FeatureId.ThumbWheel when e.EventId == 0:
                ThumbWheel?.Invoke(BinaryPrimitives.ReadInt16BigEndian(e.Params), (ThumbWheelPhase)(e.Params[4] & 0x03));
                break;
            case FeatureId.HiResWheel when e.EventId == 0:
                Wheel?.Invoke(BinaryPrimitives.ReadInt16BigEndian(e.Params.AsSpan(1)));
                break;
        }
    }

    private void HandleDivertedButtons(byte[] p)
    {
        // Up to four currently-pressed diverted controls; the event fires on every change.
        var now = new HashSet<ushort>();
        for (var i = 0; i + 1 < 8 && i + 1 < p.Length; i += 2)
        {
            var cid = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i));
            if (cid != 0) now.Add(cid);
        }

        var before = _pressed;
        _pressed = now;
        foreach (var cid in before.Where(c => !now.Contains(c))) ButtonUp?.Invoke(cid);
        foreach (var cid in now.Where(c => !before.Contains(c))) ButtonDown?.Invoke(cid);
    }

    /// <summary>Releases anything we believe is held, e.g. when the device disconnects mid-press.</summary>
    public void ReleaseAll()
    {
        var before = _pressed;
        _pressed = [];
        foreach (var cid in before) ButtonUp?.Invoke(cid);
    }
}
