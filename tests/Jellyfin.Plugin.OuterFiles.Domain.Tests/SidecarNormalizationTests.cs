using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class SidecarNormalizationTests
{
    private const string Root = "/tv/Scrubs";
    private const string Stem = "Season 8/Scrubs - S08E14 - My Full Moon Bluray-1080p";

    [Theory]
    [InlineData(".rus.19", true)]
    [InlineData(".subrip.3", true)]
    [InlineData(".hdmv_pgs_subtitle.22", true)]
    [InlineData(".ass.4", true)]
    [InlineData(".ru", false)]
    [InlineData(".en.hi", false)]
    [InlineData(".ru_Anilibria", false)]
    public void DetectsToolGeneratedSuffixes(string suffix, bool expected) => Assert.Equal(expected, TrackDescriber.HasToolSuffix(suffix));

    [Fact]
    public void NormalizesAdjacentTrackNamesWhenOrganizing()
    {
        var files = new[] { ".mkv", ".rus.19.srt", ".rus.20.srt", ".subrip.3.srt", ".hdmv_pgs_subtitle.22.sup", ".en.hi.srt", ".eng.srt", ".forced.ru.srt", "-thumb.jpg" }
            .Select(s => $"{Root}/{Stem}{s}").ToList();
        var inv = Inventory.Build(Root, files);
        var video = inv.Videos.Single();
        Assert.Equal(8, video.Sidecars.Count);

        var guessed = new Dictionary<string, TrackDescriptor> { [$"{Root}/{Stem}.subrip.3.srt"] = new("eng", "text", null, false, false) };
        var ctx = new RenameContext(inv, new Identity(false, "Scrubs", 2001), "/tv", "/tv/.outer-files-trash/x",
            new Templates("{Series Title} ({Year})", "Season {season:00}", "{Series Title} ({Year}) - S{season:00}E{episode:00} - {Episode Title} {Quality}", "{Movie Title} ({Year})", "{Movie Title} ({Year})"),
            new Dictionary<string, string> { [video.Path] = "My Full Moon" }, new Dictionary<string, string> { [video.Path] = "Bluray-1080p" }, guessed,
            new HashSet<string>(files), new HashSet<string> { "/tv", Root, Root + "/Season 8" });
        var plan = RenamePlanner.Build(ctx, [new RowDecision(video.Path, RowAction.Episode, 8, 14, null, null, "test")]);

        var newStem = "Scrubs (2001) - S08E14 - My Full Moon Bluray-1080p";
        var names = plan.Moves.Where(m => m.What == "sidecar").Select(m => Path.GetFileName(m.To)[newStem.Length..]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["-thumb.jpg", ".#2.eng.srt", ".#2.rus.srt", ".eng.srt", ".forced.rus.srt", ".rus.srt", ".sdh.eng.srt", ".und.sup"],
            names);
        Assert.Empty(plan.Conflicts);
    }
}
