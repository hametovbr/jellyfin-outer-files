using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.OuterFiles.Domain.Naming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.Services;

/// <summary>
/// Installs <see cref="NamingRules"/> into Jellyfin's shared <see cref="NamingOptions"/>. This is the one
/// deliberate change to global host state (docs/ARCHITECTURE.md, section 6.4), so it is fenced: a self-test
/// runs on the server's own parser before anything is installed, the original expressions are kept and put
/// back when the feature is switched off, and the state is visible on the status page.
/// </summary>
public sealed class NamingRulesService
{
    private readonly NamingOptions _options;
    private readonly ILogger<NamingRulesService> _logger;
    private readonly object _lock = new();
    private EpisodeExpression[]? _original;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamingRulesService"/> class.
    /// </summary>
    /// <param name="options">The server's naming options (singleton).</param>
    /// <param name="logger">Logger.</param>
    public NamingRulesService(NamingOptions options, ILogger<NamingRulesService> logger)
    {
        _options = options;
        _logger = logger;
        State = new NamingRulesState(false, "Not installed yet.", 0, []);
    }

    /// <summary>
    /// Gets the current state.
    /// </summary>
    public NamingRulesState State { get; private set; }

    /// <summary>
    /// Installs or removes the rules.
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    /// <returns>The resulting state.</returns>
    public NamingRulesState Apply(bool enabled)
    {
        lock (_lock)
        {
            if (!enabled)
            {
                if (_original is not null)
                {
                    _options.EpisodeExpressions = _original;
                    _original = null;
                    _logger.LogInformation("Outer Files: naming rules removed, Jellyfin's own episode expressions restored");
                }

                return State = new NamingRulesState(false, "Disabled in settings.", 0, []);
            }

            if (_original is not null)
            {
                return State;
            }

            var core = _options.EpisodeExpressions;
            var patched = Build(core);
            var failures = SelfTest(patched);
            if (failures.Count > 0)
            {
                _logger.LogWarning("Outer Files: naming rules NOT installed, self-test failed: {Failures}", string.Join("; ", failures));
                return State = new NamingRulesState(false, "Self-test failed on this server, rules not installed.", 0, failures);
            }

            _original = core;
            _options.EpisodeExpressions = patched;
            var confined = core.Count(e => !NamingRules.IsPathAware(e.Expression));
            _logger.LogInformation("Outer Files: naming rules installed ({Confined} core expressions confined to file names, {Added} added)", confined, NamingRules.Prepend.Count + NamingRules.Append.Count);
            return State = new NamingRulesState(true, $"Installed: {confined} core expressions confined to file names, {NamingRules.Prepend.Count + NamingRules.Append.Count} added.", patched.Length, []);
        }
    }

    /// <summary>
    /// Finds episodes whose stored numbers are what Jellyfin's original expressions produce from the path,
    /// while the installed rules produce something else. Those numbers are known to be wrong and were never
    /// edited by hand (a manual edit would differ from the original parse).
    /// </summary>
    /// <param name="episodes">Episodes with their stored numbers.</param>
    /// <returns>Episodes to re-number.</returns>
    public IReadOnlyList<Renumbering> FindMisnumbered(IEnumerable<(Guid Id, string Path, int? Season, int? Episode)> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        EpisodeExpression[] original;
        lock (_lock)
        {
            if (_original is null)
            {
                return [];
            }

            original = _original;
        }

        var before = new EpisodeResolver(new NamingOptions { EpisodeExpressions = original });
        var after = new EpisodeResolver(_options);
        var result = new List<Renumbering>();
        foreach (var (id, path, season, episode) in episodes)
        {
            var old = before.Resolve(path, false, null, null, null);
            var now = after.Resolve(path, false, null, null, null);
            if (old?.EpisodeNumber == now?.EpisodeNumber && old?.SeasonNumber == now?.SeasonNumber)
            {
                continue;
            }

            // Stored values still equal the broken parse: nobody fixed them by hand. When the rules find no
            // number at all, Jellyfin keeps the stored one anyway, so only real corrections are returned.
            if (now?.EpisodeNumber is not null && episode == old?.EpisodeNumber && (season == old?.SeasonNumber || old?.SeasonNumber is null))
            {
                result.Add(new Renumbering(id, path, season, episode, now?.SeasonNumber, now?.EpisodeNumber));
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the patched expression list from the server's own list.
    /// </summary>
    /// <param name="core">The server's expressions.</param>
    /// <returns>The patched list.</returns>
    internal static EpisodeExpression[] Build(IEnumerable<EpisodeExpression> core) =>
        NamingRules.Prepend.Select(Named)
            .Concat(core.Select(Confine))
            .Concat(NamingRules.Append.Select(Named))
            .ToArray();

    /// <summary>
    /// Runs <see cref="NamingRules.SelfTest"/> against a patched list.
    /// </summary>
    /// <param name="patched">Patched expressions.</param>
    /// <returns>Descriptions of failed cases.</returns>
    internal static List<string> SelfTest(EpisodeExpression[] patched)
    {
        var resolver = new EpisodeResolver(new NamingOptions { EpisodeExpressions = patched });
        var failures = new List<string>();
        foreach (var c in NamingRules.SelfTest)
        {
            var r = resolver.Resolve(c.Path, false, null, null, null);
            var season = c.Episode is null ? null : r?.SeasonNumber;
            if (r?.EpisodeNumber != c.Episode || (c.Season is not null && season != c.Season))
            {
                failures.Add($"{System.IO.Path.GetFileName(c.Path)}: expected S{c.Season}E{c.Episode}, got S{r?.SeasonNumber}E{r?.EpisodeNumber}");
            }
        }

        return failures;
    }

    private static EpisodeExpression Named(EpisodeRule rule) => new(rule.Expression) { IsNamed = true };

    private static EpisodeExpression Confine(EpisodeExpression e) => NamingRules.IsPathAware(e.Expression)
        ? e
        : new EpisodeExpression(NamingRules.ConfineToFileName(e.Expression), e.IsByDate)
        {
            IsNamed = e.IsNamed,
            IsOptimistic = e.IsOptimistic,
            SupportsAbsoluteEpisodeNumbers = e.SupportsAbsoluteEpisodeNumbers,
            DateTimeFormats = e.DateTimeFormats,
        };
}

/// <summary>
/// State of the naming rules.
/// </summary>
/// <param name="Installed">Whether the rules are active.</param>
/// <param name="Message">Human readable state.</param>
/// <param name="Expressions">Number of active episode expressions.</param>
/// <param name="Failures">Failed self-test cases.</param>
public sealed record NamingRulesState(bool Installed, string Message, int Expressions, IReadOnlyList<string> Failures);

/// <summary>
/// An episode whose numbers the rules change.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="Path">Path.</param>
/// <param name="OldSeason">Stored season.</param>
/// <param name="OldEpisode">Stored episode.</param>
/// <param name="NewSeason">Season with the rules.</param>
/// <param name="NewEpisode">Episode with the rules.</param>
public sealed record Renumbering(Guid Id, string Path, int? OldSeason, int? OldEpisode, int? NewSeason, int? NewEpisode);
