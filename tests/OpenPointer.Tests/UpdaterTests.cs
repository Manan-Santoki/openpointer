using System.Text.Json;
using OpenPointer.Updates;

namespace OpenPointer.Tests;

public class UpdaterTests
{
    private static JsonElement Release(string tag, bool prerelease = false, bool withSums = true)
    {
        var version = tag.TrimStart('v');
        var assets = new List<object> { new { name = $"OpenPointer-Setup-{version}.exe", browser_download_url = "https://x/setup.exe" } };
        if (withSums) assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = "https://x/sums" });
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            html_url = "https://x",
            body = "notes",
            assets,
        });
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void NewerReleaseIsOffered()
    {
        var update = Updater.Parse(Release("v0.3.0"), new Version(0, 2, 0, 0));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Equal("OpenPointer-Setup-0.3.0.exe", update.InstallerName);
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("v0.1.9")]
    public void SameOrOlderIsIgnored(string tag) => Assert.Null(Updater.Parse(Release(tag), new Version(0, 2, 0)));

    [Fact]
    public void PrereleasesAndUnverifiableReleasesAreIgnored()
    {
        Assert.Null(Updater.Parse(Release("v9.0.0", prerelease: true), new Version(0, 2, 0)));
        Assert.Null(Updater.Parse(Release("v9.0.0", withSums: false), new Version(0, 2, 0)));
    }

    [Fact]
    public void FindsChecksumInSha256SumFormat()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.zip\n{hash} *OpenPointer-Setup-0.3.0.exe\n";
        Assert.Equal(hash, Updater.FindChecksum(sums, "OpenPointer-Setup-0.3.0.exe"));
        Assert.Null(Updater.FindChecksum(sums, "missing.exe"));
    }
}
