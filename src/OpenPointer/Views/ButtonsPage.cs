using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenPointer.Config;
using OpenPointer.Engine;

namespace OpenPointer.App.Views;

/// <summary>Per-profile button mapping, including gesture directions and thumb wheel steps.</summary>
public sealed class ButtonsPage(MainWindow window) : IPage
{
    public int ProfileIndex { get; set; }

    public FrameworkElement Render()
    {
        var config = Draft.Current;
        if (ProfileIndex >= config.Profiles.Count) ProfileIndex = 0;
        var profile = config.Profiles[ProfileIndex];
        var isDefault = ProfileIndex == 0;
        var mouse = Services.SelectedMouse;

        var page = new StackPanel();
        var touchpad = mouse?.WindowsDevice?.IsTouchpad == true;
        page.Children.Add(touchpad
            ? PageHeader.Create("Touchpad gestures", "Give corners, edges, circles and multi-finger gestures an action. Gestures Windows already uses stay with Windows.")
            : PageHeader.Create("Buttons", "Choose what each button does. App profiles override All applications."));
        page.Children.Add(ProfileSelector(config, profile));
        if (touchpad)
        {
            foreach (var element in TouchpadView.Create(config, profile, isDefault, Assign)) page.Children.Add(element);
            var saveNote = Ui.Text("Save to apply your changes. Touchpad gestures follow the same app profiles as buttons.", "Caption");
            saveNote.TextWrapping = TextWrapping.Wrap;
            page.Children.Add(saveNote);
            return page;
        }

        if (mouse == null)
        {
            page.Children.Add(EmptyState.Create("", "No mouse connected", "Connect a mouse by Bluetooth, USB or its wireless receiver. OpenPointer finds it automatically."));
            return page;
        }

        var rows = new List<UIElement>();
        foreach (var button in mouse.Buttons)
        {
            profile.Buttons.TryGetValue(button, out var own);
            var effective = own ?? (isDefault ? new NativeAction() : ContextMatcher.ResolveButton(config, InheritProbe, button));
            var caption = isDefault
                ? Describe(button, effective)
                : own != null ? $"Custom for {profile.Name}" : "Same as All applications";

            var multi = MultiPressRows(config, profile, isDefault, button);
            var picker = SelectButton(ActionCatalog.Label(effective), anchor => ActionMenu.ShowDropdown(anchor, effective,
                picked => Assign(button, picked),
                inheritLabel: !isDefault && own != null ? "Use All applications setting" : null,
                allowGestures: mouse.SupportsGestures(button),
                extend: multi != null ? null : menu =>
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Ui.MenuItem("Double & triple press…", () =>
                    {
                        _expanded.Add(button);
                        window.Render();
                    }, false, "Press 2 or 3 times"));
                }));
            // A button and its nested editors form one row, so no divider separates them.
            var group = new StackPanel();
            group.Children.Add(Ui.Row(ButtonNames.Label(button), caption, picker));
            if (own is GestureAction gesture) group.Children.Add(GestureRows(button, gesture));
            if (own is ActionRingAction ring) group.Children.Add(RingRows(button, ring));
            if (multi != null) group.Children.Add(multi);
            rows.Add(group);
        }

        if (mouse.HasThumbWheel)
        {
            var (left, right) = (profile.ThumbWheelLeft, profile.ThumbWheelRight);
            rows.Add(Ui.Row("Thumb wheel left", "Default scrolls left", SelectButton(left != null ? ActionCatalog.Label(left) : "Scroll left",
                anchor => ActionMenu.ShowDropdown(anchor, left ?? new NativeAction(), a => AssignThumb(left: true, a)))));
            rows.Add(Ui.Row("Thumb wheel right", "Default scrolls right", SelectButton(right != null ? ActionCatalog.Label(right) : "Scroll right",
                anchor => ActionMenu.ShowDropdown(anchor, right ?? new NativeAction(), a => AssignThumb(left: false, a)))));
        }

        if (rows.Count > 0) page.Children.Add(Ui.Card(rows));
        else page.Children.Add(EmptyState.Create("", "No extra buttons", mouse.UnconfirmedButtons.Count > 0
            ? "Press this mouse's side buttons once and they appear here. Left and right click stay native."
            : "This mouse reports no middle or side buttons. Left and right click stay native."));
        if (mouse.IsStandardMouse)
        {
            var pending = mouse.UnconfirmedButtons.Count > 0 && rows.Count > 0
                ? " Side buttons appear here after you press them once, since many mice report buttons they don't have."
                : "";
            var support = Ui.Text(Services.Devices.WindowsRemappingAvailable
                ? "Standard button mappings apply to all mice on this PC. Left and right click stay native." + pending
                : "Windows button remapping could not start. Quit and reopen OpenPointer; device detection and Windows scroll settings are still available.", "Caption");
            support.TextWrapping = TextWrapping.Wrap;
            support.Margin = new Thickness(0, 8, 0, 8);
            page.Children.Add(support);
        }
        var note = Ui.Text(mouse.Online
            ? "Save to apply your changes. Buttons return to normal when OpenPointer quits."
            : $"{mouse.Name} is asleep. Changes apply when it wakes up.", "Caption");
        note.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(note);
        return page;
    }

    /// <summary>A context that matches only the Default profile, for showing inherited values.</summary>
    private static readonly FocusContext InheritProbe = new("\0", "", "");

    private static string Describe(string button, ActionSpec action) => action switch
    {
        NativeAction => button switch
        {
            ButtonNames.Back => "Goes back, as shipped",
            ButtonNames.Forward => "Goes forward, as shipped",
            ButtonNames.Middle => "Middle click, as shipped",
            _ => "Built-in behavior",
        },
        GestureAction => "Hold and move the mouse",
        _ => action.Describe(),
    };

    private FrameworkElement ProfileSelector(AppConfig config, Profile profile)
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = ProfileIndex == 0 ? "All applications" : profile.Name;
        var selector = SelectButton(label, anchor =>
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            for (var i = 0; i < config.Profiles.Count; i++)
            {
                var index = i;
                var p = config.Profiles[i];
                menu.Items.Add(Ui.MenuItem(i == 0 ? "All applications" : p.Name, () =>
                {
                    ProfileIndex = index;
                    window.Render();
                }, i == ProfileIndex, i == 0 ? null : string.Join(", ", p.Match)));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Add application…", () =>
            {
                var index = ApplicationsPage.AddApplication(window);
                if (index != null)
                {
                    ProfileIndex = index.Value;
                    window.Render();
                }
            }));
            menu.IsOpen = true;
        });
        selector.MinWidth = 240;
        bar.Children.Add(selector);
        return bar;
    }

    private FrameworkElement GestureRows(string button, GestureAction gesture)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        var inner = new StackPanel();
        (string Glyph, string Name, ActionSpec? Value, Func<GestureAction, ActionSpec?, GestureAction> With)[] directions =
        [
            ("", "Click", gesture.Tap, (g, a) => g with { Tap = a }),
            ("", "Move up", gesture.Up, (g, a) => g with { Up = a }),
            ("", "Move down", gesture.Down, (g, a) => g with { Down = a }),
            ("", "Move left", gesture.Left, (g, a) => g with { Left = a }),
            ("", "Move right", gesture.Right, (g, a) => g with { Right = a }),
        ];

        foreach (var (glyph, name, value, with) in directions)
        {
            var row = new Grid { Height = 44, Margin = new Thickness(12, 0, 12, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.Icon(glyph, 12, "MutedForeground"));
            var text = Ui.Text(name);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var current = value ?? new NoneAction();
            var picker = SelectButton(ActionCatalog.Label(current), anchor => ActionMenu.ShowDropdown(anchor, current,
                picked => Assign(button, with(gesture, picked is NoneAction ? null : picked)), allowNative: false));
            picker.Height = 30;
            picker.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(picker, 2);
            row.Children.Add(picker);
            if (inner.Children.Count > 0) inner.Children.Add(Ui.Divider());
            inner.Children.Add(row);
        }

        var box = new Border { Style = Ui.Style("Card"), Child = inner };
        box.SetResourceReference(Border.BackgroundProperty, "Muted");
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>Buttons whose double and triple press rows the user opened in this session.</summary>
    private readonly HashSet<string> _expanded = [];

    /// <summary>"Press twice" and "Press three times" rows, or null when neither is set and the user hasn't asked for them.</summary>
    private FrameworkElement? MultiPressRows(AppConfig config, Profile profile, bool isDefault, string button)
    {
        var counts = Enumerable.Range(2, ButtonNames.MaxPresses - 1).Select(count =>
        {
            var key = ButtonNames.Presses(button, count);
            profile.Buttons.TryGetValue(key, out var own);
            var inherited = isDefault ? null : config.DefaultProfile.Buttons.GetValueOrDefault(key);
            return (Count: count, Key: key, Own: own, Effective: own ?? inherited ?? new NativeAction());
        }).ToList();
        if (!_expanded.Contains(button) && counts.All(c => c.Effective is NativeAction)) return null;

        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        var inner = new StackPanel();
        foreach (var (count, key, own, effective) in counts)
        {
            var row = new Grid { Height = 44, Margin = new Thickness(12, 0, 12, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Ui.Text(count == 2 ? "Press twice" : "Press three times");
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            var picker = SelectButton(effective is NativeAction ? "Off" : ActionCatalog.Label(effective), anchor => ActionMenu.ShowDropdown(anchor, effective,
                picked => Assign(key, picked),
                inheritLabel: !isDefault && own != null ? "Use All applications setting" : null,
                nativeLabel: "Off"));
            picker.Height = 30;
            picker.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            if (inner.Children.Count > 0) inner.Children.Add(Ui.Divider());
            inner.Children.Add(row);
        }

        var box = new Border { Style = Ui.Style("Card"), Child = inner };
        box.SetResourceReference(Border.BackgroundProperty, "Muted");
        panel.Children.Add(box);
        var hint = Ui.Text($"A single press now waits {Math.Clamp(config.General.MultiPressMs, 150, 800)} ms to see if another one follows. Change the wait in Settings.", "Caption");
        hint.Margin = new Thickness(2, 8, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(hint);
        return panel;
    }

    private static readonly string[] Directions = ["Top", "Top right", "Right", "Bottom right", "Bottom", "Bottom left", "Left", "Top left"];

    /// <summary>Angle of ring item i in degrees: 0 is straight up, clockwise, as the overlay draws it.</summary>
    private static double RingAngle(int index, int count) => 360.0 * index / count;

    private static string DirectionName(double degrees) => Directions[(int)Math.Round(degrees / 45) % 8];

    /// <summary>A small map of the ring as it appears on screen, with one pill per item.</summary>
    private static (FrameworkElement View, List<Border> Pills) RingMap(ActionRingAction ring)
    {
        const double width = 480, height = 236, rx = 168, ry = 84;
        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) };

        var hub = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        hub.SetResourceReference(Border.BackgroundProperty, "Background");
        hub.SetResourceReference(Border.BorderBrushProperty, "Border");
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, "MutedForeground");
        hub.Child = dot;
        Canvas.SetLeft(hub, width / 2 - 14);
        Canvas.SetTop(hub, height / 2 - 14);
        canvas.Children.Add(hub);

        var pills = new List<Border>();
        for (var i = 0; i < ring.Items.Count; i++)
        {
            var radians = RingAngle(i, ring.Items.Count) * Math.PI / 180;
            var label = Ui.Text(ring.Items[i].Label, "Label", 12);
            var pill = new Border { Child = label, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
            pill.SetResourceReference(Border.BackgroundProperty, "Background");
            pill.SetResourceReference(Border.BorderBrushProperty, "Border");
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(pill, width / 2 + rx * Math.Sin(radians) - pill.DesiredSize.Width / 2);
            Canvas.SetTop(pill, height / 2 - ry * Math.Cos(radians) - pill.DesiredSize.Height / 2);
            canvas.Children.Add(pill);
            pills.Add(pill);
        }

        return (canvas, pills);
    }

    private static void HighlightPill(Border pill, bool on)
    {
        pill.SetResourceReference(Border.BackgroundProperty, on ? "Primary" : "Background");
        pill.SetResourceReference(Border.BorderBrushProperty, on ? "Primary" : "Border");
        ((TextBlock)pill.Child).SetResourceReference(TextBlock.ForegroundProperty, on ? "PrimaryForeground" : "Foreground");
    }

    private FrameworkElement RingRows(string button, ActionRingAction ring)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        var inner = new StackPanel();
        var (map, pills) = RingMap(ring);
        if (ring.Items.Count > 0) inner.Children.Add(map);
        for (var i = 0; i < ring.Items.Count; i++)
        {
            var index = i;
            var item = ring.Items[i];
            var row = new Grid { Height = 44, Margin = new Thickness(12, 0, 6, 0), Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // An arrow pointing where the item sits on the ring.
            var degrees = RingAngle(i, ring.Items.Count);
            var arrow = Ui.Icon("", 12, "MutedForeground");
            arrow.RenderTransformOrigin = new Point(0.5, 0.5);
            arrow.RenderTransform = new RotateTransform(degrees);
            arrow.HorizontalAlignment = HorizontalAlignment.Left;
            arrow.VerticalAlignment = VerticalAlignment.Center;
            arrow.ToolTip = DirectionName(degrees);
            row.Children.Add(arrow);
            row.MouseEnter += (_, _) => HighlightPill(pills[index], true);
            row.MouseLeave += (_, _) => HighlightPill(pills[index], false);

            var name = new Button { Style = Ui.Style("GhostButton"), Content = item.Label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0, 8, 0), FontWeight = FontWeights.Normal, ToolTip = $"{DirectionName(degrees)} · Rename" };
            name.Click += (_, _) =>
            {
                var label = Dialogs.AskText(window, "Rename item", "Shown on the ring.", item.Label);
                if (label != null) Assign(button, WithItem(ring, index, item with { Label = label.Trim() }));
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var picker = SelectButton(ActionCatalog.Label(item.Action), anchor => ActionMenu.ShowDropdown(anchor, item.Action,
                picked => Assign(button, WithItem(ring, index, item with
                {
                    Action = picked ?? new NoneAction(),
                    // Follow the chosen action's name unless the user renamed the item.
                    Label = item.Label == ActionCatalog.Label(item.Action) && picked != null ? ActionCatalog.Label(picked) : item.Label,
                })), allowNative: false));
            picker.Height = 30;
            picker.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(picker, 2);
            row.Children.Add(picker);

            var remove = new Button { Style = Ui.Style("IconButton"), Content = Ui.Icon("", 11, "MutedForeground"), ToolTip = "Remove", Margin = new Thickness(4, 0, 0, 0) };
            remove.Click += (_, _) => Assign(button, ring with { Items = ring.Items.Where((_, n) => n != index).ToList() });
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);

            if (inner.Children.Count > 0) inner.Children.Add(Ui.Divider());
            inner.Children.Add(row);
        }

        if (ring.Items.Count < 8)
        {
            if (inner.Children.Count > 0) inner.Children.Add(Ui.Divider());
            var add = Ui.Button("Add item", () =>
                Assign(button, ring with { Items = [.. ring.Items, new RingItem("Copy", new KeysAction("ctrl+c"))] }), "GhostButton");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(10, 6, 0, 6);
            inner.Children.Add(add);
        }

        var box = new Border { Style = Ui.Style("Card"), Child = inner };
        box.SetResourceReference(Border.BackgroundProperty, "Muted");
        panel.Children.Add(box);
        var hint = Ui.Text("Hold the button, move toward an item and release. Release without moving, or click quickly, to close it without running anything.", "Caption");
        hint.Margin = new Thickness(2, 8, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(hint);
        return panel;
    }

    private static ActionRingAction WithItem(ActionRingAction ring, int index, RingItem item)
    {
        var items = ring.Items.ToList();
        items[index] = item;
        return ring with { Items = items };
    }

    private void Assign(string button, ActionSpec? action)
    {
        var index = ProfileIndex;
        Draft.Edit(config =>
        {
            if (index >= config.Profiles.Count) return;
            var profile = config.Profiles[index];
            if (action == null || (index == 0 && action is NativeAction && button != ButtonNames.Gesture)) profile.Buttons.Remove(button);
            else profile.Buttons[button] = action;
        });
    }

    private void AssignThumb(bool left, ActionSpec? action)
    {
        var index = ProfileIndex;
        var value = action is NativeAction ? null : action;
        Draft.Edit(config =>
        {
            if (index >= config.Profiles.Count) return;
            if (left) config.Profiles[index].ThumbWheelLeft = value;
            else config.Profiles[index].ThumbWheelRight = value;
        });
    }

    public static Button SelectButton(string text, Action<FrameworkElement> open)
    {
        var button = new Button { Style = Ui.Style("SelectButton"), Content = new TextBlock { Text = text, Style = Ui.Style("Text") }, MinWidth = 220 };
        button.Click += (_, _) => open(button);
        return button;
    }
}

public static class PageHeader
{
    public static FrameworkElement Create(string title, string subtitle, UIElement? action = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, "Title"));
        var caption = Ui.Text(subtitle, "MutedText");
        caption.Margin = new Thickness(0, 4, 0, 0);
        caption.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(caption);
        grid.Children.Add(stack);
        if (action is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Top;
            element.Margin = new Thickness(16, 4, 0, 0);
            Grid.SetColumn(element, 1);
            grid.Children.Add(element);
        }

        return grid;
    }
}

public static class EmptyState
{
    public static FrameworkElement Create(string glyph, string title, string description)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 48, 24, 48) };
        var circle = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.Icon(glyph, 18) };
        circle.SetResourceReference(Border.BackgroundProperty, "Muted");
        ((TextBlock)circle.Child).HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(circle);
        var heading = Ui.Text(title, "Heading");
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(heading);
        var caption = Ui.Text(description, "MutedText");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.TextAlignment = TextAlignment.Center;
        caption.TextWrapping = TextWrapping.Wrap;
        caption.MaxWidth = 340;
        caption.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(caption);
        var card = new Border { Style = Ui.Style("Card"), Child = stack };
        card.BorderBrush = new SolidColorBrush(Colors.Transparent);
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        return card;
    }
}
