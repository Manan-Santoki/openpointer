namespace OpenPointer.Config;

public sealed record CatalogEntry(string Label, ActionSpec Action);

public sealed record CatalogGroup(string Name, IReadOnlyList<CatalogEntry> Entries);

/// <summary>Ready-made actions offered by the tray menu and the settings action picker.</summary>
public static class ActionCatalog
{
    public static readonly IReadOnlyList<CatalogGroup> Groups =
    [
        new("Navigation",
        [
            new("Back", new MouseAction(MouseButtonKind.Back)),
            new("Forward", new MouseAction(MouseButtonKind.Forward)),
            new("Middle click", new MouseAction(MouseButtonKind.Middle)),
            new("Right click", new MouseAction(MouseButtonKind.Right)),
            new("Scroll left", new ScrollAction(-1, Horizontal: true)),
            new("Scroll right", new ScrollAction(1, Horizontal: true)),
        ]),
        new("Windows",
        [
            new("Task view", new KeysAction("win+tab")),
            new("Show desktop", new KeysAction("win+d")),
            new("Previous desktop", new KeysAction("ctrl+win+left")),
            new("Next desktop", new KeysAction("ctrl+win+right")),
            new("Switch apps", new KeysAction("alt+tab")),
            new("Close window", new KeysAction("alt+f4")),
            new("Screenshot", new KeysAction("win+shift+s")),
            new("Start menu", new KeysAction("win")),
            new("Lock", new KeysAction("win+l")),
        ]),
        new("Editing",
        [
            new("Copy", new KeysAction("ctrl+c")),
            new("Paste", new KeysAction("ctrl+v")),
            new("Cut", new KeysAction("ctrl+x")),
            new("Undo", new KeysAction("ctrl+z")),
            new("Redo", new KeysAction("ctrl+y")),
            new("Select all", new KeysAction("ctrl+a")),
            new("Enter", new KeysAction("enter")),
            new("Delete", new KeysAction("delete")),
        ]),
        new("Browser",
        [
            new("New tab", new KeysAction("ctrl+t")),
            new("Close tab", new KeysAction("ctrl+w")),
            new("Reopen closed tab", new KeysAction("ctrl+shift+t")),
            new("Next tab", new KeysAction("ctrl+tab")),
            new("Previous tab", new KeysAction("ctrl+shift+tab")),
            new("Refresh", new KeysAction("f5")),
            new("Zoom in", new KeysAction("ctrl+plus")),
            new("Zoom out", new KeysAction("ctrl+minus")),
        ]),
        new("Media",
        [
            new("Play / pause", new KeysAction("media_play_pause")),
            new("Next track", new KeysAction("media_next")),
            new("Previous track", new KeysAction("media_prev")),
            new("Volume up", new KeysAction("volume_up")),
            new("Volume down", new KeysAction("volume_down")),
            new("Mute", new KeysAction("volume_mute")),
        ]),
        new("Pointer",
        [
            new("Cycle DPI", new DpiAction(DpiMode.Cycle)),
            new("Precision mode (hold)", new DpiAction(DpiMode.Shift, 400)),
            new("Toggle wheel mode", new WheelModeAction()),
        ]),
    ];

    /// <summary>Finds the catalog label for an action, or falls back to its own description.</summary>
    public static string Label(ActionSpec action)
    {
        foreach (var group in Groups)
            foreach (var entry in group.Entries)
                if (entry.Action == action)
                    return entry.Label;
        return action.Describe();
    }
}
