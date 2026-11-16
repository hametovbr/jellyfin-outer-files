using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

public sealed class OrganizeServiceTests
{
    private const string Rel = "tv/Jitsu wa Ore, Saikyou deshita [2023][BDRip][1080p]";

    [Fact]
    public async Task OrganizesARealTreeAndUndoesItToTheIdenticalTree()
    {
        using var tree = new TempTree();
        var root = tree.P(Rel);
        var videos = new List<ReleaseVideo>();
        for (var e = 1; e <= 3; e++)
        {
            var stem = $"[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [{e:00}][Ma10p_1080p][x265_flac]";
            videos.Add(new ReleaseVideo(Guid.NewGuid(), tree.File($"{Rel}/{stem}.mkv", "video" + e), null, 2023, 1080));
            tree.File($"{Rel}/{stem}.nfo", "<episodedetails><episode>2023</episodedetails>");
            tree.File($"{Rel}/{stem}-thumb.jpg");
            tree.File($"{Rel}/RU Sound [Anilibria]/{stem}.ru_Anilibria.mka");
            tree.File($"{Rel}/RU Sound [Anilibria]/надписи/{stem}.ru_signs_CR.ass");
            tree.File($"{Rel}/EN Subs/{stem}.en_CR.ass");
        }

        videos.Add(new ReleaseVideo(Guid.NewGuid(), tree.File($"{Rel}/SPs/[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [NCOP][Ma10p_1080p][x265_flac].mkv"), null, 2023, 1080));
        tree.File($"{Rel}/tvshow.nfo");
        tree.File($"{Rel}/poster.jpg");
        tree.File($"{Rel}/SPs/season.nfo");
        tree.File($"{Rel}/EN Subs/fonts/a.ttf");
        tree.File($"{Rel}/readme.txt");
        var before = tree.Snapshot("_data");

        var release = new ReleaseRef(root, tree.P("tv"), false, "Jitsu wa Ore, Saikyou deshita", 2023, new Dictionary<string, string>(), videos);
        var library = new FakeLibrary(tree.P("meta"), [tree.P("tv")], [], [release]);
        var naming = new NamingOptions();
        new NamingRulesService(naming, NullLogger<NamingRulesService>.Instance).Apply(true);
        var journals = new JournalStore(tree.P("_data/journals"));
        var service = new OrganizeService(
            library,
            new FakeMetadata((s, e) => s == 1 ? $"Title {e}: Part?" : null),
            new FileSystemReader(),
            new PlanExecutor(journals, NullLogger<PlanExecutor>.Instance),
            new OperationGate(),
            naming,
            NullLogger<OrganizeService>.Instance);
        var config = new PluginConfiguration();

        Assert.Contains(service.Candidates(config), c => c.Root == root && c.Problems.Contains("3 tracks in sub folders") is false);
        var analysis = service.Analyze(root)!;
        Assert.Equal(RowAction.Extra, analysis.Rows.Single(r => r.Relative.StartsWith("SPs/", StringComparison.Ordinal)).Action);
        Assert.Equal([1, 2, 3], analysis.Rows.Where(r => r.Action == RowAction.Episode).Select(r => r.Episode!.Value).Order());
        Assert.Equal("readme.txt", Assert.Single(analysis.Leftovers));

        var request = new PlanRequest(analysis.SessionId, false, "Am I Actually the Strongest?", 2023, new Dictionary<string, string> { ["Tmdb"] = "1" },
            analysis.Rows.Select(r => new PlanRow(r.Relative, r.Action, r.Season, r.Episode, r.EpisodeEnd, r.Part)).ToList());
        var plan = (await service.PlanAsync(request, config, CancellationToken.None))!;
        Assert.Empty(plan.Conflicts);
        Assert.Empty(plan.MissingTitles);
        Assert.Equal(tree.P("tv/Am I Actually the Strongest (2023)"), plan.TargetRoot);

        var applied = await service.ApplyAsync(analysis.SessionId, plan.Hash, CancellationToken.None);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.Equal(1, library.Scans);

        var target = plan.TargetRoot;
        var e1 = "Am I Actually the Strongest (2023) - S01E01 - Title 1 - Part 1080p";
        Assert.True(File.Exists(Path.Combine(target, "Season 01", e1 + ".mkv")));
        Assert.True(File.Exists(Path.Combine(target, "Season 01", e1 + ".Anilibria.rus.mka")));
        Assert.True(File.Exists(Path.Combine(target, "Season 01", e1 + ".CR.forced.rus.ass")));
        Assert.True(File.Exists(Path.Combine(target, "Season 01", e1 + ".CR.eng.ass")));
        Assert.True(File.Exists(Path.Combine(target, "Season 01", e1 + "-thumb.jpg")));
        Assert.False(File.Exists(Path.Combine(target, "Season 01", e1 + ".nfo")));
        Assert.True(File.Exists(Path.Combine(target, "extras", "[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [NCOP][Ma10p_1080p][x265_flac].mkv")));
        Assert.True(File.Exists(Path.Combine(target, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(target, "fonts", "a.ttf")));
        Assert.Equal(["readme.txt"], Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName));
        Assert.Single(Directory.EnumerateDirectories(tree.P("tv/.outer-files-trash")));

        // A second apply of the same session is refused.
        Assert.False((await service.ApplyAsync(analysis.SessionId, plan.Hash, CancellationToken.None)).Succeeded);

        var undo = await service.UndoAsync(applied.JournalId!, CancellationToken.None);
        Assert.True(undo.Succeeded, undo.Error);
        Assert.Equal(before, tree.Snapshot("_data", "tv/.outer-files-trash"));
    }

    [Fact]
    public async Task RefusesAPlanThatChangedOrHasConflicts()
    {
        using var tree = new TempTree();
        var root = tree.P("tv/Show");
        var a = tree.File("tv/Show/Show - 01 [1080p].mkv");
        var b = tree.File("tv/Show/Show - 1 [720p].mkv");
        tree.File("tv/Show (2020)/Season 01/Show (2020) - S01E01 1080p.mkv"); // occupied target
        var release = new ReleaseRef(root, tree.P("tv"), false, "Show", 2020, new Dictionary<string, string>(), [new(Guid.NewGuid(), a, null, 1, 1080), new(Guid.NewGuid(), b, null, 1, 720)]);
        var naming = new NamingOptions();
        var service = new OrganizeService(new FakeLibrary(tree.P("meta"), [tree.P("tv")], [], [release]), new FakeMetadata((_, _) => null), new FileSystemReader(),
            new PlanExecutor(new JournalStore(tree.P("_data/j")), NullLogger<PlanExecutor>.Instance), new OperationGate(), naming, NullLogger<OrganizeService>.Instance);

        var analysis = service.Analyze(root)!;
        Assert.Equal(RowAction.Skip, analysis.Rows.Single(r => r.Relative.Contains("720p", StringComparison.Ordinal)).Action);
        var rows = analysis.Rows.Select(r => new PlanRow(r.Relative, r.Action, r.Season, r.Episode, r.EpisodeEnd, r.Part)).ToList();
        var plan = (await service.PlanAsync(new PlanRequest(analysis.SessionId, false, "Show", 2020, new Dictionary<string, string>(), rows), new PluginConfiguration(), CancellationToken.None))!;
        Assert.True(plan.Conflicts.Any(c => c.Code == RenamePlanner.TargetExists), string.Join("\n", plan.Moves.Select(m => m.From + " -> " + m.To)) + "\n" + string.Join("\n", plan.Conflicts.Select(c => c.Code + " " + c.Path)));
        Assert.False((await service.ApplyAsync(analysis.SessionId, plan.Hash, CancellationToken.None)).Succeeded);
        Assert.False((await service.ApplyAsync(analysis.SessionId, "0000", CancellationToken.None)).Succeeded);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task NumbersARunOfSegmentsAndSpecialsByTheirTitles()
    {
        using var tree = new TempTree();
        const string rel = "tv/Приключения Джимми Нейтрона (Сезоны 1-3)";
        string[] names = ["JN.01.Когда атакуют штаны", "JN.02a.Обычный мальчик", "JN.02b.Рождение коммерсанта", "JN.39.Атака твонков (Часть 1)", "JN.40.Атака твонков (Часть 2)", "JN.41.Победи, проиграй и прощай"];
        var videos = names.Select(n => new ReleaseVideo(Guid.NewGuid(), tree.File($"{rel}/{n}.avi"), null, null, 480)).ToList();
        var release = new ReleaseRef(tree.P(rel), tree.P("tv"), false, "Jimmy", 2002, new Dictionary<string, string>(), videos);
        var titles = new Dictionary<(int, int), string> { [(1, 1)] = "Когда атакуют штаны", [(1, 2)] = "Обычный мальчик", [(1, 3)] = "Рождение коммерсанта", [(0, 2)] = "Атака твонков", [(0, 9)] = "Совсем другой спецвыпуск" };
        var service = new OrganizeService(new FakeLibrary(tree.P("meta"), [tree.P("tv")], [], [release]), new FakeMetadata((s, e) => titles.GetValueOrDefault((s, e))), new FileSystemReader(),
            new PlanExecutor(new JournalStore(tree.P("_data/j")), NullLogger<PlanExecutor>.Instance), new OperationGate(), new NamingOptions(), NullLogger<OrganizeService>.Instance);

        var analysis = service.Analyze(tree.P(rel))!;
        var byTitle = (await service.ProposeByTitlesAsync(analysis.SessionId, new Dictionary<string, string> { ["Tmdb"] = "2129" }, new PluginConfiguration(), CancellationToken.None))!
            .Rows.ToDictionary(r => r.Relative[..6].TrimEnd('.'), r => (r.Action, r.Season, r.Episode, r.Part));

        Assert.Equal((RowAction.Episode, 1, 1, (int?)null), byTitle["JN.01"]);
        Assert.Equal((RowAction.Episode, 1, 2, (int?)null), byTitle["JN.02a"]);
        Assert.Equal((RowAction.Episode, 1, 3, (int?)null), byTitle["JN.02b"]);
        Assert.Equal((RowAction.Special, 0, 2, (int?)1), byTitle["JN.39"]);
        Assert.Equal((RowAction.Special, 0, 2, (int?)2), byTitle["JN.40"]);
        Assert.Equal(RowAction.Skip, byTitle["JN.41"].Action);
    }

    [Fact]
    public void WhatAnOrganizeRunLeftBehindIsListedAgain()
    {
        using var tree = new TempTree();
        const string rel = "tv/Paw Patrol";
        var kept = tree.File($"{rel}/Season 1/Paw Patrol - S01E01.mkv");
        var copy = tree.File($"{rel}/Season 10/Paw Patrol - S10E25.mkv");
        var release = new ReleaseRef(tree.P(rel), tree.P("tv"), false, "Paw Patrol", null, new Dictionary<string, string>(), [new ReleaseVideo(Guid.NewGuid(), copy, 10, 25, 1080)]);
        var executor = new PlanExecutor(new JournalStore(tree.P("_data/j")), NullLogger<PlanExecutor>.Instance);
        var service = new OrganizeService(new FakeLibrary(tree.P("meta"), [tree.P("tv")], [], [release]), new FakeMetadata((_, _) => null), new FileSystemReader(),
            executor, new OperationGate(), new NamingOptions(), NullLogger<OrganizeService>.Instance);
        var config = new PluginConfiguration { SeriesFolderTemplate = "{Series Title}" };
        Assert.Empty(service.Candidates(config));

        var plan = new Plan("organize", [new CreateDirectory(tree.P("tv/PAW Patrol (2013)"), "x"), new MoveFile(kept, tree.P("tv/PAW Patrol (2013)/a.mkv"), "x")]);
        Assert.True(executor.Apply(plan, "organize", [tree.P("tv")], CancellationToken.None).Succeeded);

        Assert.Contains("leftovers of a release organized earlier (skipped copies)", Assert.Single(service.Candidates(config)).Problems);
    }

    [Theory]
    [InlineData("Tmdb", "238681", false)]
    [InlineData("tmdb", "42253", true)]
    [InlineData("Kinopoisk", "1", false)]
    public void ChosenIdsContradictOnlyWhenTheSameProviderPointsElsewhere(string provider, string id, bool expected) =>
        Assert.Equal(expected, OrganizeService.Contradicts(
            new Dictionary<string, string> { ["Tmdb"] = "238681", ["Tvdb"] = "87501" },
            new Dictionary<string, string> { [provider] = id }));
}
