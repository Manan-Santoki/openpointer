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
    public override string Describe() => Horizontal
        ? Amount > 0 ? "Scroll right" : "Scroll left"
        : Amount > 0 ? "Scroll up" : "Scroll down";
}

/// <summary>Starts a program, opens a file or folder, or opens a URL.</summary>
public sealed record LaunchAction(string Target, string? Arguments = null, string? WorkingDirectory = null) : ActionSpec
{
    public override string Describe()
    {
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
