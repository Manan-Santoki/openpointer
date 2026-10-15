namespace OpenPointer.Config;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();

    /// <summary>Device settings keyed by device name (e.g. "Signature M650 L"); "*" applies to every device.</summary>
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new() { ["*"] = new DeviceSettings() };

    /// <summary>The first profile is the fallback; the rest override it for matching apps.</summary>
    public List<Profile> Profiles { get; set; } = [];

    public Profile DefaultProfile
    {
        get
        {
            if (Profiles.Count == 0) Profiles.Add(new Profile { Name = "Default" });
            return Profiles[0];
        }
    }

    public DeviceSettings SettingsFor(string deviceName)
    {
        var global = Devices.TryGetValue("*", out var g) ? g : new DeviceSettings();
        return Devices.TryGetValue(deviceName, out var specific) ? global.Merge(specific) : global;
    }

    public static AppConfig CreateDefault() => new()
    {
        Profiles =
        [
            new Profile
            {
                Name = "Default",
                Buttons = new Dictionary<string, ActionSpec>
                {
                    [ButtonNames.Middle] = new NativeAction(),
                    [ButtonNames.Back] = new NativeAction(),
                    [ButtonNames.Forward] = new NativeAction(),
                    [ButtonNames.Gesture] = GestureAction.WindowsDefaults,
                },
            },
            new Profile
            {
                Name = "Desktop",
                Match = [ContextMatcher.Desktop],
                Buttons = new Dictionary<string, ActionSpec>
                {
                    [ButtonNames.Forward] = new LaunchAction("explorer.exe"),
                    [ButtonNames.Back] = new LaunchAction("ms-settings:"),
                },
            },
        ],
    };
}

public sealed class GeneralSettings
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Warn when Logi Options+ is running, since it competes for the same controls.</summary>
    public bool WarnAboutOptionsPlus { get; set; } = true;
}

/// <summary>Hardware settings. Null means "leave whatever the device has".</summary>
public sealed class DeviceSettings
{
    public int? Dpi { get; set; }

    /// <summary>DPI values offered by the tray menu and the cycle action.</summary>
    public List<int>? DpiPresets { get; set; }

    public bool? SmartShiftEnabled { get; set; }

    /// <summary>SmartShift sensitivity, 1 (disengages easily) to 50.</summary>
    public int? SmartShiftThreshold { get; set; }

    public bool? InvertScroll { get; set; }
    public bool? InvertThumbWheel { get; set; }

    public DeviceSettings Merge(DeviceSettings over) => new()
    {
        Dpi = over.Dpi ?? Dpi,
        DpiPresets = over.DpiPresets ?? DpiPresets,
        SmartShiftEnabled = over.SmartShiftEnabled ?? SmartShiftEnabled,
        SmartShiftThreshold = over.SmartShiftThreshold ?? SmartShiftThreshold,
        InvertScroll = over.InvertScroll ?? InvertScroll,
        InvertThumbWheel = over.InvertThumbWheel ?? InvertThumbWheel,
    };
}

public sealed class Profile
{
    public string Name { get; set; } = "Profile";

    /// <summary>Process names ("chrome.exe") or special contexts ("@desktop", "@taskbar").</summary>
    public List<string> Match { get; set; } = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Executable the profile was created from, used for its icon.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Button name (see <see cref="ButtonNames"/>) to action. Missing buttons fall back to the Default profile.</summary>
    public Dictionary<string, ActionSpec> Buttons { get; set; } = [];

    /// <summary>Thumb wheel actions per step. Null falls back; both null means native horizontal scroll.</summary>
    public ActionSpec? ThumbWheelLeft { get; set; }
    public ActionSpec? ThumbWheelRight { get; set; }

    /// <summary>Optional DPI while this profile is active.</summary>
    public int? Dpi { get; set; }
}

/// <summary>Friendly names for HID++ control IDs. Unknown controls are addressed as "cid:00D7".</summary>
public static class ButtonNames
{
    public const string Middle = "middle";
    public const string Back = "back";
    public const string Forward = "forward";
    public const string Gesture = "gesture";
    public const string WheelMode = "wheelMode";

    private static readonly Dictionary<string, ushort> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [Middle] = 0x52,
        [Back] = 0x53,
        [Forward] = 0x56,
        [Gesture] = 0xC3,
        [WheelMode] = 0xC4,
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [Middle] = "Middle button",
        [Back] = "Back button",
        [Forward] = "Forward button",
        [Gesture] = "Gesture button",
        [WheelMode] = "Wheel mode button",
    };

    public static ushort? ToCid(string name)
    {
        if (ByName.TryGetValue(name, out var cid)) return cid;
        if (name.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out var raw))
            return raw;
        return null;
    }

    public static string FromCid(ushort cid) =>
        ByName.FirstOrDefault(kv => kv.Value == cid).Key ?? $"cid:{cid:X4}";

    public static string Label(string name) => Labels.TryGetValue(name, out var label) ? label : $"Control {name[4..]}";

    /// <summary>Controls worth showing to users: physical, divertable, not left/right click.</summary>
    public static bool IsUserFacing(Hidpp.ButtonControl control) =>
        control.IsDivertable && !control.Flags.HasFlag(Hidpp.ControlFlags.Virtual) && control.Cid is not (0x50 or 0x51);
}
