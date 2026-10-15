using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenPointer.Platform;

namespace OpenPointer.App.Views;

/// <summary>
/// Cursor styles: Windows default plus every bundled pack, a preview of all 17 cursor types, and
/// options. Choices are drafted and applied with the Save bar.
/// </summary>
public sealed class CursorsPage(MainWindow window) : IPage
{
    private static readonly string[] StripRoles = ["Arrow", "IBeam", "Hand", "AppStarting"];

    public FrameworkElement Render()
    {
        var draft = Draft.Current.General;
        var applied = Services.Config.Current.General.CursorTheme;
        var selected = draft.CursorTheme == CursorManager.Unchanged ? null : draft.CursorTheme;
        var pack = selected != null ? CursorManager.Find(selected) : null;

        var page = new StackPanel();
        page.Children.Add(PageHeader.Create("Cursors", "Pick a pointer style for all of Windows. Preview it here, then save to apply."));

        // Style cards
        page.Children.Add(Ui.Section("Style"));
        var cards = new WrapPanel { Margin = new Thickness(0, 0, -12, 12) };
        cards.Children.Add(StyleCard(CursorManager.Windows, "Windows", "The standard Windows pointers", null, selected, applied));
        foreach (var p in CursorManager.Packs)
            cards.Children.Add(StyleCard(p.Id, p.Name, p.Description ?? "", p, selected, applied));
        page.Children.Add(cards);

        // Options for the selected pack
        if (pack != null && (pack.HasShadowOption || pack.SizeKeys.Count > 1))
        {
            page.Children.Add(Ui.Section("Options"));
            var rows = new List<UIElement>();
            if (pack.HasShadowOption)
                rows.Add(Ui.Row("Shadow", "Soft drop shadow under the pointer",
                    Ui.Switch(draft.CursorShadow, on => Draft.Edit(c => c.General.CursorShadow = on))));
            if (pack.SizeKeys.Count > 1)
            {
                var labels = new Dictionary<string, string> { ["normal"] = "Normal", ["large"] = "Large", ["xlarge"] = "Extra large" };
                var current = pack.SizeKeys.Contains(draft.CursorSize) ? draft.CursorSize : pack.SizeKeys[0];
                rows.Add(Ui.Row("Size", "Larger sizes are easier to see on high-resolution screens",
                    Segments("cursor-size", [.. pack.SizeKeys.Select(k => (labels[k], k))], current, v => Draft.Edit(c => c.General.CursorSize = v))));
            }

            page.Children.Add(Ui.Card(rows));
        }

        // Every cursor type
        var previewName = pack?.Name ?? "Windows";
        page.Children.Add(Ui.Section($"{previewName} cursors", selected == null
            ? "Your current cursors are not managed by OpenPointer. Pick a style above to preview it."
            : "How each pointer looks. Animated ones play as they will on screen."));
        if (selected != null)
        {
            var grid = new WrapPanel { Margin = new Thickness(0, 0, -12, 12) };
            foreach (var role in CursorThemes.Roles)
                grid.Children.Add(RoleTile(role, FileFor(pack, role, draft.CursorShadow, draft.CursorSize)));
            page.Children.Add(grid);
        }

        // Attribution and restore
        var footer = new List<UIElement>();
        if (pack != null)
        {
            var source = pack.Source;
            footer.Add(Ui.Row("Credits", $"{pack.Name} {pack.Attribution}".Trim(),
                source == null ? null : Ui.Button("Source", () =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source) { UseShellExecute = true })?.Dispose())));
        }

        if (CursorThemes.HasBackup)
        {
            footer.Add(Ui.Row("Original cursors", "Put back the cursors you had before OpenPointer changed them",
                Ui.Button("Restore", () =>
                {
                    if (!Dialogs.Confirm(window, "Restore your original cursors?", "OpenPointer stops managing your cursors.", "Restore")) return;
                    CursorManager.Restore();
                    Services.Config.Update(c => c.General.CursorTheme = CursorManager.Unchanged);
                    Draft.Discard();
                })));
        }

        if (footer.Count > 0) page.Children.Add(Ui.Card(footer));
        return page;
    }

    private static string? FileFor(CursorPack? pack, string role, bool shadow, string size)
    {
        if (pack == null) return CursorThemes.DefaultFile(role);
        var sizeKey = pack.SizeKeys.Contains(size) ? size : pack.SizeKeys.FirstOrDefault() ?? "normal";
        return pack.Roles.TryGetValue(role, out var file)
            ? Path.Combine(pack.VariantFolder(shadow, sizeKey), file)
            : CursorThemes.DefaultFile(role);
    }

    private FrameworkElement StyleCard(string id, string name, string description, CursorPack? pack, string? selected, string applied)
    {
        var draft = Draft.Current.General;
        var isSelected = selected == id;
        var isApplied = applied == id;

        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var role in StripRoles)
        {
            var image = CursorPreview.Create(role, FileFor(pack, role, draft.CursorShadow, draft.CursorSize), 32, Color("Muted"), Scale());
            image.Margin = new Thickness(8, 0, 8, 0);
            strip.Children.Add(image);
        }

        var stage = new Border { Height = 76, CornerRadius = new CornerRadius(6), Child = strip };
        stage.SetResourceReference(Border.BackgroundProperty, "Muted");

        var title = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(Ui.Text(name, "Label"));
        if (isApplied)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Child = Ui.Text("In use", "Caption", 11) };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentStrong");
            Grid.SetColumn(badge, 1);
            title.Children.Add(badge);
        }

        var caption = Ui.Text(pack != null ? $"{description} · {pack.Attribution}" : description, "Caption");
        caption.Margin = new Thickness(0, 2, 0, 0);

        var body = new StackPanel();
        body.Children.Add(stage);
        body.Children.Add(title);
        body.Children.Add(caption);

        var card = new Border
        {
            Style = Ui.Style("Card"),
            Width = 300,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 12, 12),
            Child = body,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
        };
        card.SetResourceReference(Border.BorderBrushProperty, isSelected ? "Foreground" : "Border");
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!isSelected) Draft.Edit(c => c.General.CursorTheme = id);
        };
        return card;
    }

    private Border RoleTile(string role, string? file)
    {
        var stage = new Border { Height = 72, CornerRadius = new CornerRadius(6) };
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
        return new Border { Width = 132, Margin = new Thickness(0, 0, 12, 12), Child = body };
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

    private static System.Windows.Media.Color Color(string key) =>
        ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private double Scale() => VisualTreeHelper.GetDpi(window).DpiScaleX;
}
