using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenPointer.Platform;

namespace OpenPointer.App.Views;

/// <summary>
/// Cursor styles as cards. "View pointers" shows every pointer of a style; styles with settings
/// (shadow, size) are configured and applied from an options dialog.
/// </summary>
public sealed class CursorsPage(MainWindow window) : IPage
{
    private static readonly string[] StripRoles = ["Arrow", "IBeam", "Hand", "AppStarting"];

    public FrameworkElement Render()
    {
        // The draft holds the selection; "In use" shows what Windows has right now.
        var general = Draft.Current.General;
        var page = new StackPanel();
        page.Children.Add(PageHeader.Create("Cursors", "Pick a pointer style for all of Windows, then save to apply it."));

        // Two equal columns that fill the page, so the cards line up with the full-width card below.
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -16, 8) };
        cards.Children.Add(StyleCard(null, general));
        foreach (var pack in CursorManager.Packs) cards.Children.Add(StyleCard(pack, general));
        page.Children.Add(cards);

        if (CursorThemes.HasBackup)
        {
            page.Children.Add(Ui.Card(
            [
                Ui.Row("Original cursors", "Put back the cursors you had before OpenPointer changed them",
                    Ui.Button("Restore", () =>
                    {
                        if (!Dialogs.Confirm(window, "Restore your original cursors?", "OpenPointer stops managing your cursors.", "Restore")) return;
                        CursorManager.Restore();
                        Services.Config.Update(c => c.General.CursorTheme = CursorManager.Unchanged);
                    })),
            ]));
        }

        return page;
    }

    private FrameworkElement StyleCard(CursorPack? pack, Config.GeneralSettings general)
    {
        var id = pack?.Id ?? CursorManager.Windows;
        var name = pack?.Name ?? "Windows";
        var selected = general.CursorTheme == id;
        var inUse = Services.Config.Current.General.CursorTheme == id;
        var hasOptions = pack is { HasShadowOption: true } || pack?.SizeKeys.Count > 1;

        var stage = new Border { Height = 84, CornerRadius = new CornerRadius(6), Child = Strip(pack, general.CursorShadow, general.CursorSize) };
        stage.SetResourceReference(Border.BackgroundProperty, "Muted");

        var title = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(Ui.Text(name, "Label"));
        if (inUse)
        {
            var label = Ui.Text("In use", "Label", 11);
            label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryForeground");
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 3), Child = label, VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "Primary");
            Grid.SetColumn(badge, 1);
            title.Children.Add(badge);
        }

        var caption = Ui.Text(pack == null ? "The standard Windows pointers" : $"{pack.Description} · {pack.Attribution}", "Caption");
        caption.Margin = new Thickness(0, 3, 0, 0);
        caption.ToolTip = caption.Text;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var view = SmallButton("View pointers", "", () => ShowPointers(pack, general.CursorShadow, general.CursorSize));
        actions.Children.Add(view);
        if (hasOptions)
        {
            var options = SmallButton("Options", "", () => ShowOptions(pack!));
            options.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(options);
        }

        var body = new StackPanel();
        body.Children.Add(stage);
        body.Children.Add(title);
        body.Children.Add(caption);
        body.Children.Add(actions);

        var card = new Border
        {
            Style = Ui.Style("Card"),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 16, 16),
            Child = body,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(selected ? 2 : 1),
            ToolTip = selected ? null : hasOptions ? $"Set up {name}" : $"Use {name}",
        };
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "Foreground" : "Border");
        card.MouseLeftButtonUp += (_, e) =>
        {
            // Clicks on the card's own buttons are handled by them.
            if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null) return;
            if (hasOptions) ShowOptions(pack!);
            else if (!selected) Apply(id, general.CursorShadow, general.CursorSize);
        };
        return card;
    }

    private static Button SmallButton(string text, string glyph, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon(glyph, 12, "MutedForeground");
        icon.Margin = new Thickness(0, 0, 6, 0);
        content.Children.Add(icon);
        content.Children.Add(Ui.Text(text, "Label", 12));
        var button = new Button { Content = content, Height = 30, Padding = new Thickness(10, 0, 10, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>All 17 pointers of a style in a dialog.</summary>
    internal void ShowPointers(CursorPack? pack, bool shadow, string size)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(-6, -6, -6, 0) };
        foreach (var role in CursorThemes.Roles)
            grid.Children.Add(RoleTile(role, FileFor(pack, role, shadow, size)));

        var name = pack?.Name ?? "Windows";
        var description = pack == null ? "The standard Windows pointers." : $"{pack.Description} · {pack.Attribution}";
        var dialog = new DialogHost($"{name} pointers", description, grid, 720);
        if (pack?.Source is { } source)
            dialog.AddButton("Source", false, () =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source) { UseShellExecute = true })?.Dispose());
        dialog.AddButton("Close", true, dialog.Close);
        dialog.ShowFor(window);
    }

    /// <summary>Shadow and size for a pack, with a live preview; Apply switches Windows to it.</summary>
    private void ShowOptions(CursorPack pack)
    {
        var general = Draft.Current.General;
        var shadow = general.CursorShadow;
        var size = pack.SizeKeys.Contains(general.CursorSize) ? general.CursorSize : pack.SizeKeys.FirstOrDefault() ?? "normal";

        var preview = new Border { Height = 92, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 16) };
        preview.SetResourceReference(Border.BackgroundProperty, "Muted");
        void Refresh() => preview.Child = Strip(pack, shadow, size, 36);
        Refresh();

        var rows = new List<UIElement>();
        if (pack.HasShadowOption)
            rows.Add(Ui.Row("Shadow", "Soft drop shadow under the pointer", Ui.Switch(shadow, on =>
            {
                shadow = on;
                Refresh();
            })));
        if (pack.SizeKeys.Count > 1)
        {
            var labels = new Dictionary<string, string> { ["normal"] = "Normal", ["large"] = "Large", ["xlarge"] = "Extra large" };
            rows.Add(Ui.Row("Size", "Larger is easier to see on high-resolution screens",
                Segments($"size-{pack.Id}", [.. pack.SizeKeys.Select(k => (labels[k], k))], size, value =>
                {
                    size = value;
                    Refresh();
                })));
        }

        var card = Ui.Card(rows);
        card.Margin = new Thickness(0);
        var body = new StackPanel();
        body.Children.Add(preview);
        body.Children.Add(card);

        var selected = general.CursorTheme == pack.Id;
        var dialog = new DialogHost($"{pack.Name} options", $"{pack.Description} · {pack.Attribution}", body, 560);
        dialog.AddButton("Cancel", false, dialog.Close);
        dialog.AddButton(selected ? "Done" : $"Use {pack.Name}", true, () =>
        {
            Apply(pack.Id, shadow, size);
            dialog.DialogResult = true;
        });
        dialog.ShowFor(window);
    }

    /// <summary>Switches Windows to a style right away and remembers the choice.</summary>
    /// <summary>Selects a style in the draft; Save switches Windows to it.</summary>
    private static void Apply(string id, bool shadow, string size) => Draft.Edit(c =>
    {
        c.General.CursorTheme = id;
        c.General.CursorShadow = shadow;
        c.General.CursorSize = size;
    });

    private StackPanel Strip(CursorPack? pack, bool shadow, string size, double cursorSize = 32)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var role in StripRoles)
        {
            var image = CursorPreview.Create(role, FileFor(pack, role, shadow, size), cursorSize, Color("Muted"), Scale());
            image.Margin = new Thickness(10, 0, 10, 0);
            strip.Children.Add(image);
        }

        return strip;
    }

    private Border RoleTile(string role, string? file)
    {
        var stage = new Border { Height = 76, CornerRadius = new CornerRadius(6) };
        stage.SetResourceReference(Border.BackgroundProperty, "Muted");
        var image = CursorPreview.Create(role, file, 40, Color("Muted"), Scale());
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
        stage.Child = image;

        var label = Ui.Text(CursorThemes.RoleLabels[role], "Caption");
        label.Margin = new Thickness(2, 8, 0, 0);
        var body = new StackPanel();
        body.Children.Add(stage);
        body.Children.Add(label);
        return new Border { Margin = new Thickness(6, 6, 6, 10), Child = body };
    }

    private static string? FileFor(CursorPack? pack, string role, bool shadow, string size)
    {
        if (pack == null) return CursorThemes.DefaultFile(role);
        var sizeKey = pack.SizeKeys.Contains(size) ? size : pack.SizeKeys.FirstOrDefault() ?? "normal";
        return pack.Roles.TryGetValue(role, out var file)
            ? Path.Combine(pack.VariantFolder(shadow, sizeKey), file)
            : CursorThemes.DefaultFile(role);
    }

    private static Border Segments(string group, (string Label, string Value)[] options, string current, Action<string> pick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in options)
        {
            var segment = new RadioButton { Style = Ui.Style("Segment"), Content = label, GroupName = group, IsChecked = current == value };
            segment.Checked += (_, _) => pick(value);
            panel.Children.Add(segment);
        }

        return new Border { Style = Ui.Style("SegmentGroup"), Child = panel };
    }

    private static T? FindParent<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static System.Windows.Media.Color Color(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private double Scale() => VisualTreeHelper.GetDpi(window).DpiScaleX;
}
