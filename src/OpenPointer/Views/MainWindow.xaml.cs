using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private readonly ButtonsPage _buttons;
    private readonly PointerPage _pointer;
    private readonly ApplicationsPage _apps;
    private readonly SettingsPage _settings;
    private IPage _page;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · MIT";

        _buttons = new ButtonsPage(this);
        _pointer = new PointerPage(this);
        _apps = new ApplicationsPage(this);
        _settings = new SettingsPage(this);
        _page = _buttons;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RenderNow();
        };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => UpdateBanner();
        _poll.Start();

        Services.Devices.StateChanged += OnExternalChange;
        Services.Config.Changed += OnConfigChanged;
        ThemeManager.Changed += Render;
        UpdateManager.Changed += Render;
        Draft.Changed += OnDraftChanged;
        StateChanged += (_, _) =>
        {
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        Closing += (_, _) =>
        {
            if (Draft.IsDirty && !App.IsQuitting)
            {
                if (Dialogs.Confirm(this, "Save your changes?", "You changed buttons or profiles without saving.", "Save")) Draft.Save();
                else Draft.Discard();
            }

            // Closing the window cancels any open dialogs so their callers unwind.
            foreach (var dialog in _dialogs.ToList()) dialog.Close();
        };
        Closed += (_, _) =>
        {
            _poll.Stop();
            Services.Devices.StateChanged -= OnExternalChange;
            Services.Config.Changed -= OnConfigChanged;
            ThemeManager.Changed -= Render;
            UpdateManager.Changed -= Render;
            Draft.Changed -= OnDraftChanged;
        };
        UpdateBanner();
        ShowSaveBar(Draft.IsDirty);
        RenderNow();
    }

    private void OnExternalChange() => Dispatcher.BeginInvoke(Render);

    private void OnConfigChanged(Config.AppConfig _) => Dispatcher.BeginInvoke(Render);

    /// <summary>Re-renders soon; coalesces bursts of device and config events.</summary>
    public void Render()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void RenderNow()
    {
        RenderDevices();
        var offset = Scroller.VerticalOffset;
        PageHost.Content = _page.Render();
        Scroller.ScrollToVerticalOffset(offset);
    }

    public void ShowButtonsFor(int profileIndex)
    {
        _buttons.ProfileIndex = profileIndex;
        if (NavButtons.IsChecked == true) RenderNow();
        else NavButtons.IsChecked = true;
    }

    /// <summary>Switches page by name ("Buttons", "Pointer", "Apps", "Settings").</summary>
    public void Navigate(string page)
    {
        var target = page switch { "Pointer" => NavPointer, "Apps" => NavApps, "Settings" => NavSettings, _ => NavButtons };
        target.IsChecked = true;
        RenderNow();
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (_buttons == null) return; // during InitializeComponent
        _page = sender == NavPointer ? _pointer : sender == NavApps ? _apps : sender == NavSettings ? _settings : _buttons;
        PageHost.Content = _page.Render();
        Scroller.ScrollToTop();
    }

    private void RenderDevices()
    {
        DeviceList.Children.Clear();
        var mice = Services.Devices.Mice;
        if (mice.Count == 0)
        {
            DeviceList.Children.Add(DeviceCard(null, false));
            return;
        }

        var selected = Services.SelectedMouse;
        foreach (var mouse in mice) DeviceList.Children.Add(DeviceCard(mouse, mice.Count > 1 && mouse == selected));
    }

    private FrameworkElement DeviceCard(ConnectedMouse? mouse, bool highlighted)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(mouse?.Name ?? "No device", "Label"));
        var status = mouse == null ? "Searching for receivers…"
            : !mouse.Online ? "Asleep or out of range"
            : mouse.Battery is { } b ? $"{b.Percent}% · {b.Status}" : "Connected";
        var caption = Ui.Text(status, "Caption");
        caption.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(caption);

        if (mouse is { Online: true, Battery: { } battery })
        {
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 10, 0, 0) };
            track.SetResourceReference(Border.BackgroundProperty, "AccentStrong");
            var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "Foreground");
            track.Child = fill;
            track.SizeChanged += (_, _) => fill.Width = Math.Max(4, track.ActualWidth * Math.Clamp(battery.Percent, 0, 100) / 100.0);
            stack.Children.Add(track);
        }

        var card = new Border { Style = Ui.Style("Card"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = stack };
        if (highlighted) card.SetResourceReference(Border.BorderBrushProperty, "Ring");
        if (mouse != null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) =>
            {
                Services.SelectedMouse = mouse;
                RenderNow();
            };
        }

        return card;
    }

    private void UpdateBanner()
    {
        var show = Services.Config.Current.General.WarnAboutOptionsPlus && OptionsPlus.IsRunning();
        Banner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStopOptionsPlus(object sender, RoutedEventArgs e)
    {
        App.StopOptionsPlus(this);
        UpdateBanner();
    }

    private readonly List<DialogHost> _dialogs = [];

    /// <summary>Shows a dialog on top of the window, blurring and disabling everything behind it.</summary>
    public void PushDialog(DialogHost dialog)
    {
        if (_dialogs.Count > 0) _dialogs[^1].Visibility = Visibility.Hidden;
        _dialogs.Add(dialog);
        DialogStack.Children.Add(dialog);
        OverlayLayer.Visibility = Visibility.Visible;
        Shell.IsEnabled = false;
        Shell.Effect ??= new System.Windows.Media.Effects.BlurEffect { Radius = 8, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
    }

    public void PopDialog(DialogHost dialog)
    {
        _dialogs.Remove(dialog);
        DialogStack.Children.Remove(dialog);
        if (_dialogs.Count > 0)
        {
            _dialogs[^1].Visibility = Visibility.Visible;
            return;
        }

        OverlayLayer.Visibility = Visibility.Collapsed;
        Shell.IsEnabled = true;
        Shell.Effect = null;
    }

    private void OnDraftChanged()
    {
        ShowSaveBar(Draft.IsDirty);
        Render();
    }

    /// <summary>Slides the floating save bar in or out and keeps page content clear of it.</summary>
    private void ShowSaveBar(bool show)
    {
        PageHost.Margin = new Thickness(40, 4, 40, show ? 112 : 40);
        if (show == (SaveBar.Visibility == Visibility.Visible)) return;
        if (!show)
        {
            SaveBar.Visibility = Visibility.Collapsed;
            return;
        }

        SaveBar.Visibility = Visibility.Visible;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        SaveBarOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        SaveBar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void OnSave(object sender, RoutedEventArgs e) => Draft.Save();

    private void OnDiscard(object sender, RoutedEventArgs e) => Draft.Discard();

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

public interface IPage
{
    FrameworkElement Render();
}
