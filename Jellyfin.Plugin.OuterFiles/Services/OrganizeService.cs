using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.Services;

/// <summary>
/// Organize mode (docs/ARCHITECTURE.md, section 7): analyze a release, identify it, map files to episodes,
/// plan the renames, apply them through the journaling executor and undo them on request.
/// Sessions live in memory; the plan the user reviewed is what gets applied (hash check).
/// </summary>
public sealed class OrganizeService
{
    private const int MaxSessions = 20;
    private readonly ILibrarySource _library;
    private readonly IMetadataSource _metadata;
    private readonly FileSystemReader _reader;
    private readonly PlanExecutor _executor;
    private readonly OperationGate _gate;
    private readonly NamingOptions _naming;
    private readonly ILogger<OrganizeService> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeService"/> class.
    /// </summary>
    /// <param name="library">Library source.</param>
    /// <param name="metadata">Metadata source.</param>
    /// <param name="reader">File system reader.</param>
    /// <param name="executor">Plan executor.</param>
    /// <param name="gate">Operation gate.</param>
    /// <param name="naming">The server's naming options (with the plugin's rules when installed).</param>
    /// <param name="logger">Logger.</param>
    public OrganizeService(ILibrarySource library, IMetadataSource metadata, FileSystemReader reader, PlanExecutor executor, OperationGate gate, NamingOptions naming, ILogger<OrganizeService> logger)
    {
        _library = library;
        _metadata = metadata;
        _reader = reader;
        _executor = executor;
        _gate = gate;
        _naming = naming;
        _logger = logger;
    }

    /// <summary>
    /// Lists releases that do not follow the templates or have known problems.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>Candidates, most problems first.</returns>
    public IReadOnlyList<Candidate> Candidates(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var templates = Templates(config);
        var result = new List<Candidate>();

        // Folders an earlier organize run moved files out of: what is still inside are skipped copies.
        var movedOut = _executor.AppliedMoves("organize")
            .Where(m => !m.Target.Contains("/.outer-files-trash/", StringComparison.Ordinal))
            .ToList();
        foreach (var release in _library.Releases())
        {
            var problems = new List<string>();
            if (movedOut.Any(m => PlanValidator.IsInside(m.Source, release.Root) && !PlanValidator.IsInside(m.Target, release.Root)))
            {
                problems.Add("leftovers of a release organized earlier (skipped copies)");
            }

            var expectedRoot = Path.Combine(release.LibraryFolder, NameTemplate.Render(release.IsMovie ? templates.MovieFolder : templates.SeriesFolder, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["series title"] = release.Title,
                ["movie title"] = release.Title,
                ["year"] = release.Year,
            }));
            // The folder name alone is not a reason to list a release: Jellyfin's title may be localized
            // ("Мстители: Эра Альтрона") while the folder is fine. It is shown next to real problems.
            var folderDiffers = !string.Equals(expectedRoot, release.Root, StringComparison.Ordinal);

            if (!release.IsMovie)
            {
                var unnumbered = release.Videos.Count(v => v.Episode is null);
                if (unnumbered > 0)
                {
                    problems.Add($"{unnumbered} without episode number");
                }

                var duplicates = release.Videos.Where(v => v.Episode is not null).GroupBy(v => (v.Season, v.Episode)).Count(g => g.Count() > 1);
                if (duplicates > 0)
                {
                    problems.Add($"{duplicates} duplicate episode numbers");
                }

                var outside = release.Videos.Count(v => !Path.GetFileName(Path.GetDirectoryName(v.Path) ?? string.Empty).StartsWith("Season ", StringComparison.Ordinal));
                if (outside > 0)
                {
                    problems.Add($"{outside} outside season folders");
                }
            }

            var inventory = Inventory.Build(release.Root, _reader.Snapshot(release.Root));
            var outer = inventory.Videos.Sum(v => v.OuterTracks.Count);
            if (outer > 0)
            {
                problems.Add($"{outer} tracks in sub folders");
            }

            var toolNamed = inventory.Videos.Sum(v => v.Sidecars.Count(sc =>
                MediaKinds.Of(sc.Path) is FileKind.Subtitle or FileKind.Audio
                && TrackDescriber.HasToolSuffix(Path.GetFileNameWithoutExtension(sc.Path)[Math.Min(v.Stem.Length, Path.GetFileNameWithoutExtension(sc.Path).Length)..])));
            if (toolNamed > 0)
            {
                problems.Add($"{toolNamed} tracks with tool-generated names");
            }

            if (problems.Count > 0)
            {
                if (folderDiffers)
                {
                    problems.Add("folder name differs from the template");
                }

                result.Add(new Candidate(release.Root, Path.GetFileName(release.Root), release.IsMovie, release.Title, release.Year, release.Videos.Count, problems));
            }
        }

        return result.OrderByDescending(c => c.Problems.Count).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Analyzes a release folder and proposes a mapping.
    /// </summary>
    /// <param name="root">Release folder.</param>
    /// <returns>The analysis, or null when the folder is not a known release.</returns>
    public Analysis? Analyze(string root)
    {
        var release = _library.Releases().FirstOrDefault(r => string.Equals(r.Root, root.TrimEnd('/', '\\'), StringComparison.Ordinal));
        if (release is null)
        {
            return null;
        }

        var inventory = Inventory.Build(release.Root, _reader.Snapshot(release.Root));
        var heights = release.Videos.Where(v => v.Height is not null).ToDictionary(v => v.Path, v => v.Height!.Value, StringComparer.Ordinal);
        var decisions = release.IsMovie ? ProposeMovie(inventory, heights) : EpisodeMapper.Propose(inventory, null, heights);
        var session = new Session(Guid.NewGuid().ToString("N"), release, inventory, heights);
        Remember(session);
        return session.View(decisions);
    }

    /// <summary>
    /// Re-proposes rows of a session, converting absolute numbers with season sizes from the metadata source.
    /// </summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new proposals, or null when the session is unknown.</returns>
    public async Task<Analysis?> ProposeWithAbsoluteNumbersAsync(string sessionId, IReadOnlyDictionary<string, string> seriesIds, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var counts = new List<int>();
        for (var season = 1; season <= 60 && !cancellationToken.IsCancellationRequested; season++)
        {
            var count = 0;
            while (count < 500 && await _metadata.EpisodeTitleAsync(seriesIds, season, count + 1, config.TitleLanguage, cancellationToken).ConfigureAwait(false) is not null)
            {
                count++;
            }

            if (count == 0)
            {
                break;
            }

            counts.Add(count);
        }

        return session.View(EpisodeMapper.Propose(session.Inventory, counts, session.Heights), counts);
    }

    /// <summary>
    /// Re-proposes rows of a session by the episode titles in the file names, matched against every episode
    /// and special of the series in the title language and, for Cyrillic names, in Russian.
    /// </summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new proposals, or null when the session is unknown.</returns>
    public async Task<Analysis?> ProposeByTitlesAsync(string sessionId, IReadOnlyDictionary<string, string> seriesIds, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var files = session.Inventory.Videos.Select(v => (v.Path, v.Stem)).ToList();
        var languages = new List<string> { config.TitleLanguage };
        if (files.Any(f => TitleMatcher.TitleOf(f.Stem) is { } t && t.Title.Any(c => c is >= '\u0400' and <= '\u04FF')))
        {
            languages.Add("ru");
        }

        var catalog = new List<CatalogEpisode>();
        var counts = new List<int>();
        foreach (var language in languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Specials are numbered with gaps (S00E17, S00E19): stop after several misses in a row.
            for (int episode = 1, misses = 0; episode <= 200 && misses < 6 && !cancellationToken.IsCancellationRequested; episode++)
            {
                var title = await _metadata.EpisodeTitleAsync(seriesIds, 0, episode, language, cancellationToken).ConfigureAwait(false);
                misses = title is null ? misses + 1 : 0;
                if (title is not null)
                {
                    catalog.Add(new CatalogEpisode(0, episode, title));
                }
            }

            var seasonCounts = new List<int>();
            for (var season = 1; season <= 60 && !cancellationToken.IsCancellationRequested; season++)
            {
                var count = 0;
                while (count < 500 && await _metadata.EpisodeTitleAsync(seriesIds, season, count + 1, language, cancellationToken).ConfigureAwait(false) is { } title)
                {
                    count++;
                    catalog.Add(new CatalogEpisode(season, count, title));
                }

                if (count == 0)
                {
                    break;
                }

                seasonCounts.Add(count);
            }

            if (counts.Count == 0)
            {
                counts = seasonCounts;
            }
        }

        if (catalog.Count == 0)
        {
            // Nothing to match against (no ids, no provider reachable): keep the usual proposal.
            _logger.LogWarning("Outer Files: no episode titles found for {Ids}", string.Join(", ", seriesIds.Select(kv => kv.Key + "=" + kv.Value)));
            return session.View(EpisodeMapper.Propose(session.Inventory, null, session.Heights), counts);
        }

        var matches = TitleMatcher.Match(files, catalog);
        var known = catalog.Select(c => (c.Season, c.Episode)).ToHashSet();
        return session.View(EpisodeMapper.ProposeByTitles(session.Inventory, matches, session.Heights, known), counts);
    }

    /// <summary>
    /// Searches the metadata providers.
    /// </summary>
    /// <param name="isMovie">Movie or series.</param>
    /// <param name="name">Name.</param>
    /// <param name="year">Year.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hits.</returns>
    public Task<IReadOnlyList<MetadataHit>> SearchAsync(bool isMovie, string name, int? year, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _metadata.SearchAsync(isMovie, name, year, config.TitleLanguage, cancellationToken);
    }

    /// <summary>
    /// Builds and validates the plan for a session.
    /// </summary>
    /// <param name="request">Identity and decisions.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan view, or null when the session is unknown.</returns>
    public async Task<PlanView?> PlanAsync(PlanRequest request, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        if (!_sessions.TryGetValue(request.SessionId, out var session))
        {
            return null;
        }

        var inventory = session.Inventory;
        var byRelative = inventory.Videos.ToDictionary(v => v.Relative, v => v.Path, StringComparer.Ordinal);
        var decisions = request.Rows
            .Where(r => byRelative.ContainsKey(r.Relative))
            .Select(r => new RowDecision(byRelative[r.Relative], r.Action, r.Season, r.Episode, r.EpisodeEnd, r.Part, "user"))
            .ToList();

        // Episode titles from the metadata source.
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingTitles = new List<string>();
        var titleMismatches = new List<string>();
        var titled = 0;
        var planned = new List<(string Video, string Code, string Title)>();
        var stems = inventory.Videos.ToDictionary(v => v.Path, v => v.Stem, StringComparer.Ordinal);
        if (!request.IsMovie && request.ProviderIds.Count > 0)
        {
            foreach (var d in decisions.Where(d => d.Action is RowAction.Episode or RowAction.Special && d.Episode is not null))
            {
                var season = d.Action == RowAction.Special ? 0 : d.Season ?? 1;
                var title = await _metadata.EpisodeTitleAsync(request.ProviderIds, season, d.Episode!.Value, config.TitleLanguage, cancellationToken).ConfigureAwait(false);
                if (title is null)
                {
                    missingTitles.Add($"S{season:00}E{d.Episode:00}");
                }
                else
                {
                    titles[d.Video] = title;

                    planned.Add((d.Video, $"S{season:00}E{d.Episode:00}", title));
                }
            }
        }

        // The title in a file name says which episode the file really is: it should fit the source's title for
        // its number, and not fit another planned episode clearly better (a release numbered by another source).
        foreach (var (video, code, title) in planned)
        {
            if (!stems.TryGetValue(video, out var stem) || TitleMatcher.TitleOf(stem) is not { } own)
            {
                continue;
            }

            titled++;
            var names = own.Title.Split(" + ").Append(own.Title).ToList();
            var here = names.Max(n => TitleMatcher.Similarity(n, title));
            var elsewhere = planned.Where(p => p.Video != video && p.Title != title)
                .Select(p => (p.Code, p.Title, Score: names.Max(n => TitleMatcher.Similarity(n, p.Title))))
                .OrderByDescending(p => p.Score)
                .FirstOrDefault();
            if (here < 0.9 && elsewhere.Score >= 0.85 && elsewhere.Score > here + 0.1)
            {
                titleMismatches.Add($"{code}: file \"{own.Title}\", source \"{title}\"; the source has \"{elsewhere.Title}\" as {elsewhere.Code}");
            }
            else if (here < 0.35)
            {
                titleMismatches.Add($"{code}: file \"{own.Title}\", source \"{title}\"");
            }
        }

        var qualities = inventory.Videos.ToDictionary(v => v.Path, v => QualityParser.Of(v.Stem, session.Heights.TryGetValue(v.Path, out var h) ? h : null), StringComparer.Ordinal);
        var tracks = new Dictionary<string, TrackDescriptor>(StringComparer.Ordinal);
        var adjacent = inventory.Videos.SelectMany(v => v.Sidecars
            .Where(sc => MediaKinds.Of(sc.Path) is FileKind.Subtitle or FileKind.Audio)
            .Select(sc => new TrackMatch(v.Path, sc.Path, MediaKinds.Of(sc.Path), Path.GetFileNameWithoutExtension(sc.Path)[Math.Min(v.Stem.Length, Path.GetFileNameWithoutExtension(sc.Path).Length)..], [])));
        foreach (var t in inventory.Videos.SelectMany(v => v.OuterTracks).Concat(adjacent))
        {
            var d = TrackDescriber.Describe(t);
            if (d.Language is null && t.Kind == FileKind.Subtitle && MediaKinds.TextSubtitle.Contains(Path.GetExtension(t.Track)))
            {
                d = d.WithFallbackLanguage(TextLanguageGuesser.Guess(_reader.ReadTextSample(t.Track), Path.GetExtension(t.Track)), "text");
            }

            tracks[t.Track] = t.Kind == FileKind.Audio ? d.WithFallbackLanguage(config.DefaultAudioLanguage, "default") : d;
        }

        var identity = new Identity(request.IsMovie, request.Title, request.Year, Contradicts(session.Release.ProviderIds, request.ProviderIds));
        var library = session.Release.LibraryFolder;
        var trash = Path.Combine(library, ".outer-files-trash", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        var templates = Templates(config);
        var probe = RenamePlanner.Build(Context(session, identity, library, trash, templates, titles, qualities, tracks, new HashSet<string>(), new HashSet<string>(), request.TrashLeftovers), decisions);

        // Real file system state around the source and target folders.
        var existingFiles = new HashSet<string>(_reader.Snapshot(inventory.Root), StringComparer.Ordinal);
        existingFiles.UnionWith(_reader.Snapshot(probe.TargetRoot));
        var existingDirs = new HashSet<string>(StringComparer.Ordinal) { library };
        foreach (var dir in new[] { inventory.Root, probe.TargetRoot }.Where(Directory.Exists))
        {
            existingDirs.Add(dir);
            existingDirs.UnionWith(Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }));
        }

        var plan = RenamePlanner.Build(Context(session, identity, library, trash, templates, titles, qualities, tracks, existingFiles, existingDirs, request.TrashLeftovers), decisions);
        var conflicts = plan.Conflicts.Concat(PlanValidator.Validate(plan.Plan, [library])).ToList();
        conflicts.AddRange(decisions
            .Where(d => d.Action is RowAction.Episode or RowAction.Special && d.Episode is null)
            .Select(d => new Conflict("missing-number", d.Video, "An episode needs an episode number.")));
        conflicts.AddRange(ParseBack(plan, decisions));
        if (titleMismatches.Count >= 5 && titleMismatches.Count * 3 >= titled)
        {
            conflicts.Add(new Conflict(
                "numbering-mismatch",
                inventory.Root,
                $"{titleMismatches.Count} of {titled} episode titles in the file names differ from the source's titles for the same numbers: the release is numbered differently from this source (e.g. TVDB order against TMDb). Use \"Match by episode titles\", or pick the series from the source the files were numbered by."));
        }

        session.Plan = plan;
        session.Library = library;
        return new PlanView(
            session.Id,
            plan.Plan.Hash,
            plan.TargetRoot,
            plan.Moves.Select(m => new MoveView(Inventory.Relative(library, m.From), Inventory.Relative(library, m.To), m.What)).ToList(),
            plan.Plan.Operations.Count,
            plan.LeftInPlace.Select(f => Inventory.Relative(library, f)).ToList(),
            conflicts,
            missingTitles,
            titleMismatches);
    }

    /// <summary>
    /// Applies the reviewed plan of a session.
    /// </summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="hash">Hash of the plan the user reviewed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<ExecutionResult> ApplyAsync(string sessionId, string hash, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Plan is null || session.Library is null)
        {
            return new ExecutionResult(null, 0, 0, [], "No plan for this session; build the plan first.");
        }

        if (!string.Equals(session.Plan.Plan.Hash, hash, StringComparison.Ordinal))
        {
            return new ExecutionResult(null, 0, 0, [], "The plan changed since it was shown; review it again.");
        }

        if (session.Plan.Conflicts.Count > 0)
        {
            return new ExecutionResult(null, 0, 0, session.Plan.Conflicts, "The plan has conflicts.");
        }

        var plan = session.Plan.Plan;
        var library = session.Library;
        var (ran, result) = await _gate.TryRunAsync("organize", ct => Task.Run(() => _executor.Apply(plan, "organize", [library], ct), ct), cancellationToken).ConfigureAwait(false);
        if (!ran)
        {
            return new ExecutionResult(null, 0, 0, [], $"Another operation is running ({_gate.Current}); try again in a moment.");
        }

        _logger.LogInformation("Outer Files: organized {Root}: {Done} steps, error: {Error}", session.Release.Root, result!.Done, result.Error ?? "none");
        _library.QueueLibraryScan();
        _sessions.TryRemove(sessionId, out _);
        return result;
    }

    /// <summary>
    /// Undoes an organize run.
    /// </summary>
    /// <param name="journalId">Journal of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<ExecutionResult> UndoAsync(string journalId, CancellationToken cancellationToken)
    {
        var roots = _library.LibraryRoots();
        var (ran, result) = await _gate.TryRunAsync("undo", ct => Task.Run(() => _executor.Undo(journalId, roots, ct), ct), cancellationToken).ConfigureAwait(false);
        if (!ran)
        {
            return new ExecutionResult(null, 0, 0, [], $"Another operation is running ({_gate.Current}); try again in a moment.");
        }

        _library.QueueLibraryScan();
        return result!;
    }

    // True when an id of the same provider points to another entry (Jellyfin matched K-ON! as "K-Pop ON!").
    internal static bool Contradicts(IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, string> chosen)
        => chosen.Any(c => known.Any(k => string.Equals(k.Key, c.Key, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(k.Value, c.Value, StringComparison.OrdinalIgnoreCase)));

    private static Templates Templates(PluginConfiguration c) =>
        new(c.SeriesFolderTemplate, c.SeasonFolderTemplate, c.EpisodeFileTemplate, c.MovieFolderTemplate, c.MovieFileTemplate);

    private static RenameContext Context(Session s, Identity identity, string library, string trash, Templates templates, Dictionary<string, string> titles, Dictionary<string, string> qualities, Dictionary<string, TrackDescriptor> tracks, IReadOnlySet<string> files, IReadOnlySet<string> dirs, bool trashLeftovers) =>
        new(s.Inventory, identity, library, trash, templates, titles, qualities, tracks, files, dirs, trashLeftovers);

    private static List<RowDecision> ProposeMovie(Inventory inventory, IReadOnlyDictionary<string, int> heights)
    {
        // The biggest-resolution files whose folder is the movie folder are the movie (versions); the rest extras.
        return inventory.Videos.Select(v =>
        {
            var p = ReleaseNameParser.Parse(v.Stem);
            var inRoot = v.Folders.Count == 0;
            return p.Extra is null && inRoot
                ? new RowDecision(v.Path, RowAction.Movie, null, null, null, null, "movie file")
                : new RowDecision(v.Path, RowAction.Extra, null, null, null, null, p.Extra is null ? "in a sub folder" : $"extra ({p.Extra})");
        }).ToList();
    }

    // Every new episode name must read back as the intended season/episode with the server's own parser
    // (and the plugin's rules when installed): the organized folder must not depend on anything else.
    private List<Conflict> ParseBack(RenamePlan plan, IReadOnlyList<RowDecision> decisions)
    {
        var resolver = new EpisodeResolver(_naming);
        var conflicts = new List<Conflict>();
        foreach (var d in decisions.Where(d => d.Action is RowAction.Episode or RowAction.Special && plan.VideoTargets.ContainsKey(d.Video)))
        {
            var target = plan.VideoTargets[d.Video];
            var r = resolver.Resolve(target, false, null, null, null);
            var season = d.Action == RowAction.Special ? 0 : d.Season ?? 1;
            if (r?.EpisodeNumber != d.Episode || r?.SeasonNumber != season)
            {
                conflicts.Add(new Conflict("parse-back", target, $"Jellyfin would read S{r?.SeasonNumber}E{r?.EpisodeNumber} instead of S{season:00}E{d.Episode:00}."));
            }
        }

        return conflicts;
    }

    private void Remember(Session session)
    {
        foreach (var old in _sessions.Values.OrderBy(s => s.Created).Take(Math.Max(0, _sessions.Count - MaxSessions + 1)))
        {
            _sessions.TryRemove(old.Id, out _);
        }

        _sessions[session.Id] = session;
    }

    private sealed class Session(string id, ReleaseRef release, Inventory inventory, Dictionary<string, int> heights)
    {
        public string Id { get; } = id;

        public DateTime Created { get; } = DateTime.UtcNow;

        public ReleaseRef Release { get; } = release;

        public Inventory Inventory { get; } = inventory;

        public Dictionary<string, int> Heights { get; } = heights;

        public RenamePlan? Plan { get; set; }

        public string? Library { get; set; }

        public Analysis View(IReadOnlyList<RowDecision> decisions, IReadOnlyList<int>? seasonCounts = null)
        {
            var stored = Release.Videos.ToDictionary(v => v.Path, StringComparer.Ordinal);
            var rows = Inventory.Videos.Zip(decisions).Select(x => new AnalysisRow(
                x.First.Relative,
                x.First.Sidecars.Count,
                x.First.OuterTracks.Count,
                stored.TryGetValue(x.First.Path, out var s) ? s.Season : null,
                stored.TryGetValue(x.First.Path, out var s2) ? s2.Episode : null,
                x.Second.Action,
                x.Second.Season,
                x.Second.Episode,
                x.Second.EpisodeEnd,
                x.Second.Part,
                x.Second.Reason)).ToList();
            return new Analysis(
                Id,
                Release.Root,
                Release.IsMovie,
                Release.Title,
                Release.Year,
                Release.ProviderIds,
                rows,
                Inventory.UnmatchedTracks.Select(f => Domain.Organize.Inventory.Relative(Release.Root, f)).ToList(),
                Inventory.Leftovers.Select(f => Domain.Organize.Inventory.Relative(Release.Root, f)).ToList(),
                seasonCounts ?? []);
        }
    }
}

/// <summary>A release with problems.</summary>
/// <param name="Root">Folder.</param>
/// <param name="Name">Folder name.</param>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Title Jellyfin knows.</param>
/// <param name="Year">Year Jellyfin knows.</param>
/// <param name="Videos">Number of videos.</param>
/// <param name="Problems">What is wrong.</param>
public sealed record Candidate(string Root, string Name, bool IsMovie, string Title, int? Year, int Videos, IReadOnlyList<string> Problems);

/// <summary>Analysis of a release.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="Root">Folder.</param>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Title Jellyfin knows.</param>
/// <param name="Year">Year Jellyfin knows.</param>
/// <param name="ProviderIds">Provider ids Jellyfin knows.</param>
/// <param name="Rows">One row per video.</param>
/// <param name="UnmatchedTracks">Tracks without a video.</param>
/// <param name="Leftovers">Other files left in place.</param>
/// <param name="SeasonCounts">Episodes per season used for absolute numbers.</param>
public sealed record Analysis(string SessionId, string Root, bool IsMovie, string Title, int? Year, IReadOnlyDictionary<string, string> ProviderIds, IReadOnlyList<AnalysisRow> Rows, IReadOnlyList<string> UnmatchedTracks, IReadOnlyList<string> Leftovers, IReadOnlyList<int> SeasonCounts);

/// <summary>One video of an analysis.</summary>
/// <param name="Relative">Path relative to the release folder.</param>
/// <param name="Sidecars">Files next to it.</param>
/// <param name="OuterTracks">Tracks in sub folders.</param>
/// <param name="StoredSeason">Season Jellyfin stores now.</param>
/// <param name="StoredEpisode">Episode Jellyfin stores now.</param>
/// <param name="Action">Proposed action.</param>
/// <param name="Season">Proposed season.</param>
/// <param name="Episode">Proposed episode.</param>
/// <param name="EpisodeEnd">Proposed last episode.</param>
/// <param name="Part">Proposed part.</param>
/// <param name="Reason">Why.</param>
public sealed record AnalysisRow(string Relative, int Sidecars, int OuterTracks, int? StoredSeason, int? StoredEpisode, RowAction Action, int? Season, int? Episode, int? EpisodeEnd, int? Part, string Reason);

/// <summary>What the user decided.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Canonical title.</param>
/// <param name="Year">Year.</param>
/// <param name="ProviderIds">Provider ids for episode titles.</param>
/// <param name="Rows">Decisions by relative path.</param>
/// <param name="TrashLeftovers">Also move what would stay in the old folder (skipped copies, scans, old nfo) to the trash folder.</param>
public sealed record PlanRequest(string SessionId, bool IsMovie, string Title, int? Year, IReadOnlyDictionary<string, string> ProviderIds, IReadOnlyList<PlanRow> Rows, bool TrashLeftovers = false);

/// <summary>A decision for one video.</summary>
/// <param name="Relative">Path relative to the release folder.</param>
/// <param name="Action">Action.</param>
/// <param name="Season">Season.</param>
/// <param name="Episode">Episode.</param>
/// <param name="EpisodeEnd">Last episode.</param>
/// <param name="Part">Part.</param>
public sealed record PlanRow(string Relative, RowAction Action, int? Season, int? Episode, int? EpisodeEnd, int? Part);

/// <summary>A plan as shown to the user.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="Hash">Hash to confirm.</param>
/// <param name="TargetRoot">New folder.</param>
/// <param name="Moves">Moves, paths relative to the library folder.</param>
/// <param name="Operations">Number of operations.</param>
/// <param name="LeftInPlace">Files not touched.</param>
/// <param name="Conflicts">Blocking problems.</param>
/// <param name="MissingTitles">Episodes the metadata source does not know.</param>
/// <param name="TitleMismatches">Episodes whose title in the file name differs from the source's title for that number.</param>
public sealed record PlanView(string SessionId, string Hash, string TargetRoot, IReadOnlyList<MoveView> Moves, int Operations, IReadOnlyList<string> LeftInPlace, IReadOnlyList<Conflict> Conflicts, IReadOnlyList<string> MissingTitles, IReadOnlyList<string>? TitleMismatches = null);

/// <summary>A move for display.</summary>
/// <param name="From">From (relative to the library folder).</param>
/// <param name="To">To (relative to the library folder).</param>
/// <param name="What">Kind.</param>
public sealed record MoveView(string From, string To, string What);
