namespace OpenPointer.Platform;

/// <summary>An app from the Start menu's app list: desktop programs and Microsoft Store apps alike.</summary>
public sealed record InstalledApp(string Name, string AppId)
{
    /// <summary>Launch target that survives app updates (Store apps live in version-named folders).</summary>
    public string LaunchTarget => InstalledApps.TargetFor(AppId);
}

public static class InstalledApps
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    public static string TargetFor(string appId) => AppsFolderPrefix + appId;

    public static bool IsAppsFolderTarget(string target) =>
        target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every app in shell:AppsFolder (what the Start menu shows), sorted by name. Must run on an
    /// STA thread, such as the UI thread.
    /// </summary>
    public static List<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return apps;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return apps;
            foreach (var item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                // Skip uninstallers, help files and web links that the Start menu also lists.
                if (id.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".chm", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(new InstalledApp(name, id));
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        return apps
            .DistinctBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase) // the Start menu can list one app twice
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
