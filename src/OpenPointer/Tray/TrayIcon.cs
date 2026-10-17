using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Hidpp;
using OpenPointer.Platform;
using Forms = System.Windows.Forms;

namespace OpenPointer.App.Tray;

/// <summary>Notification-area icon with a themed WPF menu for on-the-fly changes.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _anchor;
    private Icon? _current;
    private DateTime _lastFailureBalloon = DateTime.MinValue;

    public TrayIcon()
    {
        _icon = new Forms.NotifyIcon { Text = "OpenPointer", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            else if (e.Button == Forms.MouseButtons.Left) App.ShowMainWindow();
        };

        // A WPF menu needs a foreground window of ours, or it will not close when focus leaves.
        _anchor = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            AllowsTransparency = true, Opacity = 0, Left = -10000, Top = -10000, ShowActivated = true,
        };

        Services.Devices.StateChanged += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Services.Devices.LowBattery += mouse => Application.Current.Dispatcher.BeginInvoke(() =>
            Balloon($"{mouse.Name} battery low", $"{mouse.Battery?.Percent}% remaining. Charge it soon."));
        Services.Devices.Runner.Failed += message => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.UtcNow - _lastFailureBalloon < TimeSpan.FromSeconds(10)) return;
            _lastFailureBalloon = DateTime.UtcNow;
            Balloon("Action failed", message);
        });
        ThemeManager.Changed += Refresh;
        Refresh();
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    private void Refresh()
    {
        var mouse = Services.SelectedMouse;
        var tooltip = mouse == null
            ? "OpenPointer · no device"
            : mouse.Online
                ? $"OpenPointer · {mouse.Name}{(mouse.Battery != null ? $" · {mouse.Battery.Percent}%" : "")}"
                : $"OpenPointer · {mouse.Name} (asleep)";
        if (Services.Devices.Paused) tooltip += " · paused";
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        var next = Draw(ThemeManager.TaskbarIsLight(), mouse is { Online: true }, Services.Devices.Paused,
            mouse?.Battery is { Charging: false, Percent: <= 15 });
        _icon.Icon = next;
        if (_current != null) DestroyIcon(_current.Handle);
        _current = next;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        Build(menu);
        _anchor.Show();
        _anchor.Activate();
        SetForegroundWindow(new WindowInteropHelper(_anchor).Handle);
        menu.Closed += (_, _) => _anchor.Hide();
        menu.IsOpen = true;
    }

    private void Build(ContextMenu menu)
    {
        var devices = Services.Devices;
        var mice = devices.Mice;

        if (mice.Count == 0)
        {
            menu.Items.Add(Ui.MenuLabel(Header("No device", "Looking for Bluetooth, USB and wireless mice…", null)));
        }

        foreach (var mouse in mice)
        {
            var status = !mouse.Online ? "Asleep or out of range"
                : mouse.Battery is { } b ? $"{b.Percent}%  ·  {b.Status}" : "Connected";
            menu.Items.Add(Ui.MenuLabel(Header(mouse.Name, status, mouse.Online ? mouse.Battery?.Percent : null)));
        }

        var app = Services.Focus.LastApp;
        var config = Services.Config.Current;
        var mouseForButtons = Services.SelectedMouse;
        if (mouseForButtons is { Online: true } && app.ProcessName.Length > 0)
        {
            menu.Items.Add(new Separator());
            var profile = ContextMatcher.FindAppProfile(config, app);
            var title = Ui.Text(app.IsDesktop ? "DESKTOP" : $"{(profile?.Name ?? app.DisplayName).ToUpperInvariant()}", "Overline");
            title.Margin = new Thickness(0, 4, 0, 2);
            menu.Items.Add(Ui.MenuLabel(title));

            foreach (var button in mouseForButtons.Buttons)
            {
                var current = ContextMatcher.ResolveButton(config, app, button);
                var item = new MenuItem { Header = ButtonNames.Label(button), InputGestureText = ActionCatalog.Label(current) };
                var hasOverride = profile?.Buttons.ContainsKey(button) == true;
                ActionMenu.Populate(item, current, picked => AssignForApp(app, button, picked),
                    hasOverride ? "Use All applications setting" : null, mouseForButtons.SupportsGestures(button));
                menu.Items.Add(item);
            }
        }

        if (mouseForButtons is { Online: true, Dpi: { } dpi })
        {
            menu.Items.Add(new Separator());
            var dpiItem = new MenuItem { Header = "Pointer speed", InputGestureText = $"{dpi.Current} DPI" };
            foreach (var preset in devices.DpiPresets(mouseForButtons))
            {
                var value = preset;
                dpiItem.Items.Add(Ui.MenuItem($"{value} DPI", () => _ = devices.SetDpiAsync(mouseForButtons, value, persist: true), value == dpi.Current));
            }

            menu.Items.Add(dpiItem);

            if (mouseForButtons.SmartShift is { } shift)
            {
                var wheel = new MenuItem { Header = "Scroll wheel", InputGestureText = shift.Mode == WheelRatchetMode.Ratchet ? "Ratchet" : "Free spin" };
                wheel.Items.Add(Ui.MenuItem("Ratchet", () => _ = SetWheel(mouseForButtons, WheelRatchetMode.Ratchet), shift.Mode == WheelRatchetMode.Ratchet));
                wheel.Items.Add(Ui.MenuItem("Free spin", () => _ = SetWheel(mouseForButtons, WheelRatchetMode.Freespin), shift.Mode == WheelRatchetMode.Freespin));
                wheel.Items.Add(new Separator());
                wheel.Items.Add(Ui.MenuItem("SmartShift", () => _ = devices.SetSmartShiftAsync(mouseForButtons, !shift.Enabled,
                    config.SettingsFor(mouseForButtons.Name).SmartShiftThreshold ?? 10), shift.Enabled));
                menu.Items.Add(wheel);
            }
        }

        menu.Items.Add(new Separator());
        if (UpdateManager.Available is { } update && !UpdateManager.Busy)
            menu.Items.Add(Ui.MenuItem($"Install update {update.Version.ToString(3)}", () => _ = UpdateManager.InstallAsync()));
        menu.Items.Add(Ui.MenuItem("Open OpenPointer", App.ShowMainWindow));
        menu.Items.Add(Ui.MenuItem("Pause remapping", () => devices.Paused = !devices.Paused, devices.Paused));
        menu.Items.Add(Ui.MenuItem("Start with Windows", () => Autostart.Set(!Autostart.IsEnabled(), Services.ExecutablePath), Autostart.IsEnabled()));
        if (OptionsPlus.IsRunning())
            menu.Items.Add(Ui.MenuItem("Stop Logi Options+", () => App.StopOptionsPlus(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.MenuItem("Quit", App.Quit));
    }

    private static async Task SetWheel(ConnectedMouse mouse, WheelRatchetMode mode)
    {
        if (mouse.SmartShift?.Mode == mode) return;
        await Services.Devices.ToggleWheelModeAsync(mouse);
    }

    /// <summary>Changes one button for the given app, creating its profile on first use.</summary>
    public static void AssignForApp(FocusContext app, string button, ActionSpec? action)
    {
        Services.Config.Update(config =>
        {
            var profile = ContextMatcher.FindAppProfile(config, app);
            if (profile == null)
            {
                if (action == null) return;
                profile = new Profile
                {
                    Name = app.IsDesktop ? "Desktop" : app.IsTaskbar ? "Taskbar" : FriendlyProcessName(app.ProcessName),
                    Match = [app.MatchKey],
                };
                config.Profiles.Add(profile);
            }

            if (action == null) profile.Buttons.Remove(button);
            else profile.Buttons[button] = action;
        });
    }

    private static string FriendlyProcessName(string process)
    {
        var name = Path.GetFileNameWithoutExtension(process);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : process;
    }

    private static UIElement Header(string title, string subtitle, int? battery)
    {
        var stack = new StackPanel { Margin = new Thickness(-22, 6, 0, 6), MinWidth = 220 };
        stack.Children.Add(Ui.Text(title, "Label"));
        var caption = Ui.Text(subtitle, "Caption");
        caption.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(caption);
        if (battery is { } percent)
        {
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0) };
            track.SetResourceReference(Border.BackgroundProperty, "AccentStrong");
            var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "Foreground");
            track.Child = fill;
            track.SizeChanged += (_, _) => fill.Width = Math.Max(4, track.ActualWidth * Math.Clamp(percent, 0, 100) / 100.0);
            stack.Children.Add(track);
        }

        return stack;
    }

    /// <summary>Draws a monochrome mouse glyph that matches the taskbar theme.</summary>
    private static Icon Draw(bool lightTaskbar, bool connected, bool paused, bool lowBattery)
    {
        var size = Forms.SystemInformation.SmallIconSize.Width <= 16 ? 32 : Forms.SystemInformation.SmallIconSize.Width * 2;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var scale = size / 32f;
            var ink = lightTaskbar ? Color.FromArgb(24, 24, 27) : Color.FromArgb(250, 250, 250);
            if (!connected) ink = Color.FromArgb(120, ink);
            using var pen = new Pen(ink, 2.4f * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

            var body = new RectangleF(8.5f * scale, 3f * scale, 15f * scale, 25f * scale);
            using (var path = RoundedRect(body, 7.5f * scale)) g.DrawPath(pen, path);
            g.DrawLine(pen, 16f * scale, 8f * scale, 16f * scale, 12.5f * scale);

            if (paused || lowBattery)
            {
                var badge = new RectangleF(19f * scale, 19f * scale, 12f * scale, 12f * scale);
                using var clear = new SolidBrush(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillEllipse(clear, RectangleF.Inflate(badge, 2f * scale, 2f * scale));
                g.CompositingMode = CompositingMode.SourceOver;
                using var fill = new SolidBrush(ink);
                if (lowBattery)
                {
                    g.FillEllipse(fill, badge);
                }
                else
                {
                    g.FillRectangle(fill, 21.5f * scale, 20.5f * scale, 2.6f * scale, 9f * scale);
                    g.FillRectangle(fill, 26f * scale, 20.5f * scale, 2.6f * scale, 9f * scale);
                }
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _anchor.Close();
        if (_current != null) DestroyIcon(_current.Handle);
    }
}
