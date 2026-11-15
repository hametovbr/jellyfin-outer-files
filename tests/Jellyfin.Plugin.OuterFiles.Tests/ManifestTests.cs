using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

// The plugin repository manifest must be installable by Jellyfin.
public sealed class ManifestTests
{
    [Fact]
    public void EverySourceUrlIsAZipForJellyfinsInstaller()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(VersionConsistencyTests.FindRepositoryRoot(), "manifest.json")));
        var versions = doc.RootElement.EnumerateArray().SelectMany(p => p.GetProperty("versions").EnumerateArray()).ToList();
        Assert.NotEmpty(versions);
        foreach (var v in versions)
        {
            var url = v.GetProperty("sourceUrl").GetString()!;

            // Jellyfin 12.4 InstallationManager: Path.GetExtension(sourceUrl) must be ".zip"; a query string breaks it.
            Assert.Equal(".zip", Path.GetExtension(url));
            Assert.Matches("^[0-9a-f]{32}$", v.GetProperty("checksum").GetString()!);
            Assert.Matches(new Regex(@"^\d+\.\d+\.\d+\.\d+$"), v.GetProperty("targetAbi").GetString()!);
        }
    }
}
