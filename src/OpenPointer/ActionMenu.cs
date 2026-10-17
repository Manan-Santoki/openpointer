using System.Windows;
using System.Windows.Controls;
using OpenPointer.Config;

namespace OpenPointer.App;

/// <summary>Builds the "choose an action" menu shared by the tray and the settings window.</summary>
public static class ActionMenu
{
    /// <param name="target">ContextMenu or MenuItem to fill.</param>
    /// <param name="current">The action currently in effect.</param>
    /// <param name="pick">Receives the chosen action, or null for "inherit from All applications".</param>
    /// <param name="inheritLabel">When set, offers resetting to the inherited value.</param>
    /// <param name="allowGestures">Offer gestures (the control reports movement while held).</param>
    /// <param name="ownerResolver">Window for dialogs; null shows them centered on screen.</param>
    public static void Populate(ItemsControl target, ActionSpec current, Action<ActionSpec?> pick,
        string? inheritLabel = null, bool allowGestures = false, bool allowNative = true, Func<Window?>? ownerResolver = null,
        string nativeLabel = "Default")
    {
        target.Items.Clear();
        var isOverride = inheritLabel != null;

        if (isOverride)
        {
            target.Items.Add(Ui.MenuItem(inheritLabel!, () => pick(null)));
            target.Items.Add(new Separator());
        }

        if (allowNative)
            target.Items.Add(Ui.MenuItem(nativeLabel, () => pick(new NativeAction()), current is NativeAction, nativeLabel == "Default" ? "Built-in" : null));

        foreach (var group in ActionCatalog.Groups)
        {
            var entries = group.Entries.Where(e => Services.SelectedMouse?.SupportsAction(e.Action) != false).ToArray();
            if (entries.Length == 0) continue;
            var sub = new MenuItem { Header = group.Name };
            var any = false;
            foreach (var entry in entries)
            {
                var selected = entry.Action == current ||
                               (current is KeysAction ck && entry.Action is KeysAction ek && ek.Keys == ck.Keys);
                any |= selected;
                sub.Items.Add(Ui.MenuItem(entry.Label, () => pick(entry.Action), selected));
            }

            sub.IsChecked = any;
            target.Items.Add(sub);
        }

        if (allowGestures)
        {
            target.Items.Add(Ui.MenuItem("Gestures", () => pick(current as GestureAction ?? GestureAction.WindowsDefaults),
                current is GestureAction, "Hold + move"));
            target.Items.Add(Ui.MenuItem("Actions Ring", () => pick(current as ActionRingAction ?? ActionRingAction.Defaults),
                current is ActionRingAction, "Hold + aim"));
        }

        if (current is KeysAction held)
        {
            target.Items.Add(new Separator());
            target.Items.Add(Ui.MenuItem("Repeat while held", () => pick(held with { Repeat = !held.ShouldRepeat }), held.ShouldRepeat));
        }

        target.Items.Add(new Separator());

        var isCustomKeys = current is KeysAction k && ActionCatalog.Label(k) == k.Describe();
        target.Items.Add(Ui.MenuItem("Keyboard shortcut…", () =>
        {
            var chord = Dialogs.AskShortcut(ownerResolver?.Invoke(), (current as KeysAction)?.Keys);
            if (chord != null) pick(new KeysAction(chord));
        }, isCustomKeys, isCustomKeys ? ((KeysAction)current).Describe() : null));

        var opensApp = current is LaunchAction l && !IsUrl(l.Target);
        target.Items.Add(Ui.MenuItem("Open app…", () =>
        {
            var app = Dialogs.AskLaunchApp(ownerResolver?.Invoke());
            if (app != null) pick(app);
        }, opensApp, opensApp ? ((LaunchAction)current).Describe()[5..] : "Includes Store apps"));

        target.Items.Add(Ui.MenuItem("Open file or folder…", () =>
        {
            var path = Dialogs.AskExecutable(ownerResolver?.Invoke());
            if (path != null) pick(new LaunchAction(path));
        }));

        target.Items.Add(Ui.MenuItem("Open website…", () =>
        {
            var url = Dialogs.AskText(ownerResolver?.Invoke(), "Open website", "The page opens in your default browser.",
                current is LaunchAction la && IsUrl(la.Target) ? la.Target : "https://");
            if (url != null) pick(new LaunchAction(url.Trim()));
        }, current is LaunchAction lu && IsUrl(lu.Target)));

        target.Items.Add(Ui.MenuItem("Macro…", () =>
        {
            var macro = Dialogs.EditMacro(ownerResolver?.Invoke(), current as MacroAction);
            if (macro != null) pick(macro);
        }, current is MacroAction, current is MacroAction m ? m.Describe() : "Steps in order"));

        target.Items.Add(Ui.MenuItem("Type text…", () =>
        {
            var text = Dialogs.AskText(ownerResolver?.Invoke(), "Type text", "Typed into the focused app when you press the button.",
                (current as TextAction)?.Text ?? "");
            if (text != null) pick(new TextAction(text));
        }, current is TextAction));

        target.Items.Add(new Separator());
        target.Items.Add(Ui.MenuItem("Do nothing", () => pick(new NoneAction()), current is NoneAction));
    }

    private static bool IsUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the picker as a dropdown under a control.</summary>
    /// <param name="extend">Adds page-specific items at the end of the menu.</param>
    public static void ShowDropdown(FrameworkElement anchor, ActionSpec current, Action<ActionSpec?> pick,
        string? inheritLabel = null, bool allowGestures = false, bool allowNative = true,
        string nativeLabel = "Default", Action<ContextMenu>? extend = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        Populate(menu, current, pick, inheritLabel, allowGestures, allowNative, () => Window.GetWindow(anchor), nativeLabel);
        extend?.Invoke(menu);
        menu.IsOpen = true;
    }
}
