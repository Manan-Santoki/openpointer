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
        page.Children.Add(PageHeader.Create("Buttons", "Choose what each button does. App profiles override All applications."));
        page.Children.Add(ProfileSelector(config, profile));

        if (mouse == null)
        {
            page.Children.Add(EmptyState.Create("", "No mouse connected", "Turn on your mouse, or plug in its receiver. OpenPointer finds it automatically."));
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

            var picker = SelectButton(ActionCatalog.Label(effective), anchor => ActionMenu.ShowDropdown(anchor, effective,
                picked => Assign(button, picked),
                inheritLabel: !isDefault && own != null ? "Use All applications setting" : null,
                allowGestures: mouse.SupportsGestures(button)));
            rows.Add(Ui.Row(ButtonNames.Label(button), caption, picker));

            if (own is GestureAction gesture) rows.Add(GestureRows(button, gesture));
        }

        if (mouse.HasThumbWheel)
        {
            var (left, right) = (profile.ThumbWheelLeft, profile.ThumbWheelRight);
            rows.Add(Ui.Row("Thumb wheel left", "Default scrolls left", SelectButton(left != null ? ActionCatalog.Label(left) : "Scroll left",
                anchor => ActionMenu.ShowDropdown(anchor, left ?? new NativeAction(), a => AssignThumb(left: true, a)))));
            rows.Add(Ui.Row("Thumb wheel right", "Default scrolls right", SelectButton(right != null ? ActionCatalog.Label(right) : "Scroll right",
                anchor => ActionMenu.ShowDropdown(anchor, right ?? new NativeAction(), a => AssignThumb(left: false, a)))));
        }

        page.Children.Add(Ui.Card(rows));
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
        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
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
