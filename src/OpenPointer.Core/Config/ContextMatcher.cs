namespace OpenPointer.Config;

/// <summary>The window that has focus, reduced to what profiles match on.</summary>
public sealed record FocusContext(string ProcessName, string WindowClass, string Title)
{
    public static readonly FocusContext Unknown = new("", "", "");

    public bool IsDesktop => WindowClass is "Progman" or "WorkerW";
    public bool IsTaskbar => WindowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>What a profile would list to target this context.</summary>
    public string MatchKey => IsDesktop ? ContextMatcher.Desktop : IsTaskbar ? ContextMatcher.Taskbar : ProcessName;

    public string DisplayName => IsDesktop ? "Desktop" : IsTaskbar ? "Taskbar" : ProcessName.Length > 0 ? ProcessName : "Unknown";
}

public static class ContextMatcher
{
    public const string Desktop = "@desktop";
    public const string Taskbar = "@taskbar";

    public static bool Matches(Profile profile, FocusContext context)
    {
        if (!profile.Enabled) return false;
        foreach (var pattern in profile.Match)
        {
            if (pattern.Equals(Desktop, StringComparison.OrdinalIgnoreCase))
            {
                if (context.IsDesktop) return true;
            }
            else if (pattern.Equals(Taskbar, StringComparison.OrdinalIgnoreCase))
            {
                if (context.IsTaskbar) return true;
            }
            else if (!context.IsDesktop && !context.IsTaskbar &&
                     string.Equals(Normalize(pattern), Normalize(context.ProcessName), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";

    /// <summary>The app-specific profile for a context (first match wins), or null when only Default applies.</summary>
    public static Profile? FindAppProfile(AppConfig config, FocusContext context) =>
        config.Profiles.Skip(1).FirstOrDefault(p => Matches(p, context));

    /// <summary>Resolves a button for the context: the app profile wins, then Default, then native.</summary>
    public static ActionSpec ResolveButton(AppConfig config, FocusContext context, string button)
    {
        var app = FindAppProfile(config, context);
        if (app != null && app.Buttons.TryGetValue(button, out var action)) return action;
        return config.DefaultProfile.Buttons.TryGetValue(button, out var fallback) ? fallback : new NativeAction();
    }

    public static (ActionSpec? Left, ActionSpec? Right) ResolveThumbWheel(AppConfig config, FocusContext context)
    {
        var app = FindAppProfile(config, context);
        if (app != null && (app.ThumbWheelLeft != null || app.ThumbWheelRight != null))
            return (app.ThumbWheelLeft, app.ThumbWheelRight);
        return (config.DefaultProfile.ThumbWheelLeft, config.DefaultProfile.ThumbWheelRight);
    }

    public static int? ResolveDpi(AppConfig config, FocusContext context) =>
        FindAppProfile(config, context)?.Dpi ?? config.DefaultProfile.Dpi;
}
