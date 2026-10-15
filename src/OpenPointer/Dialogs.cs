using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenPointer.Config;

namespace OpenPointer.App;

/// <summary>A borderless modal card with a title, description, body and footer buttons.</summary>
public sealed class DialogHost : Window
{
    private readonly StackPanel _footer;

    public DialogHost(string title, string? description, UIElement body, double width = 440)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = width + 48;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, "Heading", 16));
        if (description != null)
        {
            var caption = Ui.Text(description, "MutedText");
            caption.TextWrapping = TextWrapping.Wrap;
            caption.TextTrimming = TextTrimming.None;
            caption.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(caption);
        }

        if (body is FrameworkElement element) element.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(body);
        _footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        stack.Children.Add(_footer);

        var card = new Border
        {
            Style = Ui.Style("Card"),
            Padding = new Thickness(24),
            Margin = new Thickness(24),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Background");
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or StackPanel or TextBlock) DragMove();
        };
        Content = card;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public Button AddButton(string text, bool primary, Action click)
    {
        var button = Ui.Button(text, click, primary ? "PrimaryButton" : null);
        button.Margin = new Thickness(8, 0, 0, 0);
        button.MinWidth = 80;
        if (primary) button.IsDefault = true;
        _footer.Children.Add(button);
        return button;
    }

    public bool ShowFor(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return ShowDialog() == true;
    }
}

public static class Dialogs
{
    /// <summary>Captures a keyboard shortcut. Returns a chord string such as "ctrl+shift+t", or null.</summary>
    public static string? AskShortcut(Window? owner, string? initial)
    {
        var caps = new WrapPanel { MinHeight = 32 };
        var input = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 12, 0, 0) };
        var hint = Ui.Text("Press the keys, or type a chord like ctrl+shift+t, win+d, media_play_pause.", "Caption");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 8, 0, 0);

        var surface = new Border
        {
            Style = Ui.Style("Card"),
            Height = 72,
            Focusable = true,
            Cursor = Cursors.IBeam,
            Child = caps,
            Padding = new Thickness(16, 0, 16, 0),
            FocusVisualStyle = null,
        };
        caps.VerticalAlignment = VerticalAlignment.Center;
        caps.HorizontalAlignment = HorizontalAlignment.Center;
        surface.SetResourceReference(Border.BackgroundProperty, "Muted");

        void Render(string chord)
        {
            caps.Children.Clear();
            if (string.IsNullOrWhiteSpace(chord))
            {
                caps.Children.Add(Ui.Text("Press a shortcut", "MutedText"));
                return;
            }

            foreach (var key in KeyChord.Format(chord).Split(" + "))
                caps.Children.Add(new Border { Style = Ui.Style("KeyCap"), Child = Ui.Text(key, "Label", 12) });
        }

        Render(input.Text);
        input.TextChanged += (_, _) => Render(input.Text.Trim());
        surface.MouseDown += (_, _) => surface.Focus();
        surface.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Escape) return;
            var parts = new List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("win");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
            if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
            {
                var name = KeyChord.NameOf((ushort)KeyInterop.VirtualKeyFromKey(key));
                if (name != null) parts.Add(name);
            }

            input.Text = string.Join("+", parts);
        };

        var body = new StackPanel();
        body.Children.Add(surface);
        body.Children.Add(input);
        body.Children.Add(hint);

        string? result = null;
        var dialog = new DialogHost("Keyboard shortcut", "The shortcut is held for as long as you hold the button.", body);
        dialog.AddButton("Cancel", false, dialog.Close);
        dialog.AddButton("Save", true, () =>
        {
            var chord = input.Text.Trim().ToLowerInvariant();
            if (!KeyChord.TryParse(chord, out _))
            {
                hint.Text = $"\"{chord}\" is not a valid shortcut.";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Foreground");
                return;
            }

            result = chord;
            dialog.DialogResult = true;
        });
        dialog.Loaded += (_, _) => surface.Focus();
        return dialog.ShowFor(owner) ? result : null;
    }

    /// <summary>Asks for a line of text.</summary>
    public static string? AskText(Window? owner, string title, string description, string initial = "", string? placeholder = null)
    {
        var input = new TextBox { Text = initial, Tag = placeholder };
        string? result = null;
        var dialog = new DialogHost(title, description, input);
        dialog.AddButton("Cancel", false, dialog.Close);
        dialog.AddButton("Save", true, () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            result = input.Text;
            dialog.DialogResult = true;
        });
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowFor(owner) ? result : null;
    }

    public static bool Confirm(Window? owner, string title, string description, string action)
    {
        var dialog = new DialogHost(title, description, new Border { Height = 0 });
        dialog.AddButton("Cancel", false, dialog.Close);
        dialog.AddButton(action, true, () => dialog.DialogResult = true);
        return dialog.ShowFor(owner);
    }

    public static string? AskExecutable(Window? owner)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app or file",
            Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.ps1|All files|*.*",
        };
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }

    public sealed record AppChoice(string Match, string Name, string? ExecutablePath);

    /// <summary>Lets the user pick a running app, the desktop, the taskbar, or browse for an .exe.</summary>
    public static AppChoice? AskApplication(Window? owner, IEnumerable<string> exclude)
    {
        var excluded = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var list = new StackPanel();
        AppChoice? result = null;
        DialogHost? dialog = null;

        void Add(AppChoice choice, ImageSource? icon, string? glyph)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            UIElement visual = icon != null
                ? new Image { Source = icon, Width = 18, Height = 18 }
                : Ui.Icon(glyph ?? "", 15, "MutedForeground");
            ((FrameworkElement)visual).HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(visual);
            var name = Ui.Text(choice.Name, "Label");
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var key = Ui.Text(choice.Match, "Caption");
            key.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
            Grid.SetColumn(key, 2);
            row.Children.Add(key);

            var button = new Button
            {
                Style = Ui.Style("GhostButton"),
                Content = row,
                Height = 40,
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                FontWeight = FontWeights.Normal,
            };
            button.Click += (_, _) =>
            {
                result = choice;
                dialog!.DialogResult = true;
            };
            list.Children.Add(button);
        }

        if (!excluded.Contains(ContextMatcher.Desktop)) Add(new AppChoice(ContextMatcher.Desktop, "Desktop", null), null, "");
        if (!excluded.Contains(ContextMatcher.Taskbar)) Add(new AppChoice(ContextMatcher.Taskbar, "Taskbar", null), null, "");
        foreach (var app in RunningApps().Where(a => !excluded.Contains(a.Match)))
            Add(app, Ui.ExecutableIcon(app.ExecutablePath), null);

        var scroller = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Style = Ui.Style("Card"), Padding = new Thickness(4), Child = scroller };

        dialog = new DialogHost("Add application", "Pick a running app. Its profile switches on when the app is focused.", frame, 480);
        dialog.AddButton("Browse…", false, () =>
        {
            var path = AskExecutable(dialog);
            if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            result = new AppChoice(Path.GetFileName(path).ToLowerInvariant(), FriendlyName(path), path);
            dialog.DialogResult = true;
        });
        dialog.AddButton("Cancel", false, dialog.Close);
        return dialog.ShowFor(owner) ? result : null;
    }

    private static List<AppChoice> RunningApps()
    {
        var own = Environment.ProcessId;
        var apps = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == own || process.MainWindowHandle == 0) continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                var match = (path != null ? Path.GetFileName(path) : process.ProcessName + ".exe").ToLowerInvariant();
                if (match is "applicationframehost.exe" or "textinputhost.exe" or "explorer.exe" && path == null) continue;
                apps.TryAdd(match, new AppChoice(match, path != null ? FriendlyName(path) : process.ProcessName, path));
            }
            catch
            {
                // Process exited while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string FriendlyName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch { }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : path;
    }
}
