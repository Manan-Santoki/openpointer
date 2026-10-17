using OpenPointer.Config;
using OpenPointer.Hidpp;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>Runtime state for one mouse, as shown by the UI.</summary>
public sealed class ConnectedMouse
{
    internal readonly Dictionary<ushort, (bool Divert, bool Raw)> AppliedDivert = [];
    internal readonly SemaphoreSlim ApplyLock = new(1, 1);
    internal (bool Diverted, bool Inverted)? AppliedThumb;
    internal int ThumbAccumulator;
    internal bool LowBatteryNotified;

    public ConnectedMouse(MouseDevice device) => Device = device;

    public ConnectedMouse(WindowsMouseInfo mouse) => WindowsDevice = mouse;

    public MouseDevice? Device { get; }
    public WindowsMouseInfo? WindowsDevice { get; }
    public bool IsStandardMouse => WindowsDevice != null;
    public string Key => Device?.Key ?? WindowsDevice!.DevicePath;
    public string Name => Device?.Name ?? WindowsDevice!.Name;
    public bool Online { get; internal set; } = true;
    public BatteryInfo? Battery { get; internal set; }
    public DpiInfo? Dpi { get; internal set; }
    public SmartShiftState? SmartShift { get; internal set; }
    public HostInfo? Hosts { get; internal set; }
    public bool HasThumbWheel { get; internal set; }
    public int? WheelFlags { get; internal set; }

    /// <summary>Buttons the user can remap, by config name, top of the mouse first (forward sits above back).</summary>
    public IReadOnlyList<string> Buttons =>
        WindowsDevice?.Buttons ?? Device!.Controls.Where(ButtonNames.IsUserFacing)
            .Select(c => ButtonNames.FromCid(c.Cid))
            .OrderBy(name => Array.IndexOf(DisplayOrder, name) is var i && i >= 0 ? i : DisplayOrder.Length)
            .ToList();

    private static readonly string[] DisplayOrder =
        [ButtonNames.Middle, ButtonNames.Forward, ButtonNames.Back, ButtonNames.Gesture, ButtonNames.WheelMode];

    public bool SupportsGestures(string button)
    {
        if (IsStandardMouse) return Buttons.Contains(button);
        var cid = ButtonNames.ToCid(button);
        return Device!.Controls.Any(c => c.Cid == cid && c.SupportsRawXY);
    }

    /// <summary>Hardware actions are offered only when the device exposes the corresponding feature.</summary>
    public bool SupportsAction(ActionSpec action) => action switch
    {
        DpiAction => Dpi != null,
        WheelModeAction => SmartShift != null,
        SwitchHostAction => Hosts is { Count: > 1 },
        GestureAction g => new[] { g.Tap, g.Up, g.Down, g.Left, g.Right }.All(a => a == null || SupportsAction(a)),
        ActionRingAction ring => ring.Items.All(i => SupportsAction(i.Action)),
        MacroAction macro => macro.Steps.All(s => s.Action == null || SupportsAction(s.Action)),
        _ => true,
    };

    public override string ToString() => Name;
}
