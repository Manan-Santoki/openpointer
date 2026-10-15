using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenPointer.Platform;

/// <summary>
/// Switches the Windows cursor scheme (HKCU\Control Panel\Cursors) and reloads it instantly.
/// The user's original scheme is backed up the first time OpenPointer changes it, so it can be
/// restored later (and on uninstall).
/// </summary>
public static partial class CursorThemes
{
    private const string CursorsKey = @"Control Panel\Cursors";

    /// <summary>Every cursor role Windows reads, in the order of the Schemes registry format.</summary>
    public static readonly string[] Roles =
        ["Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person"];

    /// <summary>The stock "Windows Default" scheme.</summary>
    private static readonly Dictionary<string, string> WindowsDefault = new()
    {
        ["Arrow"] = @"%SystemRoot%\cursors\aero_arrow.cur",
        ["Help"] = @"%SystemRoot%\cursors\aero_helpsel.cur",
        ["AppStarting"] = @"%SystemRoot%\cursors\aero_working.ani",
        ["Wait"] = @"%SystemRoot%\cursors\aero_busy.ani",
        ["Crosshair"] = "",
        ["IBeam"] = "",
        ["NWPen"] = @"%SystemRoot%\cursors\aero_pen.cur",
        ["No"] = @"%SystemRoot%\cursors\aero_unavail.cur",
        ["SizeNS"] = @"%SystemRoot%\cursors\aero_ns.cur",
        ["SizeWE"] = @"%SystemRoot%\cursors\aero_ew.cur",
        ["SizeNWSE"] = @"%SystemRoot%\cursors\aero_nwse.cur",
        ["SizeNESW"] = @"%SystemRoot%\cursors\aero_nesw.cur",
        ["SizeAll"] = @"%SystemRoot%\cursors\aero_move.cur",
        ["UpArrow"] = @"%SystemRoot%\cursors\aero_up.cur",
        ["Hand"] = @"%SystemRoot%\cursors\aero_link.cur",
        ["Pin"] = @"%SystemRoot%\cursors\aero_pin.cur",
        ["Person"] = @"%SystemRoot%\cursors\aero_person.cur",
    };

    /// <summary>Friendly names Windows uses for each role (Mouse settings, Pointers tab).</summary>
    public static readonly Dictionary<string, string> RoleLabels = new()
    {
        ["Arrow"] = "Normal select",
        ["Help"] = "Help select",
        ["AppStarting"] = "Working in background",
        ["Wait"] = "Busy",
        ["Crosshair"] = "Precision select",
        ["IBeam"] = "Text select",
        ["NWPen"] = "Handwriting",
        ["No"] = "Unavailable",
        ["SizeNS"] = "Vertical resize",
        ["SizeWE"] = "Horizontal resize",
        ["SizeNWSE"] = "Diagonal resize 1",
        ["SizeNESW"] = "Diagonal resize 2",
        ["SizeAll"] = "Move",
        ["UpArrow"] = "Alternate select",
        ["Hand"] = "Link select",
        ["Pin"] = "Location select",
        ["Person"] = "Person select",
    };

    /// <summary>The Windows default file for a role, expanded; empty for roles drawn from built-in resources.</summary>
    public static string DefaultFile(string role) =>
        WindowsDefault.TryGetValue(role, out var path) ? Environment.ExpandEnvironmentVariables(path) : "";

    /// <summary>The currently active scheme name ("" when Windows uses its built-in cursors).</summary>
    public static string ActiveSchemeName
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(CursorsKey);
            return key?.GetValue("") as string ?? "";
        }
    }

    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPointer");

    private static string BackupPath => Path.Combine(DataDirectory, "cursor-backup.json");

    public static bool HasBackup => File.Exists(BackupPath);

    public static void ApplyWindowsDefault()
    {
        BackupIfNeeded();
        Write("Windows Default", WindowsDefault);
    }

    /// <summary>
    /// Applies a cursor set from a folder. Files are copied to %LOCALAPPDATA%\OpenPointer\Cursors so
    /// the scheme keeps working when the app is updated or moved.
    /// </summary>
    public static void ApplyFolder(string sourceFolder, IReadOnlyDictionary<string, string> files, string schemeName, string variant)
    {
        var target = Path.Combine(DataDirectory, "Cursors", variant);
        Directory.CreateDirectory(target);
        foreach (var file in files.Values.Distinct())
        {
            var source = Path.Combine(sourceFolder, file);
            if (!File.Exists(source)) throw new FileNotFoundException($"Cursor file is missing: {file}", source);
            File.Copy(source, Path.Combine(target, file), overwrite: true);
        }

        BackupIfNeeded();
        // Roles the pack does not provide fall back to the Windows default.
        var paths = new Dictionary<string, string>(WindowsDefault);
        foreach (var (role, file) in files) paths[role] = Path.Combine(target, file);
        Write(schemeName, paths);
    }

    /// <summary>Puts back the cursors the user had before OpenPointer changed them.</summary>
    public static bool RestoreBackup()
    {
        if (!HasBackup) return false;
        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupPath)) ?? [];
        saved.TryGetValue("", out var scheme);
        Write(scheme ?? "", saved.Where(kv => kv.Key.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value));
        File.Delete(BackupPath);
        return true;
    }

    private static void BackupIfNeeded()
    {
        if (HasBackup) return;
        using var key = Registry.CurrentUser.OpenSubKey(CursorsKey);
        var saved = new Dictionary<string, string> { [""] = key?.GetValue("") as string ?? "" };
        foreach (var role in Roles)
            saved[role] = key?.GetValue(role, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(saved));
    }

    private static void Write(string schemeName, IReadOnlyDictionary<string, string> paths)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey))
        {
            key.SetValue("", schemeName, RegistryValueKind.String);
            foreach (var role in Roles)
                key.SetValue(role, paths.TryGetValue(role, out var path) ? path : "", RegistryValueKind.ExpandString);
        }

        // Reload the cursors for every app without signing out.
        SystemParametersInfoW(SPI_SETCURSORS, 0, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, nint value, uint winIni);
}
