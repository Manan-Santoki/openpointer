using Microsoft.Win32;

namespace OpenPointer.Config;

/// <summary>A touchpad gesture slot. Its <see cref="Key"/> is stored in <see cref="Profile.Buttons"/> like a button.</summary>
public sealed record TouchpadGesture(string Key, string Group, string Label, string Hint, bool Repeats = false);

/// <summary>Every touchpad gesture OpenPointer recognizes, grouped as the Touchpad page shows them.</summary>
public static class TouchpadGestures
{
    public const string Prefix = "touchpad:";
    public const string Corners = "Corners", EdgeSlides = "Slide along an edge", EdgeSwipes = "Swipe in from an edge",
        Circles = "Draw a circle", ThreeFingers = "Three fingers", FourFingers = "Four fingers", FiveFingers = "Five fingers";

    public static readonly IReadOnlyList<TouchpadGesture> All =
    [
        new(Key("corner-top-left"), Corners, "Top-left corner", "One finger"),
        new(Key("corner-top-right"), Corners, "Top-right corner", "One finger"),
        new(Key("corner-bottom-left"), Corners, "Bottom-left corner", "One finger"),
        new(Key("corner-bottom-right"), Corners, "Bottom-right corner", "One finger"),
        new(Key("edge-left-up"), EdgeSlides, "Left edge, up", "Keys repeat as you slide", true),
        new(Key("edge-left-down"), EdgeSlides, "Left edge, down", "Keys repeat as you slide", true),
        new(Key("edge-right-up"), EdgeSlides, "Right edge, up", "Keys repeat as you slide", true),
        new(Key("edge-right-down"), EdgeSlides, "Right edge, down", "Keys repeat as you slide", true),
        new(Key("edge-top-left"), EdgeSlides, "Top edge, left", "Keys repeat as you slide", true),
        new(Key("edge-top-right"), EdgeSlides, "Top edge, right", "Keys repeat as you slide", true),
        new(Key("edge-bottom-left"), EdgeSlides, "Bottom edge, left", "Keys repeat as you slide", true),
        new(Key("edge-bottom-right"), EdgeSlides, "Bottom edge, right", "Keys repeat as you slide", true),
        new(Key("swipe-in-left"), EdgeSwipes, "From the left edge", "One finger, quick"),
        new(Key("swipe-in-right"), EdgeSwipes, "From the right edge", "One finger, quick"),
        new(Key("swipe-in-top"), EdgeSwipes, "From the top edge", "One finger, quick"),
        new(Key("swipe-in-bottom"), EdgeSwipes, "From the bottom edge", "One finger, quick"),
        new(Key("circle-cw"), Circles, "Clockwise", "Two turns; keys repeat each turn", true),
        new(Key("circle-ccw"), Circles, "Counterclockwise", "Two turns; keys repeat each turn", true),
        .. Fingers(3, ThreeFingers),
        .. Fingers(4, FourFingers),
        new(Key("5-tap"), FiveFingers, "Tap", "All five fingers"),
    ];

    private static IEnumerable<TouchpadGesture> Fingers(int count, string group) =>
    [
        new(Key($"{count}-up"), group, "Swipe up", $"{count} fingers"),
        new(Key($"{count}-down"), group, "Swipe down", $"{count} fingers"),
        new(Key($"{count}-left"), group, "Swipe left", $"{count} fingers"),
        new(Key($"{count}-right"), group, "Swipe right", $"{count} fingers"),
        new(Key($"{count}-tap"), group, "Tap", $"{count} fingers"),
    ];

    /// <summary>Corner zones as fractions of the pad's width and height; edge bands in pad heights.</summary>
    public const double CornerX = 0.14, CornerY = 0.2, EdgeBand = 0.08;

    public static string Key(string name) => Prefix + name;

    public static TouchpadGesture? Find(string key) => All.FirstOrDefault(g => g.Key == key);

    public static bool IsGesture(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// The user's Windows touchpad settings (Settings → Devices → Touchpad). OpenPointer never changes
/// them: a gesture Windows already uses stays Windows', and its slot is locked in OpenPointer.
/// </summary>
public sealed record WindowsTouchpadSettings(bool TapToClick, int ThreeSwipe, int FourSwipe, int ThreeTap, int FourTap)
{
    // Absent values mean Windows' defaults: switch apps / switch desktops, Search / Action Center.
    public static WindowsTouchpadSettings Default { get; } = new(true, 1, 2, 1, 2);

    public static WindowsTouchpadSettings Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            if (key == null) return Default;
            int Value(string name, int fallback) => key.GetValue(name) is int v ? v : fallback;
            return new(Value("TapsEnabled", -1) != 0, Value("ThreeFingerSlideEnabled", 1), Value("FourFingerSlideEnabled", 2),
                Value("ThreeFingerTapEnabled", 1), Value("FourFingerTapEnabled", 2));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Default; }
    }

    /// <summary>What Windows does with this gesture, or null when Windows leaves it free.</summary>
    public string? Owner(string key)
    {
        var name = key[TouchpadGestures.Prefix.Length..];
        var swipe = name.EndsWith("-tap") ? 0 : 1;
        return name[0] switch
        {
            '3' when swipe == 1 => SwipeLabel(ThreeSwipe),
            '3' => TapLabel(ThreeTap),
            '4' when swipe == 1 => SwipeLabel(FourSwipe),
            '4' => TapLabel(FourTap),
            // Windows counts a five-finger tap as its four-finger tap, so both would run.
            '5' => TapLabel(FourTap) is { } four ? $"{four} (Windows' four-finger tap also fires on five)" : null,
            _ => null,
        };
    }

    /// <summary>Corners need a short hold when a quick tap already clicks.</summary>
    public bool CornersNeedHold => TapToClick;

    private static string? SwipeLabel(int value) => value switch
    {
        0 => null,
        1 => "Switch apps and show desktop",
        2 => "Switch desktops and show desktop",
        3 => "Change audio and volume",
        _ => "Custom Windows gesture",
    };

    private static string? TapLabel(int value) => value switch
    {
        0 => null,
        1 => "Search",
        2 => "Action Center",
        3 => "Play/pause",
        4 => "Middle mouse button",
        _ => "Custom Windows gesture",
    };
}
