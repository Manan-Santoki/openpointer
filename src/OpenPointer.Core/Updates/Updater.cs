using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenPointer.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string InstallerName, string InstallerUrl, string ChecksumsUrl, string ReleaseUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer version, downloads the installer, verifies it against the
/// release's SHA256SUMS.txt and runs it silently. Releases without a checksum file are ignored.
/// </summary>
public static class Updater
{
    public const string Repository = "Manan-Santoki/openpointer";
    public const string ChecksumsName = "SHA256SUMS.txt";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenPointer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Returns the latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(json.RootElement, current);
    }

    internal static UpdateInfo? Parse(JsonElement release, Version current)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= Normalize(current)) return null;

        string? installerName = null, installerUrl = null, checksumsUrl = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.StartsWith("OpenPointer-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                installerName = name;
                installerUrl = url;
            }
            else if (name.Equals(ChecksumsName, StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = url;
            }
        }

        if (installerName == null || installerUrl == null || checksumsUrl == null) return null;
        return new UpdateInfo(version, tag, installerName, installerUrl, checksumsUrl,
            release.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
            release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        if (Version.TryParse(text, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    /// <summary>Treats 1.2 and 1.2.0.0 alike.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Downloads and verifies the installer. Returns its local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var sums = await Http.GetStringAsync(update.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        var expected = FindChecksum(sums, update.InstallerName)
                       ?? throw new InvalidOperationException($"{ChecksumsName} has no entry for {update.InstallerName}.");

        var directory = Path.Combine(Path.GetTempPath(), "OpenPointer-update");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.InstallerName);

        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total is > 0) progress?.Report((double)received / total.Value);
            }
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer does not match its published checksum.");
        }

        return path;
    }

    /// <summary>Finds "hash  filename" (sha256sum format) for a file.</summary>
    internal static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                return parts[0];
        }

        return null;
    }

    /// <summary>Starts the silent installer. The caller should exit right after so files can be replaced.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        })?.Dispose();
    }
}
