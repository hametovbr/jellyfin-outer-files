using Jellyfin.Plugin.OuterFiles.Domain.Organize;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class NameTemplateTests
{
    private const string Episode = "{Series Title} ({Year}) - S{season:00}E{episode:00}{-Eend:00} - {Episode Title} {Quality}";

    private static Dictionary<string, object?> V(string? title, int? year, int s, int e, int? end, string et, string q) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["series title"] = title, ["year"] = year, ["season"] = s, ["episode"] = e, ["end"] = end, ["episode title"] = et, ["quality"] = q,
    };

    [Fact]
    public void RendersSonarrStyleNames()
    {
        Assert.Equal("Bluey (2018) - S03E05 - Omelette WEBDL-720p", NameTemplate.Render(Episode, V("Bluey", 2018, 3, 5, null, "Omelette", "WEBDL-720p")));
        Assert.Equal("Paw Patrol - S01E11-E12 - Pups on Ice WEBDL-720p", NameTemplate.Render(Episode, V("Paw Patrol", null, 1, 11, 12, "Pups on Ice", "WEBDL-720p")));
        Assert.Equal("Show (2023) - S01E01", NameTemplate.Render(Episode, V("Show", 2023, 1, 1, null, string.Empty, string.Empty)));
        Assert.Equal("Show (2023) - S01E01 1080p", NameTemplate.Render(Episode, V("Show", 2023, 1, 1, null, string.Empty, "1080p")));
        Assert.Equal("Season 00", NameTemplate.Render("Season {season:00}", new Dictionary<string, object?> { ["season"] = 0 }));
    }

    [Fact]
    public void SanitizesTitles()
    {
        Assert.Equal("Re - Zero - Who Are You+What", NameTemplate.Render("{Series Title}", new Dictionary<string, object?> { ["series title"] = "Re: Zero: Who Are You?/What\"" }));
        Assert.Equal(NameTemplate.MaxNameLength, NameTemplate.Fit(new string('x', 400), 0).Length);
    }

    [Theory]
    [InlineData("Show - S01E01 - Title WEBDL-1080p Proper", null, "WEBDL-1080p Proper")]
    [InlineData("[VCB-Studio] Jitsu [01][Ma10p_1080p][x265_flac]", null, "1080p")]
    [InlineData("[MoscowGolem] Shinchou Yuusha - 01 [BDRip 1080p x265 FLAC]", null, "Bluray-1080p")]
    [InlineData("Yakusoku no Neverland TV-2 - 10 (WEB-DL 1920x1080 x264 AAC)", null, "WEBDL-1080p")]
    [InlineData("[Henshin]_K-ON!_07_(BD_x264_1920x1080_FLAC)", null, "Bluray-1080p")]
    [InlineData("136. Индийский чай", 576, "576p")]
    [InlineData("136. Индийский чай", null, "")]
    public void FindsQuality(string name, int? height, string expected) => Assert.Equal(expected, QualityParser.Of(name, height));
}
