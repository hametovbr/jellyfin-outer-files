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
        // Sonarr's Specials/S00E05 and Henshin's sp_05 are the same special: the Henshin copy has Russian
        // subtitles, so it is kept and Sonarr's bare copy is proposed as a skipped duplicate.
        Assert.Equal((RowAction.Special, 0, 5), Key(rows["[Henshin]_K-ON!_sp_05_(BD_x264_1920x1080_FLAC).mkv"]));
        var sonarr = rows.First(r => r.Key.StartsWith("K-ON! - S00E05", StringComparison.Ordinal)).Value;
        Assert.Equal((RowAction.Skip, 0, 5), Key(sonarr));
        Assert.StartsWith("duplicate of [Henshin] K-ON! (BDrip)/Special/[Henshin]_K-ON!_sp_05", sonarr.Reason, StringComparison.Ordinal);
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

    [Fact]
    public void KeepsOneCopyOfFontsShippedPerSubtitleGroup()
    {
        const string root = "/lib/Show [BD]";
        string[] files = [root + "/Show - 01.mkv", root + "/EN Subs/fonts/A.ttf", root + "/RU Subs/Fonts/A.ttf", root + "/RU Subs/Fonts/B.otf"];
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "Show", 2020), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/EN Subs", root + "/EN Subs/fonts", root + "/RU Subs", root + "/RU Subs/Fonts" });
        var plan = RenamePlanner.Build(ctx, EpisodeMapper.Propose(inv, null, new Dictionary<string, int>()));

        Assert.Equal(
            ["/lib/Show (2020)/fonts/A.ttf", "/lib/Show (2020)/fonts/B.otf"],
            plan.Moves.Where(m => m.What == "font").Select(m => m.To).Order(StringComparer.Ordinal));
        Assert.Single(plan.Moves, m => m.What == "trash" && m.From.EndsWith("/A.ttf", StringComparison.Ordinal));
        Assert.Empty(PlanValidator.Validate(plan.Plan, ["/lib"]));
    }

    [Fact]
    public void KeepsTheDuplicateThatHasTracksOverABareOne()
    {
        const string root = "/lib/Show";
        string[] files =
        [
            root + "/Season 1/Show - S01E01 - Pilot WEBDL-1080p.mkv",
            root + "/[Grp] Show (BDRip)/[Grp]_Show_01_(BD_1080p).mkv",
            root + "/[Grp] Show (BDRip)/Rus Sub/[Grp]_Show_01_(BD_1080p).ass",
            root + "/[Grp] Show (BDRip)/Rus Sound/[Grp]_Show_01_(BD_1080p).mka",
        ];
        var heights = new Dictionary<string, int> { [files[0]] = 1080, [files[1]] = 1080 };

        var rows = EpisodeMapper.Propose(Inventory.Build(root, files), null, heights);

        Assert.Equal(RowAction.Episode, rows.Single(r => r.Video == files[1]).Action);
        Assert.Equal(RowAction.Skip, rows.Single(r => r.Video == files[0]).Action);
    }

    [Theory]
    [InlineData(false, "meta")]
    [InlineData(true, "trash")]
    public void SeriesMetadataOfAWrongMatchDoesNotFollowTheRelease(bool reidentified, string what)
    {
        const string root = "/lib/K-ON!";
        string[] files = [root + "/Season 1/K-ON! - S01E01.mkv", root + "/tvshow.nfo", root + "/poster.jpg"];
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "K-ON!", 2009, reidentified), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/Season 1" });

        var plan = RenamePlanner.Build(ctx, EpisodeMapper.Propose(inv, null, new Dictionary<string, int>()));

        Assert.All(plan.Moves.Where(m => m.From.EndsWith("tvshow.nfo", StringComparison.Ordinal) || m.From.EndsWith("poster.jpg", StringComparison.Ordinal)), m => Assert.Equal(what, m.What));
        Assert.Equal(2, plan.Moves.Count(m => m.What == what));
    }

    private static (RowAction, int?, int?) Key(RowDecision r) => (r.Action, r.Season, r.Episode);

    [Fact]
    public void EditedRowsThatPutTwoCopiesOnOneEpisodeAreAConflict()
    {
        const string root = "/lib/Show";
        string[] files = [root + "/Show - 01 [1080p].mkv", root + "/Other/Show - 01 [720p].mkv", root + "/Other/Rus/Show - 01 [720p].ass"];
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "Show", 2020), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/Other", root + "/Other/Rus" });
        var rows = inv.Videos.Select(v => new RowDecision(v.Path, RowAction.Episode, 1, 1, null, null, "edited")).ToList();

        var conflict = Assert.Single(RenamePlanner.Build(ctx, rows).Conflicts);

        Assert.Equal(RenamePlanner.DuplicateEpisode, conflict.Code);
        Assert.StartsWith("S01E01 is planned for 2 files: Other/Show - 01 [720p].mkv (1 tracks)", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EpisodesAlreadyInTheTargetFolderAreAConflict()
    {
        const string root = "/lib/K-ON!";
        string[] files = [root + "/Season 1/K-ON! - S01E01 - Abolition of the Club! Bluray-1080p.mkv", root + "/Season 2/K-ON! - S02E14 - Summer Classes! Bluray-720p.mp4"];
        var existing = files.Append("/lib/K-ON! (2009)/Season 01/K-ON! (2009) - S01E01 - Disband the Club! 1080p.mkv").ToHashSet(StringComparer.Ordinal);
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "K-ON!", 2009), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            existing, new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/Season 1", root + "/Season 2", "/lib/K-ON! (2009)", "/lib/K-ON! (2009)/Season 01" });

        var conflict = Assert.Single(RenamePlanner.Build(ctx, EpisodeMapper.Propose(inv, null, new Dictionary<string, int>())).Conflicts);

        Assert.Equal((RenamePlanner.EpisodeExists, files[0]), (conflict.Code, conflict.Path));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void LeftoversCanGoToTheTrashSoTheOldFolderDisappears(bool trashLeftovers, int left)
    {
        const string root = "/lib/Show";
        string[] files = [root + "/Season 1/Show - S01E01.mkv", root + "/Copy/Show - 01 [720p].mkv", root + "/Copy/Show - 01 [720p].ass", root + "/Scans/01.jpg"];
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "Show", 2020), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/Season 1", root + "/Copy", root + "/Scans" }, trashLeftovers);
        RowDecision[] rows =
        [
            new(files[0], RowAction.Episode, 1, 1, null, null, "SxxEyy"),
            new(files[1], RowAction.Skip, null, null, null, null, "duplicate"),
        ];

        var plan = RenamePlanner.Build(ctx, rows);

        Assert.Equal(left, plan.LeftInPlace.Count);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(trashLeftovers, plan.Plan.Operations.OfType<RemoveEmptyDirectory>().Any(d => d.Path == root));
        Assert.All(plan.Moves.Where(m => m.What == "trash"), m => Assert.StartsWith("/lib/.outer-files-trash/x/", m.To, StringComparison.Ordinal));
    }

    [Fact]
    public void ADoubleFileMakesSingleCopiesOfItsEpisodesDuplicates()
    {
        const string root = "/tv/Paw Patrol";
        string[] files =
        [
            root + "/Season 10/Paw Patrol - S10E25-E26 - Smiley's Cup + Baby Caribou WEBDL-1080p.mp4",
            root + "/Season 10/Paw Patrol - S10E25 - Smiley's Cup WEBDL-1080p.mp4",
            root + "/Season 10/Paw Patrol - S10E26 - Baby Caribou WEBDL-1080p.mp4",
            root + "/Season 10/Paw Patrol - S10E27 - Other WEBDL-1080p.mp4",
        ];
        var inv = Inventory.Build(root, files);

        var rows = EpisodeMapper.Propose(inv, null, new Dictionary<string, int>());

        Assert.Equal([RowAction.Episode, RowAction.Skip, RowAction.Skip, RowAction.Episode], files.Select(f => rows.Single(r => r.Video == f).Action));

        var ctx = new RenameContext(inv, new Identity(false, "PAW Patrol", 2013), "/tv", "/tv/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "/tv", root, root + "/Season 10" });
        var forced = rows.Select(r => r.Video == files[2] ? r with { Action = RowAction.Episode } : r).ToList();
        Assert.Equal(RenamePlanner.DuplicateEpisode, Assert.Single(RenamePlanner.Build(ctx, forced).Conflicts).Code);
    }

    [Fact]
    public void SeriesMetadataAlreadyInTheTargetFolderWins()
    {
        const string root = "/lib/Paw Patrol";
        string[] files = [root + "/Season 1/Paw Patrol - S01E01.mkv", root + "/tvshow.nfo"];
        var inv = Inventory.Build(root, files);
        var ctx = new RenameContext(inv, new Identity(false, "PAW Patrol", 2013), "/lib", "/lib/.outer-files-trash/x", Default,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, TrackDescriptor>(),
            files.Append("/lib/PAW Patrol (2013)/tvshow.nfo").ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "/lib", root, root + "/Season 1", "/lib/PAW Patrol (2013)" });

        var plan = RenamePlanner.Build(ctx, EpisodeMapper.Propose(inv, null, new Dictionary<string, int>()));

        Assert.Empty(plan.Conflicts);
        Assert.Equal("trash", plan.Moves.Single(m => m.From.EndsWith("tvshow.nfo", StringComparison.Ordinal)).What);
    }
}
