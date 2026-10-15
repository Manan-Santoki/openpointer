using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPointer.Platform;

/// <summary>A cursor set described by a pack.json file in its folder.</summary>
public sealed class CursorPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Source { get; set; }
    public string? License { get; set; }
    public string? LicenseFile { get; set; }

    /// <summary>Folder names for the shadow variants: keys "on" and "off". Null when there is no choice.</summary>
    public Dictionary<string, string>? Shadow { get; set; }

    /// <summary>Folder names per size: keys "normal", "large", "xlarge". Null when there is one size.</summary>
    public Dictionary<string, string>? Sizes { get; set; }

    /// <summary>Variant folder template using {shadow} and {size}, relative to the pack folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>Windows cursor role (Arrow, IBeam, ...) to file name.</summary>
    public Dictionary<string, string> Roles { get; set; } = [];

    [JsonIgnore]
    public string Folder { get; set; } = "";

    public bool HasShadowOption => Shadow is { Count: > 1 };

    public IReadOnlyList<string> SizeKeys =>
        Sizes == null ? [] : new[] { "normal", "large", "xlarge" }.Where(Sizes.ContainsKey).ToList();

    /// <summary>"by antiden · MIT", for attribution in the UI.</summary>
    public string Attribution => string.Join(" · ", new[] { Author != null ? $"by {Author}" : null, License }.Where(s => s != null));

    /// <summary>Resolves the folder holding the files for a shadow/size choice (falling back to the first options).</summary>
    public string VariantFolder(bool shadow, string size)
    {
        var shadowFolder = Shadow == null ? "" : Shadow.GetValueOrDefault(shadow ? "on" : "off") ?? Shadow.Values.First();
        var sizeFolder = Sizes == null ? "" : Sizes.GetValueOrDefault(size) ?? Sizes[SizeKeys.FirstOrDefault() ?? Sizes.Keys.First()];
        var relative = Path.Replace("{shadow}", shadowFolder).Replace("{size}", sizeFolder).Trim('/', '\\');
        return System.IO.Path.Combine(Folder, relative);
    }

    /// <summary>A stable name for the copied files, e.g. "macos-sierra-shadow-large".</summary>
    public string VariantId(bool shadow, string size) =>
        string.Join("-", new[] { Id, HasShadowOption ? (shadow ? "shadow" : "no-shadow") : null, SizeKeys.Count > 1 ? size : null }.Where(s => s != null));
}

public static class CursorPacks
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads every pack under a folder. Broken packs are skipped and reported.</summary>
    public static List<CursorPack> Load(string root, Action<string>? warn = null)
    {
        var packs = new List<CursorPack>();
        if (!Directory.Exists(root)) return packs;
        foreach (var file in Directory.EnumerateFiles(root, "pack.json", SearchOption.AllDirectories))
        {
            try
            {
                var pack = Parse(File.ReadAllText(file), System.IO.Path.GetDirectoryName(file)!);
                if (packs.Any(p => p.Id.Equals(pack.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"duplicate id \"{pack.Id}\"");
                packs.Add(pack);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                warn?.Invoke($"Skipping cursor pack {file}: {ex.Message}");
            }
        }

        return packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static CursorPack Parse(string json, string folder)
    {
        var pack = JsonSerializer.Deserialize<CursorPack>(json, Options) ?? throw new InvalidDataException("empty pack.json");
        if (string.IsNullOrWhiteSpace(pack.Id) || string.IsNullOrWhiteSpace(pack.Name)) throw new InvalidDataException("id and name are required");
        if (pack.Id is "windows" or "unchanged") throw new InvalidDataException($"\"{pack.Id}\" is a reserved id");
        var unknown = pack.Roles.Keys.Except(CursorThemes.Roles, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0) throw new InvalidDataException($"unknown roles: {string.Join(", ", unknown)}");
        if (!pack.Roles.ContainsKey("Arrow")) throw new InvalidDataException("the Arrow role is required");
        pack.Folder = folder;
        return pack;
    }
}
