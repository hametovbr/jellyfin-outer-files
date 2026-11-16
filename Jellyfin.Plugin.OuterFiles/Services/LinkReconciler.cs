using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Domain.Fonts;
using Jellyfin.Plugin.OuterFiles.Domain.Links;
using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.Services;

/// <summary>
/// Link mode: make the plugin-owned links in every item's internal metadata folder equal to the tracks found
/// in its release folder, then let Jellyfin re-probe the items that changed (docs/ARCHITECTURE.md, section 6).
/// </summary>
public sealed class LinkReconciler
{
    private readonly ILibrarySource _library;
    private readonly FileSystemReader _reader;
    private readonly PlanExecutor _executor;
    private readonly IRefresher _refresh;
    private readonly OperationGate _gate;
    private readonly ReportStore _reports;
    private readonly FallbackFonts _fonts;
    private readonly ILogger<LinkReconciler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkReconciler"/> class.
    /// </summary>
    /// <param name="library">Library gateway.</param>
    /// <param name="reader">File system reader.</param>
    /// <param name="executor">Plan executor.</param>
    /// <param name="refresh">Refresh gateway.</param>
    /// <param name="gate">Operation gate.</param>
    /// <param name="reports">Report store.</param>
    /// <param name="fonts">Fallback font folder.</param>
    /// <param name="logger">Logger.</param>
    public LinkReconciler(ILibrarySource library, FileSystemReader reader, PlanExecutor executor, IRefresher refresh, OperationGate gate, ReportStore reports, FallbackFonts fonts, ILogger<LinkReconciler> logger)
    {
        _fonts = fonts;
        _library = library;
        _reader = reader;
        _executor = executor;
        _refresh = refresh;
        _gate = gate;
        _reports = reports;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full reconciliation.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="progress">Progress (0-100), may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, or null when another operation was running.</returns>
    public async Task<LinkReport?> RunAsync(PluginConfiguration config, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (ran, report) = await _gate.TryRunAsync("links", ct => Task.Run(() => Run(config, progress, ct), ct), cancellationToken).ConfigureAwait(false);
        if (!ran)
        {
            _logger.LogInformation("Outer Files: link run skipped, {Operation} is running", _gate.Current);
            return null;
        }

        _reports.Save("links", report!);
        return report;
    }

    /// <summary>
    /// Shows what link mode would do for one video, without changing anything.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The preview, or null when the item is not a file-based video.</returns>
    public LinkPreview? Preview(Guid itemId, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var video = _library.Video(itemId);
        if (video is null)
        {
            return null;
        }

        var roots = _library.LibraryRoots();
        var files = _reader.Snapshot(video.ReleaseRoot);
        var match = StemMatcher.Match(files);
        var state = BuildState(video, match.Matches.Where(m => m.Video == video.Path), roots, config);
        var plan = LinkPlanner.Plan([state]).Plan;
        var stem = Path.GetFileNameWithoutExtension(video.Path);
        var hostHandled = match.HostHandled.Where(t => StemMatcher.Continues(Path.GetFileNameWithoutExtension(t), stem)).ToList();
        return new LinkPreview(video.Path, video.ReleaseRoot, state.Folder, state.Desired, state.Existing, hostHandled, plan.Operations);
    }

    private LinkReport Run(PluginConfiguration config, IProgress<double>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var roots = _library.LibraryRoots();
        var videos = _library.Videos();
        var errors = new List<string>();
        var states = new List<(VideoRef Video, VideoLinkState State)>();
        int matched = 0, hostHandled = 0;
        var unmatched = new List<string>();
        var fontFiles = new List<string>();

        var groups = videos.GroupBy(v => v.ReleaseRoot, StringComparer.Ordinal).ToList();
        var done = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var files = _reader.Snapshot(group.Key);
                fontFiles.AddRange(files.Where(f => FontCollector.Extensions.Contains(Path.GetExtension(f))));
                var match = StemMatcher.Match(files);
                matched += match.Matches.Count;
                hostHandled += match.HostHandled.Count;
                unmatched.AddRange(match.Unmatched);
                var byVideo = match.Matches.ToLookup(m => m.Video, StringComparer.Ordinal);
                foreach (var video in group)
                {
                    var state = BuildState(video, byVideo[video.Path], roots, config);
                    if (state.Desired.Count > 0 || state.Existing.Count > 0)
                    {
                        states.Add((video, state));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(group.Key + ": " + ex.Message);
            }

            progress?.Report(++done * 90.0 / Math.Max(1, groups.Count));
        }

        var (plan, changed) = LinkPlanner.Plan(states.Select(s => s.State));
        var fontState = FontState(config.CollectFonts ? FontCollector.Desire(fontFiles) : [], roots);
        var fontPlan = LinkPlanner.Plan([fontState]).Plan;
        plan = new Plan(plan.Title, plan.Operations.Concat(fontPlan.Operations).ToList());
        var result = _executor.Apply(plan, "links", [_library.InternalMetadataRoot, Path.GetDirectoryName(_fonts.Folder)!], ct);
        var fonts = result.Succeeded ? _fonts.Apply(config.CollectFonts, fontState.Desired) : null;
        if (result.Error is not null)
        {
            errors.Add(result.Error);
            errors.AddRange(result.Conflicts.Select(c => $"{c.Code}: {c.Path}"));
        }

        // Refresh only items whose links were fully applied; Jellyfin re-probes and picks the links up.
        var changedSet = changed.ToHashSet(StringComparer.Ordinal);
        var refreshed = 0;
        if (result.Succeeded)
        {
            foreach (var (video, _) in states.Where(s => changedSet.Contains(s.State.Folder)))
            {
                _refresh.QueueRefresh(video.Id);
                refreshed++;
            }
        }

        progress?.Report(100);
        var created = plan.Operations.Count(o => o is CreateSymlink);
        var removed = plan.Operations.Count(o => o is RemoveSymlink);
        _logger.LogInformation(
            "Outer Files: {Videos} videos, {Matched} outer tracks, {Created} links created, {Removed} removed, {Refreshed} items refreshed, {Unmatched} unmatched",
            videos.Count,
            matched,
            created,
            removed,
            refreshed,
            unmatched.Count);

        return new LinkReport(started, DateTime.UtcNow, videos.Count, matched, hostHandled, unmatched.Count, created, removed, refreshed, result.JournalId, unmatched.Take(200).ToList(), errors, fonts);
    }

    private VideoLinkState FontState(IReadOnlyList<DesiredLink> desired, IReadOnlyCollection<string> roots) => new(
        _fonts.Folder,
        desired.Count > 0 ? _reader.MissingFolders(_fonts.Folder, Path.GetDirectoryName(Path.GetDirectoryName(_fonts.Folder))!) : [],
        desired,
        _reader.OwnedLinks(_fonts.Folder, roots));

    private VideoLinkState BuildState(VideoRef video, IEnumerable<TrackMatch> matches, IReadOnlyCollection<string> roots, PluginConfiguration config)
    {
        var tracks = new List<(string Track, TrackDescriptor Descriptor)>();
        foreach (var m in matches)
        {
            var d = TrackDescriber.Describe(m);
            if (d.Language is null && m.Kind == FileKind.Subtitle && MediaKinds.TextSubtitle.Contains(Path.GetExtension(m.Track)))
            {
                d = d.WithFallbackLanguage(TextLanguageGuesser.Guess(_reader.ReadTextSample(m.Track), Path.GetExtension(m.Track)), "text");
            }

            if (m.Kind == FileKind.Audio)
            {
                d = d.WithFallbackLanguage(config.DefaultAudioLanguage, "default");
            }

            tracks.Add((m.Track, d));
        }

        var desired = LinkPlanner.Desire(Path.GetFileNameWithoutExtension(video.Path), tracks);
        var existing = _reader.OwnedLinks(video.InternalMetadataPath, roots);
        var missing = desired.Count > 0 ? _reader.MissingFolders(video.InternalMetadataPath, _library.InternalMetadataRoot) : [];
        return new VideoLinkState(video.InternalMetadataPath, missing, desired, existing);
    }
}

/// <summary>
/// Result of a link run.
/// </summary>
/// <param name="StartedUtc">Start.</param>
/// <param name="FinishedUtc">End.</param>
/// <param name="Videos">Videos inspected.</param>
/// <param name="OuterTracks">Tracks found outside their video's folder.</param>
/// <param name="HostHandled">Tracks next to their video (Jellyfin finds them itself).</param>
/// <param name="Unmatched">Tracks no video claims.</param>
/// <param name="Created">Links created.</param>
/// <param name="Removed">Links removed.</param>
/// <param name="Refreshed">Items queued for refresh.</param>
/// <param name="JournalId">Journal of the run, null when nothing changed.</param>
/// <param name="UnmatchedSample">Up to 200 unmatched tracks.</param>
/// <param name="Errors">Errors.</param>
/// <param name="Fonts">Fallback font state, null when the run failed.</param>
public sealed record LinkReport(DateTime StartedUtc, DateTime FinishedUtc, int Videos, int OuterTracks, int HostHandled, int Unmatched, int Created, int Removed, int Refreshed, string? JournalId, IReadOnlyList<string> UnmatchedSample, IReadOnlyList<string> Errors, FontReport? Fonts = null);

/// <summary>
/// Link mode preview for one video.
/// </summary>
/// <param name="Video">Video path.</param>
/// <param name="ReleaseRoot">Folder searched for tracks.</param>
/// <param name="LinkFolder">Internal metadata folder.</param>
/// <param name="Desired">Links that should exist, with reasons.</param>
/// <param name="Existing">Plugin links that exist now.</param>
/// <param name="HostHandled">Tracks next to the video that Jellyfin finds itself.</param>
/// <param name="Operations">What a run would change.</param>
public sealed record LinkPreview(string Video, string ReleaseRoot, string LinkFolder, IReadOnlyList<DesiredLink> Desired, IReadOnlyList<ExistingLink> Existing, IReadOnlyList<string> HostHandled, IReadOnlyList<Operation> Operations);
