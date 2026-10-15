using System.Windows;
using System.Windows.Threading;
using OpenPointer.App.Tray;
using OpenPointer.App.Views;
using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.App;

public partial class App : Application
{
    private const string InstanceName = "OpenPointer.Singleton";
    private const string ShowEventName = "OpenPointer.Show";
    private const string QuitEventName = "OpenPointer.Quit";

    private Mutex? _singleton;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _quitEvent;
    private TrayIcon? _tray;
    private static MainWindow? _main;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleton = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to show its window (or quit, for installers/updates).
            try { EventWaitHandle.OpenExisting(e.Args.Contains("--quit") ? QuitEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPointer");
        Log.Initialize(dataDirectory);
        Log.Info($"OpenPointer {typeof(App).Assembly.GetName().Version} starting");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);

        var config = new ConfigStore();
        string? configError = null;
        try
        {
            config.Load();
        }
        catch (Exception ex)
        {
            // Keep running on defaults but never overwrite the user's broken file.
            configError = ex.Message;
            Log.Error("Loading config", ex);
        }

        config.Watch();
        ThemeManager.Apply(config.Current.General.Theme);
        SmoothScroll.Register();
        ThemeManager.WatchSystem();

        var devices = new DeviceManager(config);
        var focus = new FocusTracker();
        Services.Initialize(config, devices, focus);

        focus.Changed += devices.OnFocusChanged;
        focus.Start();
        devices.OnFocusChanged(focus.Current);
        devices.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => devices.RestoreDevices();

        _tray = new TrayIcon();
        config.Error += message => Dispatcher.BeginInvoke(() => _tray?.Balloon("Configuration error", message));
        if (configError != null) _tray.Balloon("Configuration error", $"Using defaults. {configError}");

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "Show signal" }.Start();

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            if (_quitEvent.WaitOne()) Dispatcher.BeginInvoke(Quit);
        }) { IsBackground = true, Name = "Quit signal" }.Start();

        if (e.Args.Contains("--quit"))
        {
            Quit();
            return;
        }

        if (!e.Args.Contains("--background")) ShowMainWindow();

        if (config.Current.General.WarnAboutOptionsPlus && OptionsPlus.IsRunning())
            _tray.Balloon("Logi Options+ is running", "It overrides button remapping. Stop it from the tray menu.");
    }

    public static MainWindow? MainWindowInstance => _main;

    public static MainWindow ShowMainWindowAndGet()
    {
        ShowMainWindow();
        return _main!;
    }

    public static void ShowMainWindow()
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }

        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public static void StopOptionsPlus(Window? owner)
    {
        if (!Dialogs.Confirm(owner, "Stop Logi Options+?",
                "Options+ stays installed and starts again with Windows. Stop it now so OpenPointer can control your buttons.",
                "Stop Options+"))
            return;

        if (!OptionsPlus.Stop())
            Dialogs.Confirm(owner, "Could not stop Options+", "Part of Options+ is running with higher privileges. Close it from Task Manager.", "OK");
        Services.Devices.Rescan();
    }

    public static void Quit()
    {
        Current._tray?.Dispose();
        Services.Devices.Dispose();
        Services.Focus.Dispose();
        Services.Config.Dispose();
        Log.Info("OpenPointer exiting");
        Current.Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        e.Handled = true;
    }
}
