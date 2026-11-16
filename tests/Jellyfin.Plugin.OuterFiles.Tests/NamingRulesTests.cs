using System;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.OuterFiles.Domain.Naming;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

// Contract tests: they run Jellyfin's real Emby.Naming 12.4 (docs/ARCHITECTURE.md, P11).
public sealed class NamingRulesTests
{
    [Fact]
    public void SelfTestPassesOnTheRealJellyfinParser() =>
        Assert.Empty(NamingRulesService.SelfTest(NamingRulesService.Build(new NamingOptions().EpisodeExpressions)));

    [Fact]
    public void WithoutTheRulesJellyfinGetsTheseWrong()
    {
        var core = new EpisodeResolver(new NamingOptions());
        Assert.Equal(2023, core.Resolve(NamingRules.SelfTest[0].Path, false)!.EpisodeNumber);
        Assert.Null(core.Resolve(NamingRules.SelfTest[1].Path, false));
        Assert.Equal(2, core.Resolve(NamingRules.SelfTest[4].Path, false)!.EpisodeNumber);
        Assert.Equal(3, core.Resolve(NamingRules.SelfTest[5].Path, false)!.EpisodeNumber);
    }

    [Fact]
    public void InstallsIntoTheSharedOptionsAndRestoresThem()
    {
        var options = new NamingOptions();
        var original = options.EpisodeExpressions;
        var service = new NamingRulesService(options, NullLogger<NamingRulesService>.Instance);

        Assert.True(service.Apply(true).Installed);
        Assert.Equal(original.Length + NamingRules.Prepend.Count + NamingRules.Append.Count, options.EpisodeExpressions.Length);
        Assert.Equal(7, new EpisodeResolver(options).Resolve(NamingRules.SelfTest[0].Path, false)!.EpisodeNumber);
        Assert.Same(options.EpisodeExpressions, service.Apply(true) is { Installed: true } ? options.EpisodeExpressions : null);

        Assert.False(service.Apply(false).Installed);
        Assert.Same(original, options.EpisodeExpressions);
    }

    [Fact]
    public void ReNumbersOnlyEpisodesThatStillCarryTheBrokenNumber()
    {
        var options = new NamingOptions();
        var service = new NamingRulesService(options, NullLogger<NamingRulesService>.Instance);
        Assert.Empty(service.FindMisnumbered([new EpisodeRef(Guid.NewGuid(), NamingRules.SelfTest[0].Path, null, 2023, false)]));
        service.Apply(true);

        var broken = Guid.NewGuid();
        var editedByHand = Guid.NewGuid();
        var unnumbered = Guid.NewGuid();
        var keptByNfo = Guid.NewGuid();
        var fine = Guid.NewGuid();
        var alreadyRight = Guid.NewGuid();
        var fixes = service.FindMisnumbered(
        [
            new EpisodeRef(broken, NamingRules.SelfTest[0].Path, null, 2023, false),
            new EpisodeRef(editedByHand, NamingRules.SelfTest[0].Path, 0, 1, false),
            new EpisodeRef(unnumbered, NamingRules.SelfTest[5].Path, 1, 3, false),
            new EpisodeRef(keptByNfo, NamingRules.SelfTest[1].Path.Replace("_07_", "_08_", StringComparison.Ordinal), null, null, true),
            new EpisodeRef(keptByNfo, NamingRules.SelfTest[0].Path.Replace("[07]", "[08]", StringComparison.Ordinal), null, 2023, true),
            new EpisodeRef(fine, NamingRules.SelfTest[6].Path, 1, 9, false),
            new EpisodeRef(alreadyRight, "/tv/Show/Show [TV-2] [2021]/Show TV-2 - 02 (WEB-DL).mkv", 2, 2, false),
        ]).ToDictionary(f => f.Id);

        Assert.Equal(3, fixes.Count);
        Assert.Equal((RenumberAction.Refresh, (int?)7), (fixes[broken].Action, fixes[broken].NewEpisode));
        Assert.Equal((RenumberAction.Clear, true), (fixes[unnumbered].Action, fixes[unnumbered].ClearSeason));
        Assert.Equal(RenumberAction.StaleNfo, fixes[keptByNfo].Action);
    }

    /// <summary>
    /// Opt-in regression over a whole library: OUTER_FILES_VIDEOS=/path/to/list (one video path per line,
    /// as the server sees it). Prints every path whose numbers the rules change.
    /// </summary>
    [Fact]
    public void ReportsEveryChangeOnAFullLibrary()
    {
        var file = Environment.GetEnvironmentVariable("OUTER_FILES_VIDEOS");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return;
        }

        var core = new EpisodeResolver(new NamingOptions());
        var patched = new EpisodeResolver(new NamingOptions { EpisodeExpressions = NamingRulesService.Build(new NamingOptions().EpisodeExpressions) });
        var changes = File.ReadLines(file)
            .Select(p => (p, a: core.Resolve(p, false), b: patched.Resolve(p, false)))
            .Where(x => x.a?.EpisodeNumber != x.b?.EpisodeNumber || x.a?.SeasonNumber != x.b?.SeasonNumber)
            .ToList();
        foreach (var (p, a, b) in changes)
        {
            Console.WriteLine($"S{a?.SeasonNumber}E{a?.EpisodeNumber} -> S{b?.SeasonNumber}E{b?.EpisodeNumber}  {p}");
        }

        Console.WriteLine($"naming: {changes.Count} changed");
    }

    [Fact]
    public void ReportsNumbersThatContradictSxxEyyInTheName()
    {
        var found = NamingRulesService.FindContradicting(
        [
            new EpisodeRef(Guid.NewGuid(), "/tv/Hetalia/Season 1/Hetalia - Axis Powers - S01E27 - Germany's Diary DVD.mkv", 1, 1, false),
            new EpisodeRef(Guid.NewGuid(), "/tv/Hetalia/Season 1/Hetalia - Axis Powers - S01E01 - Meeting of the World DVD.mkv", 1, 1, false),
            new EpisodeRef(Guid.NewGuid(), "/tv/Paw Patrol/Season 7/Paw Patrol - S07E15-E16 - Mighty Pups.mkv", 7, 16, false),
            new EpisodeRef(Guid.NewGuid(), "/tv/Show/Season 1/Show - S01E03.mkv", 1, 9, false, Locked: true),
            new EpisodeRef(Guid.NewGuid(), "/tv/Show/Show 08 [BDRip].mkv", 1, 9, false),
            new EpisodeRef(Guid.NewGuid(), "/tv/Show/Season 1/Show - S01E04.mkv", 1, null, false),
        ]);

        var only = Assert.Single(found);
        Assert.Equal((1, 1, 1, 27), (only.StoredSeason, only.StoredEpisode, only.NamedSeason, only.NamedEpisode));
    }
}
