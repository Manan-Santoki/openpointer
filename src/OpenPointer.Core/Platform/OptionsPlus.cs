using System.Diagnostics;

namespace OpenPointer.Platform;

/// <summary>Detects and stops Logi Options+, which competes with OpenPointer for button diversion.</summary>
public static class OptionsPlus
{
    // The agent owns the device; the others are the UI and its broker.
    private static readonly string[] Processes = ["logioptionsplus_agent", "logioptionsplus_appbroker", "logioptionsplus"];

    public static bool IsRunning() => Process.GetProcessesByName(Processes[0]).Length > 0;

    /// <summary>Stops Options+ for this session. Returns false if a process could not be stopped (e.g. elevated).</summary>
    public static bool Stop()
    {
        var ok = true;
        foreach (var name in Processes)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not stop {name}: {ex.Message}");
                    ok = false;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return ok;
    }
}

/// <summary>Start-with-Windows via the per-user Run key.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenPointer";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string executablePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\" --background");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
