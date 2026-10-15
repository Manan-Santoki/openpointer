namespace OpenPointer;

/// <summary>Minimal file logger: %APPDATA%\OpenPointer\openpointer.log, trimmed at startup.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string? _path;

    public static event Action<string>? Written;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "openpointer.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                File.Move(_path, _path + ".old", overwrite: true);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
        lock (Sync)
        {
            if (_path != null)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); }
                catch (IOException) { }
            }
        }

        Written?.Invoke(line);
    }
}
