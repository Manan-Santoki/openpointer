using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenPointer.Config;

namespace OpenPointer.App;

/// <summary>
/// A modal card shown inside the main window over a blurred, disabled background. It belongs to the
/// window: it moves, minimizes and goes behind other apps together with it.
/// </summary>
public sealed class DialogHost : Border
{
    private readonly StackPanel _footer;
    private System.Windows.Threading.DispatcherFrame? _frame;
    private bool? _result;

    public DialogHost(string title, string? description, UIElement body, double width = 440)
    {
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

        Style = Ui.Style("Card");
        Padding = new Thickness(24);
        Width = width;
        MaxHeight = 640;
        Child = stack;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.35, Color = Colors.Black };
        SetResourceReference(BackgroundProperty, "Background");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>The window hosting this dialog, for nested system dialogs.</summary>
    public Window? HostWindow => Window.GetWindow(this);

    /// <summary>Setting a value closes the dialog with that result, like Window.DialogResult.</summary>
    public bool? DialogResult
    {
        get => _result;
        set
        {
            _result = value;
            if (_frame != null) _frame.Continue = false;
        }
    }

    public void Close() => DialogResult = false;

    public Button AddButton(string text, bool primary, Action click)
    {
        var button = Ui.Button(text, click, primary ? "PrimaryButton" : null);
        button.Margin = new Thickness(8, 0, 0, 0);
        button.MinWidth = 80;
        if (primary) button.IsDefault = true;
        _footer.Children.Add(button);
        return button;
    }

    /// <summary>Shows the dialog in the main window and blocks (pumping messages) until it closes.</summary>
    public bool ShowFor(Window? owner)
    {
        var host = owner as Views.MainWindow ?? App.MainWindowInstance ?? App.ShowMainWindowAndGet();
        if (!host.IsVisible) host.Show();
        if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
        host.Activate();

        host.PushDialog(this);
        _frame = new System.Windows.Threading.DispatcherFrame();
        Loaded += FocusFirst;
        System.Windows.Threading.Dispatcher.PushFrame(_frame);
        host.PopDialog(this);
        _frame = null;
        return _result == true;
    }

    private void FocusFirst(object sender, RoutedEventArgs e)
    {
        Loaded -= FocusFirst;
        if (!IsKeyboardFocusWithin) MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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

    private enum StepKind { Shortcut, Text, Open, Wait }

    private sealed class StepDraft
    {
        public StepKind Kind;
        public string Value = "";
    }

    /// <summary>Edits a macro: shortcuts, typed text, launches and waits, run in order.</summary>
    public static MacroAction? EditMacro(Window? owner, MacroAction? initial)
    {
        var steps = initial?.Steps.Select(ToDraft).Where(d => d != null).Select(d => d!).ToList()
                    ?? [new StepDraft { Kind = StepKind.Shortcut, Value = "ctrl+c" }];
        var name = new TextBox { Text = initial?.Name ?? "" };
        var list = new StackPanel();
        var error = Ui.Text("", "Caption");
        error.SetResourceReference(TextBlock.ForegroundProperty, "Foreground");

        void Render()
        {
            list.Children.Clear();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var index = i;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var number = Ui.Text($"{i + 1}", "Caption");
                number.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(number);

                var kind = Views.ButtonsPage.SelectButton(KindLabel(step.Kind), anchor =>
                {
                    var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                    foreach (var k in Enum.GetValues<StepKind>())
                    {
                        var choice = k;
                        menu.Items.Add(Ui.MenuItem(KindLabel(choice), () =>
                        {
                            step.Kind = choice;
                            step.Value = choice == StepKind.Wait ? "200" : "";
                            Render();
                        }, choice == step.Kind));
                    }

                    menu.IsOpen = true;
                });
                kind.MinWidth = 0;
                kind.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(kind, 1);
                row.Children.Add(kind);

                var value = new TextBox { Text = step.Value, ToolTip = KindHint(step.Kind) };
                value.TextChanged += (_, _) => step.Value = value.Text;
                Grid.SetColumn(value, 2);
                row.Children.Add(value);

                var remove = new Button { Style = Ui.Style("IconButton"), Content = Ui.Icon("", 11, "MutedForeground"), ToolTip = "Remove", Margin = new Thickness(4, 0, 0, 0) };
                remove.Click += (_, _) =>
                {
                    steps.RemoveAt(index);
                    Render();
                };
                Grid.SetColumn(remove, 3);
                row.Children.Add(remove);
                list.Children.Add(row);
            }

            var add = Ui.Button("Add step", () =>
            {
                steps.Add(new StepDraft { Kind = StepKind.Shortcut });
                Render();
            }, "GhostButton");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(add);
        }

        Render();
        var nameLabel = Ui.Text("Name", "Label");
        nameLabel.Margin = new Thickness(0, 0, 0, 6);
        var stepsLabel = Ui.Text("Steps", "Label");
        stepsLabel.Margin = new Thickness(0, 16, 0, 6);
        var body = new StackPanel();
        body.Children.Add(nameLabel);
        body.Children.Add(name);
        body.Children.Add(stepsLabel);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(error);

        MacroAction? result = null;
        var dialog = new DialogHost("Macro", "Runs each step in order when you press the button. Shortcut steps accept chords like ctrl+shift+t.", body, 560);
        dialog.AddButton("Cancel", false, dialog.Close);
        dialog.AddButton("Save", true, () =>
        {
            var built = new List<MacroStep>();
            foreach (var (step, n) in steps.Select((s, n) => (s, n + 1)))
            {
                var v = step.Value.Trim();
                switch (step.Kind)
                {
                    case StepKind.Shortcut when KeyChord.TryParse(v.ToLowerInvariant(), out _):
                        built.Add(new MacroStep(new KeysAction(v.ToLowerInvariant())));
                        break;
                    case StepKind.Text when v.Length > 0:
                        built.Add(new MacroStep(new TextAction(step.Value)));
                        break;
                    case StepKind.Open when v.Length > 0:
                        built.Add(new MacroStep(new LaunchAction(v)));
                        break;
                    case StepKind.Wait when int.TryParse(v, out var ms) && ms is > 0 and <= 60000:
                        built.Add(new MacroStep(DelayMs: ms));
                        break;
                    default:
                        error.Text = $"Step {n}: {KindHint(step.Kind)}";
                        error.Margin = new Thickness(0, 8, 0, 0);
                        return;
                }
            }

            if (built.Count == 0)
            {
                error.Text = "Add at least one step.";
                return;
            }

            result = new MacroAction(built, string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim());
            dialog.DialogResult = true;
        });
        return dialog.ShowFor(owner) ? result : null;
    }

    private static StepDraft? ToDraft(MacroStep step) => step switch
    {
        { Action: KeysAction k } => new StepDraft { Kind = StepKind.Shortcut, Value = k.Keys },
        { Action: TextAction t } => new StepDraft { Kind = StepKind.Text, Value = t.Text },
        { Action: LaunchAction l } => new StepDraft { Kind = StepKind.Open, Value = l.Target },
        { Action: null, DelayMs: > 0 } => new StepDraft { Kind = StepKind.Wait, Value = step.DelayMs.ToString() },
        _ => null,
    };

    private static string KindLabel(StepKind kind) => kind switch
    {
        StepKind.Shortcut => "Shortcut",
        StepKind.Text => "Type text",
        StepKind.Open => "Open",
        _ => "Wait (ms)",
    };

    private static string KindHint(StepKind kind) => kind switch
    {
        StepKind.Shortcut => "enter a shortcut such as ctrl+c or win+d.",
        StepKind.Text => "enter the text to type.",
        StepKind.Open => "enter an app, file, folder or URL.",
        _ => "enter a wait between 1 and 60000 milliseconds.",
    };

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
        // File Explorer windows live in the shell process, so they never show up as a running app.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!excluded.Contains("explorer.exe")) Add(new AppChoice("explorer.exe", "File Explorer", explorer), Ui.ExecutableIcon(explorer), null);
        foreach (var app in RunningApps().Where(a => !excluded.Contains(a.Match) && a.Match != "explorer.exe"))
            Add(app, Ui.ExecutableIcon(app.ExecutablePath), null);

        var scroller = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Style = Ui.Style("Card"), Padding = new Thickness(4), Child = scroller };

        dialog = new DialogHost("Add application", "Pick a running app. Its profile switches on when the app is focused.", frame, 480);
        dialog.AddButton("Browse…", false, () =>
        {
            var path = AskExecutable(dialog.HostWindow);
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
                if (process.Id == own || process.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                if (!IsWindowVisible(process.MainWindowHandle) || HiddenProcesses.Contains(process.ProcessName)) continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                var match = (path != null ? Path.GetFileName(path) : process.ProcessName + ".exe").ToLowerInvariant();
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

    // Background hosts that own invisible or helper windows.
    private static readonly HashSet<string> HiddenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "textinputhost", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "searchapp", "lockapp", "dwm", "ctfmon", "sihost", "runtimebroker", "systemsettingsbroker",
        "powertoys.quickaccess", "nvidia share", "nvcontainer", "gamebar", "gamebarftserver", "widgets", "logioptionsplus",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

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
