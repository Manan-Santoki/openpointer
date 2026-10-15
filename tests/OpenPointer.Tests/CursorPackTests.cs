using OpenPointer.Platform;

namespace OpenPointer.Tests;

public class CursorPackTests
{
    private static readonly string Shipped = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "OpenPointer", "Assets", "Cursors"));

    [Fact]
    public void ShippedPacksAreValidAndComplete()
    {
        var warnings = new List<string>();
        var packs = CursorPacks.Load(Shipped, warnings.Add);
        Assert.Empty(warnings);
        var mac = Assert.Single(packs, p => p.Id == "macos-sierra");
        Assert.Equal("MIT", mac.License);
        Assert.True(File.Exists(Path.Combine(mac.Folder, mac.LicenseFile!)));
        foreach (var shadow in new[] { true, false })
            foreach (var size in mac.SizeKeys)
                foreach (var file in mac.Roles.Values)
                    Assert.True(File.Exists(Path.Combine(mac.VariantFolder(shadow, size), file)), $"{shadow}/{size}/{file}");
    }

    [Fact]
    public void ResolvesVariantFolders()
    {
        var pack = CursorPacks.Parse("""
            { "id": "x", "name": "X", "shadow": { "on": "s", "off": "ns" }, "sizes": { "normal": "n", "large": "l" },
              "path": "{shadow}/{size}", "roles": { "Arrow": "a.cur" } }
            """, @"C:\packs\x");
        Assert.Equal(Path.Combine(@"C:\packs\x", "ns/l"), pack.VariantFolder(false, "large"));
        Assert.Equal(Path.Combine(@"C:\packs\x", "s/n"), pack.VariantFolder(true, "xlarge")); // unknown size falls back
        Assert.Equal("x-no-shadow-large", pack.VariantId(false, "large"));
    }

    [Theory]
    [InlineData("""{ "id": "", "name": "X", "roles": { "Arrow": "a.cur" } }""")]
    [InlineData("""{ "id": "windows", "name": "X", "roles": { "Arrow": "a.cur" } }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": { "Pointer": "a.cur" } }""")]
    [InlineData("""{ "id": "x", "name": "X", "roles": { "IBeam": "t.cur" } }""")]
    public void RejectsInvalidPacks(string json) =>
        Assert.Throws<InvalidDataException>(() => CursorPacks.Parse(json, "."));
}
