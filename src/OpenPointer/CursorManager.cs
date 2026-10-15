using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.App;

/// <summary>Applies the cursor style chosen in Settings: Windows default or a bundled cursor pack.</summary>
public static class CursorManager
{
    public const string Unchanged = "unchanged";
    public const string Windows = "windows";

    private static List<CursorPack>? _packs;

    /// <summary>Packs shipped in Assets\Cursors, each described by its pack.json.</summary>
    public static IReadOnlyList<CursorPack> Packs =>
        _packs ??= CursorPacks.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"), Log.Warn);

    public static CursorPack? Find(string id) => Packs.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static void Apply(GeneralSettings settings)
    {
        try
        {
            if (settings.CursorTheme == Windows)
            {
                CursorThemes.ApplyWindowsDefault();
                return;
            }

            if (Find(settings.CursorTheme) is not { } pack) return;
            var size = pack.SizeKeys.Contains(settings.CursorSize) ? settings.CursorSize : pack.SizeKeys.FirstOrDefault() ?? "normal";
            CursorThemes.ApplyFolder(pack.VariantFolder(settings.CursorShadow, size), pack.Roles,
                $"{pack.Name} (OpenPointer)", pack.VariantId(settings.CursorShadow, size));
        }
        catch (Exception ex)
        {
            Log.Error("Applying cursors", ex);
        }
    }

    /// <summary>Puts back the user's original cursors and stops managing them.</summary>
    public static void Restore()
    {
        try { CursorThemes.RestoreBackup(); }
        catch (Exception ex) { Log.Error("Restoring cursors", ex); }
    }
}
