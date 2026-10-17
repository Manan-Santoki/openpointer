using System.Text.Json;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>
/// Remembers which buttons each standard mouse has actually pressed, in
/// %LOCALAPPDATA%\OpenPointer\seen-buttons.json. Many mice declare five HID buttons but have
/// three, so reported Back and Forward stay hidden until Windows sees them pressed.
/// </summary>
internal sealed class SeenButtonStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPointer", "seen-buttons.json");
    private readonly object _sync = new();
    private Dictionary<string, HashSet<string>>? _seen;

    public IReadOnlySet<string> For(WindowsMouseInfo mouse)
    {
        lock (_sync) return Load().TryGetValue(Key(mouse), out var buttons) ? buttons.ToHashSet() : new HashSet<string>();
    }

    /// <summary>Records a press; false when the button was already known.</summary>
    public bool Add(WindowsMouseInfo mouse, string button)
    {
        lock (_sync)
        {
            var seen = Load();
            if (!seen.TryGetValue(Key(mouse), out var buttons)) seen[Key(mouse)] = buttons = [];
            if (!buttons.Add(button)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(seen));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Saving seen buttons: {ex.Message}"); }
            return true;
        }
    }

    /// <summary>Same model, same buttons: keyed by hardware ID when Windows exposes one.</summary>
    internal static string Key(WindowsMouseInfo mouse) => mouse is { VendorId: { } vendor, ProductId: { } product }
        ? $"{vendor:X4}:{product:X4}" : mouse.DevicePath.ToUpperInvariant();

    private Dictionary<string, HashSet<string>> Load()
    {
        if (_seen != null) return _seen;
        try
        {
            _seen = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(_path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log.Warn($"Reading seen buttons: {ex.Message}"); }
        return _seen ??= [];
    }
}
