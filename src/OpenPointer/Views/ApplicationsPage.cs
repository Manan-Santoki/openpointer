using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenPointer.Config;

namespace OpenPointer.App.Views;

/// <summary>App profiles: which apps get their own button layout, DPI, and on/off state.</summary>
public sealed class ApplicationsPage(MainWindow window) : IPage
{
    public FrameworkElement Render()
    {
        var config = Draft.Current;
        var page = new StackPanel();
        var add = Ui.Button("Add application", () =>
        {
            var index = AddApplication(window);
            if (index != null) window.ShowButtonsFor(index.Value);
        }, "PrimaryButton");
        page.Children.Add(PageHeader.Create("Applications", "A profile turns on automatically when its app is in focus.", add));

        var rows = new List<UIElement>();
        for (var i = 0; i < config.Profiles.Count; i++) rows.Add(ProfileRow(config, i));
        page.Children.Add(Ui.Card(rows));

        if (config.Profiles.Count == 1)
        {
            var hint = Ui.Text("Add Chrome, File Explorer, the Desktop or any other app to give it its own buttons.", "Caption");
            hint.TextWrapping = TextWrapping.Wrap;
            page.Children.Add(hint);
        }

        return page;
    }

    private Grid ProfileRow(AppConfig config, int index)
    {
        var profile = config.Profiles[index];
        var isDefault = index == 0;

        var grid = new Grid { MinHeight = 64, Margin = new Thickness(16, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(ProfileIcon(profile, isDefault));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        text.Children.Add(Ui.Text(isDefault ? "All applications" : profile.Name, "Label"));
        var parts = new List<string>();
        if (isDefault) parts.Add("Used when no app profile matches");
        else parts.Add(string.Join(", ", profile.Match.Select(m => m switch { ContextMatcher.Desktop => "Desktop", ContextMatcher.Taskbar => "Taskbar", _ => m })));
        var customized = profile.Buttons.Count + (profile.ThumbWheelLeft != null || profile.ThumbWheelRight != null ? 1 : 0);
        if (!isDefault) parts.Add(customized == 0 ? "No changes yet" : customized == 1 ? "1 change" : $"{customized} changes");
        if (profile.Dpi != null) parts.Add($"{profile.Dpi} DPI");
        var caption = Ui.Text(string.Join("  ·  ", parts), "Caption");
        caption.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(caption);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!isDefault)
        {
            var toggle = Ui.Switch(profile.Enabled, on => Draft.Edit(c => c.Profiles[index].Enabled = on, rerender: false));
            toggle.Margin = new Thickness(0, 0, 12, 0);
            toggle.ToolTip = profile.Enabled ? "Profile is on" : "Profile is off";
            actions.Children.Add(toggle);
        }

        actions.Children.Add(Ui.Button("Edit buttons", () => window.ShowButtonsFor(index)));
        var more = new Button { Style = Ui.Style("IconButton"), Content = Ui.Icon("", 14, "MutedForeground"), Margin = new Thickness(4, 0, 0, 0), ToolTip = "More" };
        more.Click += (_, _) => MoreMenu(more, index, isDefault).IsOpen = true;
        actions.Children.Add(more);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private ContextMenu MoreMenu(FrameworkElement anchor, int index, bool isDefault)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var profile = Draft.Current.Profiles[index];
        if (!isDefault)
        {
            menu.Items.Add(Ui.MenuItem("Rename…", () =>
            {
                var name = Dialogs.AskText(window, "Rename profile", "Shown in the tray menu and here.", profile.Name);
                if (name != null) Draft.Edit(c => c.Profiles[index].Name = name.Trim());
            }));
        }

        menu.Items.Add(Ui.MenuItem("Pointer speed…", () =>
        {
            var text = Dialogs.AskText(window, "Pointer speed for this profile",
                "DPI to use while this profile is active. Leave empty to use the device setting.", profile.Dpi?.ToString() ?? "");
            if (text == null) return;
            var dpi = int.TryParse(text.Trim(), out var value) && value > 0 ? value : (int?)null;
            Draft.Edit(c => c.Profiles[index].Dpi = dpi);
        }, hint: profile.Dpi != null ? $"{profile.Dpi}" : null));

        if (profile.Dpi != null)
            menu.Items.Add(Ui.MenuItem("Use device pointer speed", () => Draft.Edit(c => c.Profiles[index].Dpi = null)));

        if (!isDefault)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.MenuItem("Remove", () =>
            {
                if (Dialogs.Confirm(window, $"Remove {profile.Name}?", "Its buttons go back to the All applications settings.", "Remove"))
                    Draft.Edit(c => c.Profiles.RemoveAt(index));
            }));
        }

        return menu;
    }

    private static FrameworkElement ProfileIcon(Profile profile, bool isDefault)
    {
        var icon = isDefault ? null : Ui.ExecutableIcon(profile.ExecutablePath);
        FrameworkElement content = icon != null
            ? new Image { Source = icon, Width = 20, Height = 20 }
            : Ui.Icon(isDefault ? "" : profile.Match.Contains(ContextMatcher.Desktop) ? ""
                : profile.Match.Contains(ContextMatcher.Taskbar) ? "" : "", 14);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Child = content, HorizontalAlignment = HorizontalAlignment.Left };
        tile.SetResourceReference(Border.BackgroundProperty, "Muted");
        return tile;
    }

    /// <summary>Asks for an app and creates its profile. Returns the new (or existing) profile index.</summary>
    public static int? AddApplication(Window owner)
    {
        var existing = Draft.Current.Profiles.Skip(1).SelectMany(p => p.Match).ToList();
        var choice = Dialogs.AskApplication(owner, existing);
        if (choice == null) return null;
        var index = -1;
        Draft.Edit(c =>
        {
            c.Profiles.Add(new Profile { Name = choice.Name, Match = [choice.Match], ExecutablePath = choice.ExecutablePath });
            index = c.Profiles.Count - 1;
        });
        return index;
    }
}
