using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPointer.Config;

/// <summary>
/// Loads and saves %APPDATA%\OpenPointer\config.json and reloads it when edited by hand.
/// A broken file is never overwritten: the last good config stays active and Error is raised.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keep chords like "ctrl+shift+t" readable instead of "ctrl+shift+t".
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastWrittenJson;

    public ConfigStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPointer", "config.json");
        Current = AppConfig.CreateDefault();
    }

    public string FilePath { get; }
    public AppConfig Current { get; private set; }

    /// <summary>Raised after the config changes, from Save or from an external edit.</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>Raised when the file on disk cannot be parsed.</summary>
    public event Action<string>? Error;

    public void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
            {
                Current = AppConfig.CreateDefault();
                WriteFile(Current);
                return;
            }

            Current = Parse(File.ReadAllText(FilePath));
        }
    }

    public static AppConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
        if (config.Profiles.Count == 0) config.Profiles.Add(new Profile { Name = "Default" });
        config.Devices.TryAdd("*", new DeviceSettings());
        return config;
    }

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    /// <summary>Applies a change to a copy of the config, saves it and notifies listeners.</summary>
    public void Update(Action<AppConfig> mutate)
    {
        AppConfig next;
        lock (_sync)
        {
            next = Parse(Serialize(Current));
            mutate(next);
            WriteFile(next);
            Current = next;
        }

        Changed?.Invoke(next);
    }

    private void WriteFile(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = Serialize(config);
        _lastWrittenJson = json;
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Watch()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        _debounce = new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _debounce.Change(250, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(250, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(250, Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        AppConfig next;
        lock (_sync)
        {
            string json;
            try { json = File.ReadAllText(FilePath); }
            catch (IOException) { _debounce?.Change(250, Timeout.Infinite); return; }

            if (json == _lastWrittenJson) return;
            try
            {
                next = Parse(json);
            }
            catch (JsonException ex)
            {
                Error?.Invoke($"config.json has an error and was not applied: {ex.Message}");
                return;
            }

            _lastWrittenJson = json;
            Current = next;
        }

        Changed?.Invoke(next);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
