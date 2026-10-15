using System.Windows;
using Microsoft.Win32;
using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.App;

/// <summary>Application-wide services, created once at startup.</summary>
public static class Services
{
    public static ConfigStore Config { get; private set; } = null!;
    public static DeviceManager Devices { get; private set; } = null!;
    public static FocusTracker Focus { get; private set; } = null!;

    public static void Initialize(ConfigStore config, DeviceManager devices, FocusTracker focus)
    {
        Config = config;
        Devices = devices;
        Focus = focus;
    }

    /// <summary>The mouse shown in the settings window; the first connected one by default.</summary>
    public static ConnectedMouse? SelectedMouse
    {
        get
        {
            var mice = Devices.Mice;
            return mice.FirstOrDefault(m => m.Name == _selectedName) ?? mice.FirstOrDefault(m => m.Online) ?? mice.FirstOrDefault();
        }
        set => _selectedName = value?.Name;
    }

    private static string? _selectedName;

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenPointer.exe");
}

public static class ThemeManager
{
    private static string _mode = "system";

    public static bool IsDark { get; private set; } = true;

    public static event Action? Changed;

    public static void Apply(string mode)
    {
        _mode = mode;
        var dark = mode switch
        {
            "light" => false,
            "dark" => true,
            _ => !SystemUsesLightApps(),
        };
        IsDark = dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        Changed?.Invoke();
    }

    public static void WatchSystem()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_mode == "system") Apply(_mode);
                Changed?.Invoke();
            });
        };
    }

    public static bool SystemUsesLightApps() => ReadPersonalize("AppsUseLightTheme");

    public static bool TaskbarIsLight() => ReadPersonalize("SystemUsesLightTheme");

    private static bool ReadPersonalize(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue(name) is int value && value != 0;
    }
}
