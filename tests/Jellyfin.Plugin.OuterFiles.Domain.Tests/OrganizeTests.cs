using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class OrganizeTests
{
    private static readonly Templates Default = new(
        "{Series Title} ({Year})",
        "Season {season:00}",
        "{Series Title} ({Year}) - S{season:00}E{episode:00}{-Eend:00} - {Episode Title} {Quality}",
        "{Movie Title} ({Year})",
        "{Movie Title} ({Year}) {Quality}");

    private static Inventory Release(string name) =>
        Inventory.Build(Corpus.Series + "/" + name, Corpus.Under(Corpus.Series + "/" + name).ToList());

    private static Dictionary<string, RowDecision> Proposed(Inventory inv) =>
        EpisodeMapper.Propose(inv, null, new Dictionary<string, int>()).ToDictionary(r => Path.GetFileName(r.Video));

    [Fact]
    public void ProposesJujutsuKaisenSeasonsFromFoldersAndSkipsTheDuplicateRelease()
    {
        var rows = Proposed(Release("JUJUTSU KAISEN"));
        Assert.Equal((RowAction.Episode, 1, 1), Key(rows.First(r => r.Key.StartsWith("JUJUTSU KAISEN - S01E01", StringComparison.Ordinal)).Value));
        Assert.Equal((RowAction.Episode, 2, 1), Key(rows["Jujutsu Kaisen II - 01 [BDRip 1080p HEVC 10bits FLAC].mkv"]));
        Assert.Equal((RowAction.Episode, 3, 11), Key(rows["[BudLightSubs] Jujutsu Kaisen S3 - 11 [1080p].mkv"]));
        var aniPlague = rows.Where(r => r.Key.StartsWith("[AniPlague]", StringComparison.Ordinal)).Select(r => r.Value).ToList();
        Assert.Equal(24, aniPlague.Count);
        Assert.All(aniPlague, r => Assert.Equal(RowAction.Skip, r.Action));
        Assert.All(aniPlague, r => Assert.StartsWith("duplicate of Season 1/", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ProposesSpecialsAndExtrasForKOn()
    {
        var rows = Proposed(Release("K-ON!"));
        // Sonarr's Specials/S00E05 already exists: the Henshin copy is proposed as a skipped duplicate.
        var sp = rows["[Henshin]_K-ON!_sp_05_(BD_x264_1920x1080_FLAC).mkv"];
        Assert.Equal((RowAction.Skip, 0, 5), Key(sp));
        Assert.StartsWith("duplicate of Specials/K-ON! - S00E05", sp.Reason, StringComparison.Ordinal);
        Assert.Equal((RowAction.Special, 0, 19), Key(rows.First(r => r.Key.StartsWith("K-ON! - S00E19", StringComparison.Ordinal)).Value));
        Assert.Equal(RowAction.Extra, rows["[Henshin]_K-ON!_no-credit_ending_(BD_x264_1920x1080_FLAC).mkv"].Action);
    }

    [Fact]
    public void ConvertsAbsoluteNumbersWithSeasonCounts()
    {
        Assert.Equal((2, 3), EpisodeMapper.FromAbsolute(15, [12, 12]));
        Assert.Null(EpisodeMapper.FromAbsolute(30, [12, 12]));
    }

    [Fact]
    public void PlansJitsuIntoTheTemplateWithTracksNextToTheVideo()
    {
        var inv = Release("Jitsu wa Ore, Saikyou deshita [2023][BDRip][1080p]");
        var decisions = EpisodeMapper.Propose(inv, null, new Dictionary<string, int>());
        var titles = inv.Videos.ToDictionary(v => v.Path, v => "Episode " + ReleaseNameParser.Parse(v.Stem).Episode);
        var qualities = inv.Videos.ToDictionary(v => v.Path, v => QualityParser.Of(v.Stem, 1080));
        var descriptors = inv.Videos.SelectMany(v => v.OuterTracks).ToDictionary(t => t.Track, t => TrackDescriber.Describe(t).WithFallbackLanguage("rus", "default"));
        var dirs = new HashSet<string>(StringComparer.Ordinal) { Corpus.Series, inv.Root };
        foreach (var f in Corpus.Under(inv.Root))
        {
            for (var d = Path.GetDirectoryName(f)!; d.Length > inv.Root.Length; d = Path.GetDirectoryName(d)!)
            {
                dirs.Add(d);
            }
        }

        var ctx = new RenameContext(inv, new Identity(false, "Am I Actually the Strongest?", 2023), Corpus.Series, Corpus.Series + "/.outer-files-trash/x", Default,
            titles, qualities, descriptors, Corpus.Under(inv.Root).ToHashSet(StringComparer.Ordinal), dirs);
        var plan = RenamePlanner.Build(ctx, decisions);

        Assert.Empty(plan.Conflicts);
        Assert.Equal(Corpus.Series + "/Am I Actually the Strongest (2023)", plan.TargetRoot);
        var e1 = plan.VideoTargets.Values.Single(v => v.Contains("S01E01", StringComparison.Ordinal));
        Assert.Equal(plan.TargetRoot + "/Season 01/Am I Actually the Strongest (2023) - S01E01 - Episode 1 1080p.mkv", e1);
        Assert.Equal(2, plan.VideoTargets.Values.Count(v => v.StartsWith(plan.TargetRoot + "/extras/", StringComparison.Ordinal)));

        var e1Stem = Path.GetFileNameWithoutExtension(e1);
        var tracks = plan.Moves.Where(m => m.What == "track" && Path.GetFileName(m.To).StartsWith(e1Stem, StringComparison.Ordinal)).Select(m => Path.GetFileName(m.To)[e1Stem.Length..]).Order(StringComparer.Ordinal).ToList();
        Assert.Contains(".Anilibria.rus.mka", tracks);
        Assert.Contains(".CR.forced.rus.ass", tracks);
        Assert.Contains(".CR.eng.ass", tracks);
        Assert.Contains(".CR.forced.eng.ass", tracks);
        Assert.Contains(".jpn.srt", tracks);

        Assert.Empty(PlanValidator.Validate(plan.Plan, [Corpus.Series]));
        Assert.Contains(plan.Plan.Operations, o => o is RemoveEmptyDirectory r && r.Path == inv.Root);
        Assert.Empty(plan.LeftInPlace);
    }

    private static (RowAction, int?, int?) Key(RowDecision r) => (r.Action, r.Season, r.Episode);
}
