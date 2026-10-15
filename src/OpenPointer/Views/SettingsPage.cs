using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.App.Views;

public sealed class SettingsPage(MainWindow window) : IPage
{
    public FrameworkElement Render()
    {
        var config = Services.Config.Current;
        var page = new StackPanel();
        page.Children.Add(PageHeader.Create("Settings", "How OpenPointer runs on this PC."));

        page.Children.Add(Ui.Section("General"));
        var theme = new Border { Style = Ui.Style("SegmentGroup") };
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new[] { ("System", "system"), ("Light", "light"), ("Dark", "dark") })
        {
            var segment = new RadioButton { Style = Ui.Style("Segment"), Content = label, GroupName = "theme", IsChecked = config.General.Theme == value };
            segment.Checked += (_, _) =>
            {
                Services.Config.Update(c => c.General.Theme = value);
                ThemeManager.Apply(value);
            };
            themes.Children.Add(segment);
        }

        theme.Child = themes;
        page.Children.Add(Ui.Card(
        [
            Ui.Row("Appearance", null, theme),
            Ui.Row("Start with Windows", "Runs quietly in the tray after you sign in",
                Ui.Switch(Autostart.IsEnabled(), on => Autostart.Set(on, Services.ExecutablePath))),
            Ui.Row("Pause remapping", "Hand every button back to the mouse until you turn this off",
                Ui.Switch(Services.Devices.Paused, on => Services.Devices.Paused = on)),
        ]));

        page.Children.Add(Ui.Section("Logi Options+"));
        var running = OptionsPlus.IsRunning();
        UIElement status = running
            ? Ui.Button("Stop Options+", () =>
            {
                App.StopOptionsPlus(window);
                window.Render();
            })
            : Ui.Text("Not running", "MutedText");
        page.Children.Add(Ui.Card(
        [
            Ui.Row("Status", running ? "Running. It competes with OpenPointer for your buttons." : "OpenPointer has full control of your buttons.", status),
            Ui.Row("Warn when it is running", null,
                Ui.Switch(config.General.WarnAboutOptionsPlus, on => Services.Config.Update(c => c.General.WarnAboutOptionsPlus = on))),
        ]));

        page.Children.Add(Ui.Section("Updates"));
        var updateAction = UpdateManager.Available is { } update
            ? Ui.Button($"Install {update.Version.ToString(3)}", () => _ = UpdateManager.InstallAsync(), "PrimaryButton")
            : Ui.Button("Check now", () => _ = UpdateManager.CheckAsync(userInitiated: true));
        updateAction.IsEnabled = !UpdateManager.Busy;
        var updateStatus = UpdateManager.Status.Length > 0 ? UpdateManager.Status : "Updates come from GitHub Releases and are checksum-verified.";
        page.Children.Add(Ui.Card(
        [
            Ui.Row($"Version {UpdateManager.Current.ToString(3)}", updateStatus, updateAction),
            Ui.Row("Check automatically", "Look for a new version every few hours",
                Ui.Switch(config.General.CheckForUpdates, on => Services.Config.Update(c => c.General.CheckForUpdates = on))),
        ]));

        page.Children.Add(Ui.Section("Configuration", "Everything is stored in one JSON file. Edits are picked up as soon as you save."));
        var path = Ui.Text(Services.Config.FilePath, "Caption");
        path.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal };
        fileButtons.Children.Add(Ui.Button("Open", () => Open(Services.Config.FilePath)));
        var folder = Ui.Button("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{Services.Config.FilePath}\""));
        folder.Margin = new Thickness(8, 0, 0, 0);
        fileButtons.Children.Add(folder);
        var configRow = Ui.Row("config.json", null, fileButtons);
        ((StackPanel)configRow.Children[0]).Children.Add(path);

        page.Children.Add(Ui.Card(
        [
            configRow,
            Ui.Row("Backup", "Save all profiles and settings to a file, or restore them", BackupButtons()),
            Ui.Row("Log", "Useful when something does not work",
                Ui.Button("Open log", () => Open(Path.Combine(Path.GetDirectoryName(Services.Config.FilePath)!, "openpointer.log")))),
            Ui.Row("Reset", "Restore the default profiles and device settings",
                Ui.Button("Reset…", () =>
                {
                    if (!Dialogs.Confirm(window, "Reset all settings?", "Every profile and button change is removed. This cannot be undone.", "Reset"))
                        return;
                    var defaults = AppConfig.CreateDefault();
                    Services.Config.Update(c =>
                    {
                        c.Profiles = defaults.Profiles;
                        c.Devices = defaults.Devices;
                    });
                })),
        ]));

        page.Children.Add(Ui.Section("About"));
        var about = Ui.Text(
            "OpenPointer is free software under the MIT license. It is not affiliated with Logitech. " +
            "Logitech, Logi, MX Master and Options+ are trademarks of Logitech. Geist font under the SIL Open Font License.", "Caption");
        about.TextWrapping = TextWrapping.Wrap;
        about.TextTrimming = TextTrimming.None;
        page.Children.Add(about);
        return page;
    }

    private StackPanel BackupButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Ui.Button("Export…", () =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { FileName = "openpointer-settings.json", Filter = "OpenPointer settings|*.json" };
            if (picker.ShowDialog(window) == true) File.WriteAllText(picker.FileName, ConfigStore.Serialize(Services.Config.Current));
        }));
        var import = Ui.Button("Import…", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "OpenPointer settings|*.json|All files|*.*" };
            if (picker.ShowDialog(window) != true) return;
            AppConfig imported;
            try { imported = ConfigStore.Parse(File.ReadAllText(picker.FileName)); }
            catch (Exception ex)
            {
                Dialogs.Confirm(window, "Could not import", $"The file is not valid OpenPointer settings. {ex.Message}", "OK");
                return;
            }

            if (!Dialogs.Confirm(window, "Replace your settings?", $"Profiles and device settings are replaced with the {imported.Profiles.Count} profiles in this file.", "Import"))
                return;
            Services.Config.Update(c =>
            {
                c.Profiles = imported.Profiles;
                c.Devices = imported.Devices;
            });
            Draft.Discard();
        });
        import.Margin = new Thickness(8, 0, 0, 0);
        panel.Children.Add(import);
        return panel;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Could not open {path}: {ex.Message}"); }
    }
}
