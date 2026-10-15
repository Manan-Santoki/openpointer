using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.App;

/// <summary>
/// Every change made in the settings window is drafted here: buttons and apps, pointer speed and
/// scrolling, cursors and app settings. One Save applies them all; Discard reverts them all.
/// The tray menu stays instant and writes straight to the config.
/// </summary>
public static class Draft
{
    private static string _baseline = "";

    /// <summary>DPI before a live preview started, per mouse, so Discard can put it back.</summary>
    private static readonly Dictionary<ConnectedMouse, int> PreviewedDpi = [];

    public static AppConfig Current { get; private set; } = new();

    public static bool IsDirty => Snapshot(Current) != _baseline;

    /// <summary>
    /// Raised on the UI thread after the draft changes or is saved/discarded. The argument says
    /// whether pages must rebuild; edits from sliders and switches already show their new state,
    /// and rebuilding under the user's pointer makes them flicker.
    /// </summary>
    public static event Action<bool>? Changed;

    public static void Initialize()
    {
        Reset(Services.Config.Current);
        Services.Config.Changed += config => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Follow outside changes (tray, hand edits) unless the user has edits in progress.
            if (IsDirty) return;
            Reset(config);
            Changed?.Invoke(true);
        });
    }

    public static void Edit(Action<AppConfig> change, bool rerender = true)
    {
        change(Current);
        Changed?.Invoke(rerender);
    }

    /// <summary>Edits the draft settings of one device.</summary>
    public static void EditDevice(string deviceName, Action<DeviceSettings> change, bool rerender = true) => Edit(config =>
    {
        if (!config.Devices.TryGetValue(deviceName, out var settings)) config.Devices[deviceName] = settings = new DeviceSettings();
        change(settings);
    }, rerender);

    /// <summary>Feels the new pointer speed right away; it is kept on Save and reverted on Discard.</summary>
    public static void PreviewDpi(ConnectedMouse mouse, int dpi)
    {
        if (mouse.Dpi is { } current) PreviewedDpi.TryAdd(mouse, current.Current);
        _ = Services.Devices.SetDpiAsync(mouse, dpi, persist: false, notify: false);
        EditDevice(mouse.Name, s => s.Dpi = dpi, rerender: false);
    }

    public static void Save()
    {
        if (!IsDirty) return;
        var draft = Clone(Current);
        var before = Services.Config.Current.General;

        Services.Config.Update(config =>
        {
            config.Profiles = draft.Profiles;
            config.Devices = draft.Devices;
            config.General = draft.General;
        });

        // Settings that live outside the mouse.
        var after = Services.Config.Current.General;
        if (after.CursorTheme != before.CursorTheme || after.CursorShadow != before.CursorShadow || after.CursorSize != before.CursorSize)
            CursorManager.Apply(after);
        if (after.Theme != before.Theme) ThemeManager.Apply(after.Theme);
        if (after.ScrollLines is { } lines && lines != SystemMouse.ScrollLines) SystemMouse.ScrollLines = lines;
        if (after.StartWithWindows is { } start && start != Autostart.IsEnabled()) Autostart.Set(start, Services.ExecutablePath);

        PreviewedDpi.Clear();
        Reset(Services.Config.Current);
        Changed?.Invoke(true);
    }

    public static void Discard()
    {
        foreach (var (mouse, dpi) in PreviewedDpi) _ = Services.Devices.SetDpiAsync(mouse, dpi, persist: false);
        PreviewedDpi.Clear();
        Reset(Services.Config.Current);
        Services.Devices.Reapply();
        Changed?.Invoke(true);
    }

    private static void Reset(AppConfig config)
    {
        Current = Clone(config);
        // Show what Windows actually has for settings that also change outside OpenPointer.
        Current.General.ScrollLines = SystemMouse.ScrollLines;
        Current.General.StartWithWindows = Autostart.IsEnabled();
        _baseline = Snapshot(Current);
    }

    /// <summary>Which areas have unsaved changes, for the Save bar ("Buttons · Cursor").</summary>
    public static IReadOnlyList<string> ChangedAreas()
    {
        var saved = Clone(Services.Config.Current);
        saved.General.ScrollLines = SystemMouse.ScrollLines;
        saved.General.StartWithWindows = Autostart.IsEnabled();
        var draft = Current;
        string J(object value) => System.Text.Json.JsonSerializer.Serialize(value, ConfigStore.JsonOptions);

        var areas = new List<string>();
        if (J(draft.Profiles) != J(saved.Profiles)) areas.Add("Buttons & apps");
        if (J(draft.Devices) != J(saved.Devices) || draft.General.ScrollLines != saved.General.ScrollLines) areas.Add("Pointer & scrolling");
        if (draft.General.CursorTheme != saved.General.CursorTheme || draft.General.CursorShadow != saved.General.CursorShadow ||
            draft.General.CursorSize != saved.General.CursorSize) areas.Add("Cursor");
        if (draft.General.Theme != saved.General.Theme || draft.General.StartWithWindows != saved.General.StartWithWindows ||
            draft.General.WarnAboutOptionsPlus != saved.General.WarnAboutOptionsPlus || draft.General.CheckForUpdates != saved.General.CheckForUpdates)
            areas.Add("Settings");
        return areas;
    }

    private static AppConfig Clone(AppConfig config) => ConfigStore.Parse(ConfigStore.Serialize(config));

    private static string Snapshot(AppConfig config) => ConfigStore.Serialize(config);
}
