using System;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.OuterFiles.Domain.Naming;
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
        Assert.Empty(service.FindMisnumbered([(Guid.NewGuid(), NamingRules.SelfTest[0].Path, null, 2023)]));
        service.Apply(true);

        var broken = Guid.NewGuid();
        var editedByHand = Guid.NewGuid();
        var unfixable = Guid.NewGuid();
        var fine = Guid.NewGuid();
        var fixes = service.FindMisnumbered(
        [
            (broken, NamingRules.SelfTest[0].Path, null, 2023),
            (editedByHand, NamingRules.SelfTest[0].Path, 0, 1),
            (unfixable, NamingRules.SelfTest[5].Path, 1, 3),
            (fine, NamingRules.SelfTest[6].Path, 1, 9),
        ]);

        var fix = Assert.Single(fixes);
        Assert.Equal((broken, (int?)7), (fix.Id, fix.NewEpisode));
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
}
