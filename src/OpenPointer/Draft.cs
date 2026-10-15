using OpenPointer.Config;

namespace OpenPointer.App;

/// <summary>
/// Unsaved profile edits made in the settings window. Pages read and write the draft; Save commits
/// its profiles to config.json, Discard reverts. Device and app settings stay instant, and so does
/// the tray menu, which writes straight to the config.
/// </summary>
public static class Draft
{
    private static string _baseline = "";

    public static AppConfig Current { get; private set; } = new();

    public static bool IsDirty => Snapshot(Current) != _baseline;

    /// <summary>Raised on the UI thread after the draft changes or is saved/discarded.</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        Reset(Services.Config.Current);
        Services.Config.Changed += config => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // Follow outside changes (tray, hand edits) unless the user has edits in progress.
            if (IsDirty) return;
            Reset(config);
            Changed?.Invoke();
        });
    }

    public static void Edit(Action<AppConfig> change)
    {
        change(Current);
        Changed?.Invoke();
    }

    public static void Save()
    {
        if (!IsDirty) return;
        var profiles = Clone(Current).Profiles;
        Services.Config.Update(config => config.Profiles = profiles);
        Reset(Services.Config.Current);
        Changed?.Invoke();
    }

    public static void Discard()
    {
        Reset(Services.Config.Current);
        Changed?.Invoke();
    }

    private static void Reset(AppConfig config)
    {
        Current = Clone(config);
        _baseline = Snapshot(Current);
    }

    private static AppConfig Clone(AppConfig config) => ConfigStore.Parse(ConfigStore.Serialize(config));

    // Only profiles are drafted; device, cursor and general settings apply immediately.
    private static string Snapshot(AppConfig config) =>
        System.Text.Json.JsonSerializer.Serialize(config.Profiles, ConfigStore.JsonOptions);
}
