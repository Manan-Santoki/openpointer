using System.Windows;
using System.Windows.Threading;
using OpenPointer.Updates;

namespace OpenPointer.App;

/// <summary>Background update checks and one-click install from GitHub Releases.</summary>
public static class UpdateManager
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static DispatcherTimer? _timer;
    private static Version? _announced;

    public static Version Current { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static UpdateInfo? Available { get; private set; }
    public static bool Busy { get; private set; }
    public static string Status { get; private set; } = "";

    /// <summary>Raised on the UI thread whenever Available, Busy or Status changes.</summary>
    public static event Action? Changed;

    /// <summary>Raised once per new version found by a background check.</summary>
    public static event Action<UpdateInfo>? Found;

    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (Services.Config.Current.General.CheckForUpdates) await CheckAsync(userInitiated: false);
        };
        _timer.Start();
    }

    public static async Task CheckAsync(bool userInitiated)
    {
        if (Busy) return;
        SetState(busy: true, userInitiated ? "Checking for updates…" : Status);
        try
        {
            Available = await Updater.CheckAsync(Current);
            if (Available != null)
            {
                SetState(false, $"Version {Available.Version.ToString(3)} is available.");
                if (!userInitiated && _announced != Available.Version)
                {
                    _announced = Available.Version;
                    Found?.Invoke(Available);
                }
            }
            else
            {
                SetState(false, userInitiated ? "You have the latest version." : Status);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            SetState(false, userInitiated ? "Could not reach GitHub. Try again later." : Status);
        }
    }

    /// <summary>Downloads, verifies and runs the installer, then exits so it can replace the app.</summary>
    public static async Task InstallAsync()
    {
        if (Available is not { } update || Busy) return;
        SetState(true, "Downloading update…");
        try
        {
            var progress = new Progress<double>(p => SetState(true, $"Downloading update… {p:P0}"));
            var path = await Updater.DownloadAsync(update, progress);
            SetState(true, "Installing…");
            Log.Info($"Installing update {update.Tag}");
            Services.Devices.RestoreDevices();
            Updater.LaunchInstaller(path);
            App.Quit();
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            SetState(false, $"Update failed: {ex.Message}");
        }
    }

    private static void SetState(bool busy, string status)
    {
        Busy = busy;
        Status = status;
        if (Application.Current.Dispatcher.CheckAccess()) Changed?.Invoke();
        else Application.Current.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
