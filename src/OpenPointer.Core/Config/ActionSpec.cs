using System.Text.Json.Serialization;

namespace OpenPointer.Config;

/// <summary>What a button, gesture direction or wheel step does. Serialized with a "type" discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(NativeAction), "native")]
[JsonDerivedType(typeof(NoneAction), "none")]
[JsonDerivedType(typeof(KeysAction), "keys")]
[JsonDerivedType(typeof(MouseAction), "mouse")]
[JsonDerivedType(typeof(ScrollAction), "scroll")]
[JsonDerivedType(typeof(LaunchAction), "launch")]
[JsonDerivedType(typeof(TextAction), "text")]
[JsonDerivedType(typeof(MacroAction), "macro")]
[JsonDerivedType(typeof(GestureAction), "gesture")]
[JsonDerivedType(typeof(ActionRingAction), "ring")]
[JsonDerivedType(typeof(DpiAction), "dpi")]
[JsonDerivedType(typeof(WheelModeAction), "wheelMode")]
[JsonDerivedType(typeof(SwitchHostAction), "switchHost")]
public abstract record ActionSpec
{
    /// <summary>Short human description used by the tray menu and settings UI.</summary>
    public abstract string Describe();
}

/// <summary>Leave the control alone: it is not diverted and the device behaves as shipped.</summary>
public sealed record NativeAction : ActionSpec
{
    public override string Describe() => "Default";
}

/// <summary>Swallow the press.</summary>
public sealed record NoneAction : ActionSpec
{
    public override string Describe() => "Do nothing";
}

/// <summary>A keyboard chord such as "ctrl+shift+t", "win+tab" or "media_play_pause". Held while the button is held.</summary>
public sealed record KeysAction(string Keys) : ActionSpec
{
    /// <summary>
    /// Repeat while held, like a keyboard key. Null picks automatically: on for keys people hold
    /// (volume, arrows, page up/down, delete, zoom), off for one-shot chords like alt+f4.
    /// </summary>
    public bool? Repeat { get; init; }

    [JsonIgnore]
    public bool ShouldRepeat => Repeat ?? KeyChord.IsRepeatable(Keys);

    public override string Describe() => KeyChord.Format(Keys);
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    Back,
    Forward,
}

/// <summary>Emulates a mouse button; held while the source is held, so dragging and autoscroll work.</summary>
public sealed record MouseAction(MouseButtonKind Button) : ActionSpec
{
    public override string Describe() => Button switch
    {
        MouseButtonKind.Back => "Back",
        MouseButtonKind.Forward => "Forward",
        MouseButtonKind.Middle => "Middle click",
        MouseButtonKind.Left => "Left click",
        _ => "Right click",
    };
}

/// <summary>Scrolls by a number of wheel notches (positive = up/right).</summary>
public sealed record ScrollAction(int Amount, bool Horizontal = false) : ActionSpec
{
    /// <summary>Keep scrolling while the button is held.</summary>
    public bool Repeat { get; init; } = true;

    public override string Describe() => Horizontal
        ? Amount > 0 ? "Scroll right" : "Scroll left"
        : Amount > 0 ? "Scroll up" : "Scroll down";
}

/// <summary>Starts a program, opens a file or folder, or opens a URL.</summary>
public sealed record LaunchAction(string Target, string? Arguments = null, string? WorkingDirectory = null) : ActionSpec
{
    /// <summary>Display name, e.g. "WhatsApp" for a Store app whose target is an app ID.</summary>
    public string? Name { get; init; }

    public override string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Name)) return $"Open {Name}";
        var name = Path.GetFileNameWithoutExtension(Target.TrimEnd('\\', '/'));
        return $"Open {(name.Length > 0 ? name : Target)}";
    }
}

/// <summary>Types literal text.</summary>
public sealed record TextAction(string Text) : ActionSpec
{
    public override string Describe() => $"Type \"{(Text.Length > 18 ? Text[..18] + "…" : Text)}\"";
}

/// <summary>A sequence of actions with optional delays between them.</summary>
public sealed record MacroAction(List<MacroStep> Steps, string? Name = null) : ActionSpec
{
    public override string Describe() => Name ?? $"Macro ({Steps.Count} steps)";
}

public sealed record MacroStep(ActionSpec? Action = null, int DelayMs = 0);

/// <summary>
/// Hold the button and move the mouse: a short press runs Tap, a drag runs the matching direction.
/// While held the pointer does not move.
/// </summary>
public sealed record GestureAction : ActionSpec
{
    public ActionSpec? Tap { get; init; }
    public ActionSpec? Up { get; init; }
    public ActionSpec? Down { get; init; }
    public ActionSpec? Left { get; init; }
    public ActionSpec? Right { get; init; }

    /// <summary>Movement (device counts) needed before a drag counts as a direction.</summary>
    public int Threshold { get; init; } = 50;

    public override string Describe() => "Gestures";

    public static GestureAction WindowsDefaults => new()
    {
        Tap = new KeysAction("win+tab"),
        Up = new KeysAction("win+tab"),
        Down = new KeysAction("win+d"),
        Left = new KeysAction("ctrl+win+left"),
        Right = new KeysAction("ctrl+win+right"),
    };
}

/// <summary>
/// Hold the button to show a radial menu at the cursor, move toward an item and release to run it.
/// Releasing without moving cancels.
/// </summary>
public sealed record ActionRingAction : ActionSpec
{
    public List<RingItem> Items { get; init; } = [];

    /// <summary>Dead zone (counts at 1000 DPI) before an item is selected.</summary>
    public int Threshold { get; init; } = 90;

    /// <summary>
    /// The dead zone in use. Never below 90: the mouse shifts a little as the button goes down,
    /// and a smaller dead zone turned that into a selection.
    /// </summary>
    [JsonIgnore]
    public double DeadZone => Math.Max(Threshold, 90);

    /// <summary>Aim is clamped to this radius, so reversing direction responds immediately.</summary>
    public int Reach { get; init; } = 180;

    public override string Describe() => "Actions Ring";

    public static ActionRingAction Defaults => new()
    {
        Items =
        [
            new RingItem("Copy", new KeysAction("ctrl+c")),
            new RingItem("Paste", new KeysAction("ctrl+v")),
            new RingItem("Screenshot", new KeysAction("win+shift+s")),
            new RingItem("Show desktop", new KeysAction("win+d")),
            new RingItem("Undo", new KeysAction("ctrl+z")),
            new RingItem("Task view", new KeysAction("win+tab")),
            new RingItem("Play / pause", new KeysAction("media_play_pause")),
            new RingItem("Mute", new KeysAction("volume_mute")),
        ],
    };

    /// <summary>
    /// Index of the item in the direction of the aim (x, y), or -1 inside the dead zone. Item 0 is
    /// straight up; items go clockwise. With a current selection, the aim must cross a slice border
    /// by a few degrees before the selection changes, so it does not flicker at the edges.
    /// </summary>
    public int Pick(double x, double y, int current = -1)
    {
        if (Items.Count == 0 || Math.Sqrt(x * x + y * y) < DeadZone) return -1;
        var angle = Math.Atan2(x, -y); // 0 = up, clockwise positive (screen y grows downward)
        if (angle < 0) angle += 2 * Math.PI;
        var slice = 2 * Math.PI / Items.Count;
        var index = (int)Math.Floor((angle + slice / 2) / slice) % Items.Count;
        if (current >= 0 && current < Items.Count && index != current)
        {
            var center = current * slice;
            var offset = Math.Abs(Math.IEEERemainder(angle - center, 2 * Math.PI));
            const double hysteresis = 8 * Math.PI / 180;
            if (offset < slice / 2 + hysteresis) return current;
        }

        return index;
    }

    /// <summary>Adds movement to an aim point and clamps it to <see cref="Reach"/>.</summary>
    public (double X, double Y) Aim(double x, double y, double dx, double dy)
    {
        x += dx;
        y += dy;
        var length = Math.Sqrt(x * x + y * y);
        var reach = Math.Max(Reach, DeadZone * 1.5);
        return length > reach ? (x / length * reach, y / length * reach) : (x, y);
    }
}

public sealed record RingItem(string Label, ActionSpec Action);

public enum DpiMode
{
    /// <summary>Switch to Value.</summary>
    Set,
    /// <summary>Step through Values.</summary>
    Cycle,
    /// <summary>Use Value while held, then restore ("sniper" button).</summary>
    Shift,
}

public sealed record DpiAction(DpiMode Mode, int Value = 800, List<int>? Values = null) : ActionSpec
{
    public override string Describe() => Mode switch
    {
        DpiMode.Set => $"DPI {Value}",
        DpiMode.Shift => $"Precision ({Value} DPI)",
        _ => "Cycle DPI",
    };
}

/// <summary>Toggles the scroll wheel between ratchet and free spin (MX SmartShift wheels).</summary>
public sealed record WheelModeAction : ActionSpec
{
    public override string Describe() => "Toggle wheel mode";
}

/// <summary>Moves the device to Easy-Switch channel Host (1-based).</summary>
public sealed record SwitchHostAction(int Host) : ActionSpec
{
    public override string Describe() => $"Switch to computer {Host}";
}
