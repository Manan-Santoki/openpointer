using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Hidpp;

namespace OpenPointer.App.Views;

/// <summary>DPI, wheel and Easy-Switch settings plus device details.</summary>
public sealed class PointerPage(MainWindow window) : IPage
{
    private readonly DispatcherTimer _dpiCommit = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _pendingDpi;

    public FrameworkElement Render()
    {
        var page = new StackPanel();
        page.Children.Add(PageHeader.Create("Pointer & scrolling", "Speed and wheel behavior. Stored on this PC and re-applied when the mouse reconnects."));
        var mouse = Services.SelectedMouse;
        if (mouse == null)
        {
            page.Children.Add(EmptyState.Create("", "No mouse connected", "Turn on your mouse, or plug in its receiver."));
            page.Children.Add(new Border { Height = 24 });
            AddCursorSection(page);
            return page;
        }

        if (mouse.Dpi is { } dpi)
        {
            page.Children.Add(Ui.Section("Pointer speed"));
            page.Children.Add(DpiCard(mouse, dpi));
        }

        var settings = Services.Config.Current.SettingsFor(mouse.Name);
        var wheelRows = new List<UIElement>();

        // Windows-wide, like Options+ "Scrolling speed".
        var lines = new Slider { Minimum = 1, Maximum = 12, Value = Math.Clamp(Platform.SystemMouse.ScrollLines, 1, 12), Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1 };
        var linesValue = Ui.Text($"{(int)lines.Value}", "MutedText");
        linesValue.Width = 24;
        linesValue.TextAlignment = TextAlignment.Right;
        lines.ValueChanged += (_, e) => linesValue.Text = $"{(int)e.NewValue}";
        lines.LostMouseCapture += (_, _) => Platform.SystemMouse.ScrollLines = (int)lines.Value;
        lines.KeyUp += (_, _) => Platform.SystemMouse.ScrollLines = (int)lines.Value;
        var speed = new StackPanel { Orientation = Orientation.Horizontal };
        speed.Children.Add(lines);
        speed.Children.Add(linesValue);
        wheelRows.Add(Ui.Row("Scroll speed", "Lines per notch, for every mouse on this PC", speed));

        if (mouse.WheelFlags is { } flags)
        {
            wheelRows.Add(Ui.Row("Smooth scrolling", "High-resolution wheel reporting for finer, smoother scrolling",
                Ui.Switch((flags & 0x02) != 0, on => UpdateDevice(mouse, s => s.SmoothScroll = on))));
            wheelRows.Add(Ui.Row("Natural scrolling", "Reverse the scroll direction",
                Ui.Switch((flags & 0x04) != 0, on => UpdateDevice(mouse, s => s.InvertScroll = on))));
        }

        if (mouse.SmartShift is { } shift)
        {
            var mode = new Border { Style = Ui.Style("SegmentGroup") };
            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, value) in new[] { ("Ratchet", WheelRatchetMode.Ratchet), ("Free spin", WheelRatchetMode.Freespin) })
            {
                var segment = new RadioButton { Style = Ui.Style("Segment"), Content = label, GroupName = "wheel-" + mouse.Name, IsChecked = shift.Mode == value };
                segment.Checked += async (_, _) =>
                {
                    if (mouse.SmartShift?.Mode != value) await Services.Devices.ToggleWheelModeAsync(mouse);
                };
                modes.Children.Add(segment);
            }

            mode.Child = modes;
            wheelRows.Add(Ui.Row("Wheel mode", "How the wheel feels when SmartShift is off", mode));
            wheelRows.Add(Ui.Row("SmartShift", "Switch to free spin automatically when you flick the wheel",
                Ui.Switch(shift.Enabled, on => _ = Services.Devices.SetSmartShiftAsync(mouse, on, settings.SmartShiftThreshold ?? 10))));
            if (shift.Enabled)
            {
                var slider = new Slider { Minimum = 1, Maximum = 50, Value = Math.Clamp(shift.AutoDisengage, 1, 50), Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1 };
                slider.LostMouseCapture += (_, _) => _ = Services.Devices.SetSmartShiftAsync(mouse, true, (int)slider.Value);
                wheelRows.Add(Ui.Row("Sensitivity", "Lower switches to free spin more easily", slider));
            }
        }

        if (mouse.HasThumbWheel)
        {
            wheelRows.Add(Ui.Row("Invert thumb wheel", null,
                Ui.Switch(settings.InvertThumbWheel ?? false, on => UpdateDevice(mouse, s => s.InvertThumbWheel = on))));
        }

        if (wheelRows.Count > 0)
        {
            page.Children.Add(Ui.Section("Scrolling"));
            page.Children.Add(Ui.Card(wheelRows));
        }

        AddCursorSection(page);

        if (mouse.Hosts is { Count: > 1 } hosts)
        {
            page.Children.Add(Ui.Section("Easy-Switch", "Move the mouse to another paired computer. It disconnects from this PC."));
            var group = new Border { Style = Ui.Style("SegmentGroup") };
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < hosts.Count; i++)
            {
                var host = i + 1;
                var segment = new RadioButton { Style = Ui.Style("Segment"), Content = $"Computer {host}", GroupName = "host", IsChecked = i == hosts.Current };
                segment.Click += (_, _) =>
                {
                    if (host - 1 == hosts.Current) return;
                    if (Dialogs.Confirm(window, $"Switch to computer {host}?", "The mouse will stop working on this PC until you switch back.", "Switch"))
                        _ = Services.Devices.SwitchHostAsync(mouse, host);
                    else window.Render();
                };
                segments.Children.Add(segment);
            }

            group.Child = segments;
            page.Children.Add(Ui.Card([Ui.Row("Channel", null, group)]));
        }

        page.Children.Add(Ui.Section("Device"));
        var device = mouse.Device;
        var connection = device.Device.Transport.IsReceiver
            ? $"{ReceiverName(device.Device.Transport.ProductId)} · slot {device.Device.DeviceIndex}"
            : "Direct (Bluetooth or USB)";
        page.Children.Add(Ui.Card(
        [
            Ui.Row("Model", null, Value(mouse.Name)),
            Ui.Row("Connection", null, Value(connection)),
            Ui.Row("Battery", null, Value(mouse.Battery is { } b ? $"{b.Percent}% · {b.Status}" : "Unknown")),
            Ui.Row("Protocol", null, Value(device.Protocol != null ? $"HID++ {device.Protocol}" : "Unknown")),
        ]));
        return page;
    }

    private void AddCursorSection(StackPanel page)
    {
        var general = Services.Config.Current.General;
        page.Children.Add(Ui.Section("Cursor", "Changes the pointer for all of Windows."));

        var styles = new List<(string Label, string Value)> { ("Windows", CursorManager.Windows) };
        styles.AddRange(CursorManager.Packs.Select(p => (p.Name, p.Id)));
        var pack = CursorManager.Find(general.CursorTheme);
        var styleCaption = general.CursorTheme == CursorManager.Unchanged ? "Not changed by OpenPointer"
            : pack != null ? $"{pack.Description ?? pack.Name} · {pack.Attribution}" : null;
        var rows = new List<UIElement>
        {
            Ui.Row("Style", styleCaption, Segments("cursor-style", [.. styles], general.CursorTheme, value => UpdateCursor(g => g.CursorTheme = value))),
        };

        if (pack != null)
        {
            if (pack.HasShadowOption)
                rows.Add(Ui.Row("Shadow", "Soft drop shadow under the pointer",
                    Ui.Switch(general.CursorShadow, on => UpdateCursor(g => g.CursorShadow = on))));
            if (pack.SizeKeys.Count > 1)
            {
                var labels = new Dictionary<string, string> { ["normal"] = "Normal", ["large"] = "Large", ["xlarge"] = "Extra large" };
                var current = pack.SizeKeys.Contains(general.CursorSize) ? general.CursorSize : pack.SizeKeys[0];
                rows.Add(Ui.Row("Size", null, Segments("cursor-size", [.. pack.SizeKeys.Select(k => (labels[k], k))], current,
                    value => UpdateCursor(g => g.CursorSize = value))));
            }

            if (pack.Source != null)
                rows.Add(Ui.Row("Source", pack.Source, Ui.Button("Open", () =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pack.Source) { UseShellExecute = true })?.Dispose())));
        }

        if (Platform.CursorThemes.HasBackup)
        {
            rows.Add(Ui.Row("Original cursors", "Put back the cursors you had before OpenPointer changed them",
                Ui.Button("Restore", () =>
                {
                    CursorManager.Restore();
                    Services.Config.Update(c => c.General.CursorTheme = CursorManager.Unchanged);
                })));
        }

        page.Children.Add(Ui.Card(rows));
    }

    private static void UpdateCursor(Action<GeneralSettings> change)
    {
        Services.Config.Update(c => change(c.General));
        CursorManager.Apply(Services.Config.Current.General);
    }

    private static Border Segments(string group, (string Label, string Value)[] options, string current, Action<string> pick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var segment = new RadioButton { Style = Ui.Style("Segment"), Content = label, GroupName = group, IsChecked = current == value };
            segment.Checked += (_, _) =>
            {
                if (current != value) pick(value);
            };
            panel.Children.Add(segment);
        }

        return new Border { Style = Ui.Style("SegmentGroup"), Child = panel };
    }

    private Border DpiCard(ConnectedMouse mouse, DpiInfo dpi)
    {
        var value = new TextBlock
        {
            Text = dpi.Current.ToString(),
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Foreground");
        var unit = Ui.Text("DPI", "MutedText");
        unit.Margin = new Thickness(8, 0, 0, 5);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        var range = Ui.Text(dpi.Supported.Count > 0 ? $"{dpi.Supported[0]}–{dpi.Supported[^1]}" : "", "Caption");
        range.HorizontalAlignment = HorizontalAlignment.Right;
        range.VerticalAlignment = VerticalAlignment.Bottom;
        range.Margin = new Thickness(0, 0, 0, 6);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(value);
        Grid.SetColumn(unit, 1);
        top.Children.Add(unit);
        Grid.SetColumn(range, 2);
        top.Children.Add(range);

        var steps = dpi.Supported.Count > 0 ? dpi.Supported : [dpi.Current];
        var index = Math.Max(0, IndexOf(steps, dpi.Current));
        var slider = new Slider { Minimum = 0, Maximum = steps.Count - 1, Value = index, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 16, 0, 16) };
        slider.ValueChanged += (_, e) =>
        {
            var chosen = steps[(int)Math.Round(e.NewValue)];
            value.Text = chosen.ToString();
            _pendingDpi = chosen;
            // Committing re-renders the page, so wait until a drag is finished.
            if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
            _dpiCommit.Stop();
            _dpiCommit.Start();
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dpiCommit.Stop();
            _dpiCommit.Start();
        };
        _dpiCommit.Tick -= CommitDpi;
        _dpiCommit.Tick += CommitDpi;
        _commitTarget = mouse;

        var chips = new WrapPanel();
        foreach (var preset in Services.Devices.DpiPresets(mouse))
        {
            var chip = new Button { Style = Ui.Style("Chip"), Content = preset.ToString(), Tag = preset == dpi.Current };
            var target = preset;
            chip.Click += (_, _) => slider.Value = Math.Max(0, IndexOf(steps, target));
            chips.Children.Add(chip);
        }

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(top);
        stack.Children.Add(slider);
        stack.Children.Add(chips);
        return new Border { Style = Ui.Style("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 24) };
    }

    private ConnectedMouse? _commitTarget;

    private void CommitDpi(object? sender, EventArgs e)
    {
        _dpiCommit.Stop();
        if (_commitTarget != null && _pendingDpi > 0) _ = Services.Devices.SetDpiAsync(_commitTarget, _pendingDpi, persist: true);
    }

    private static int IndexOf(IReadOnlyList<int> steps, int value)
    {
        var best = 0;
        for (var i = 1; i < steps.Count; i++)
            if (Math.Abs(steps[i] - value) < Math.Abs(steps[best] - value)) best = i;
        return best;
    }

    private static void UpdateDevice(ConnectedMouse mouse, Action<DeviceSettings> change) =>
        Services.Config.Update(config =>
        {
            if (!config.Devices.TryGetValue(mouse.Name, out var settings)) config.Devices[mouse.Name] = settings = new DeviceSettings();
            change(settings);
        });

    private static TextBlock Value(string text) => Ui.Text(text, "MutedText");

    private static string ReceiverName(int pid) => pid switch
    {
        0xC548 => "Logi Bolt receiver",
        0xC52B or 0xC532 => "Unifying receiver",
        _ when HidppTransport.KnownReceivers.Contains(pid) => "Logitech receiver",
        _ => $"Receiver {pid:X4}",
    };
}
