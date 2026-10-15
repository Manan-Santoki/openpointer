namespace OpenPointer.Config;

/// <summary>Parses and formats chords like "ctrl+shift+t", "win+left" or "volume_up".</summary>
public static class KeyChord
{
    private static readonly Dictionary<string, ushort> Keys = BuildKeys();

    private static readonly HashSet<ushort> Modifiers = [0x10, 0x11, 0x12, 0x5B, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    private static Dictionary<string, ushort> BuildKeys()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["meta"] = 0x5B,
            ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D, ["pause"] = 0x13,
            ["capslock"] = 0x14, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, ["pageup"] = 0x21,
            ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27,
            ["down"] = 0x28, ["printscreen"] = 0x2C, ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["apps"] = 0x5D, ["menu"] = 0x5D,
            ["browser_back"] = 0xA6, ["browser_forward"] = 0xA7, ["browser_refresh"] = 0xA8,
            ["browser_home"] = 0xAC, ["volume_mute"] = 0xAD, ["volume_down"] = 0xAE, ["volume_up"] = 0xAF,
            ["media_next"] = 0xB0, ["media_prev"] = 0xB1, ["media_stop"] = 0xB2, ["media_play_pause"] = 0xB3,
            ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE, ["semicolon"] = 0xBA,
            ["slash"] = 0xBF, ["backquote"] = 0xC0, ["lbracket"] = 0xDB, ["backslash"] = 0xDC, ["rbracket"] = 0xDD,
            ["quote"] = 0xDE,
            ["numpad_add"] = 0x6B, ["numpad_subtract"] = 0x6D, ["numpad_multiply"] = 0x6A, ["numpad_divide"] = 0x6F,
        };
        for (var c = 'a'; c <= 'z'; c++) map[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (var d = 0; d <= 9; d++)
        {
            map[d.ToString()] = (ushort)('0' + d);
            map[$"numpad{d}"] = (ushort)(0x60 + d);
        }

        for (var f = 1; f <= 24; f++) map[$"f{f}"] = (ushort)(0x6F + f);
        return map;
    }

    /// <summary>Virtual-key codes in press order (modifiers first). Throws on unknown key names.</summary>
    public static IReadOnlyList<ushort> Parse(string chord)
    {
        var codes = new List<ushort>();
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Keys.TryGetValue(raw, out var vk))
                throw new FormatException($"Unknown key \"{raw}\" in \"{chord}\".");
            codes.Add(vk);
        }

        return codes.OrderBy(vk => IsModifier(vk) ? 0 : 1).ToList();
    }

    public static bool TryParse(string chord, out IReadOnlyList<ushort> codes)
    {
        try
        {
            codes = Parse(chord);
            return codes.Count > 0;
        }
        catch (FormatException)
        {
            codes = [];
            return false;
        }
    }

    public static bool IsModifier(ushort vk) => Modifiers.Contains(vk);

    private static readonly HashSet<string> RepeatableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "volume_up", "volume_down", "up", "down", "left", "right", "pageup", "pagedown",
        "backspace", "delete", "del", "plus", "minus", "numpad_add", "numpad_subtract", "z", "y",
    };

    /// <summary>
    /// Whether holding this chord should repeat it. True when its main key is one people hold on a
    /// keyboard (volume, arrows, deletion, zoom, undo/redo) and no Win/Alt makes it a one-shot command.
    /// </summary>
    public static bool IsRepeatable(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        if (parts.Any(p => p.Equals("win", StringComparison.OrdinalIgnoreCase) || p.Equals("alt", StringComparison.OrdinalIgnoreCase)))
            return false;
        var main = parts[^1];
        // Bare z/y would type letters; only undo/redo repeat.
        if (main is "z" or "y" or "Z" or "Y")
            return parts.Any(p => p.Equals("ctrl", StringComparison.OrdinalIgnoreCase));
        return RepeatableKeys.Contains(main);
    }

    /// <summary>Name for a virtual-key code, for building chords from captured key presses.</summary>
    public static string? NameOf(ushort vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => "shift",
        0x11 or 0xA2 or 0xA3 => "ctrl",
        0x12 or 0xA4 or 0xA5 => "alt",
        0x5B or 0x5C => "win",
        _ => Keys.FirstOrDefault(kv => kv.Value == vk && kv.Key.Length > 0 && kv.Key != "control" && kv.Key != "return" &&
                                      kv.Key != "escape" && kv.Key != "del" && kv.Key != "meta" && kv.Key != "menu").Key,
    };

    /// <summary>"ctrl+shift+t" → "Ctrl + Shift + T".</summary>
    public static string Format(string chord) => string.Join(" + ",
        chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Pretty));

    private static string Pretty(string key) => key.ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Ctrl",
        "win" or "meta" => "Win",
        "media_play_pause" => "Play/Pause",
        "media_next" => "Next track",
        "media_prev" => "Previous track",
        "volume_up" => "Volume up",
        "volume_down" => "Volume down",
        "volume_mute" => "Mute",
        "browser_back" => "Browser back",
        "browser_forward" => "Browser forward",
        { Length: 1 } k => k.ToUpperInvariant(),
        var k => char.ToUpperInvariant(k[0]) + k[1..],
    };
}
