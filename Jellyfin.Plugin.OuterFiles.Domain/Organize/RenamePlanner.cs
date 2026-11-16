using System.Globalization;
using Jellyfin.Plugin.OuterFiles.Domain.Links;
using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// Naming templates (docs/ARCHITECTURE.md, section 7.2).
/// </summary>
/// <param name="SeriesFolder">Series folder.</param>
/// <param name="SeasonFolder">Season folder.</param>
/// <param name="EpisodeFile">Episode file, without extension.</param>
/// <param name="MovieFolder">Movie folder.</param>
/// <param name="MovieFile">Movie file, without extension.</param>
public sealed record Templates(string SeriesFolder, string SeasonFolder, string EpisodeFile, string MovieFolder, string MovieFile);

/// <summary>
/// The identity a release is organized as.
/// </summary>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Canonical title from the metadata source.</param>
/// <param name="Year">Premiere year.</param>
public sealed record Identity(bool IsMovie, string Title, int? Year);

/// <summary>
/// Everything the planner needs besides the decisions.
/// </summary>
/// <param name="Inventory">The release.</param>
/// <param name="Identity">What it is.</param>
/// <param name="LibraryFolder">Library folder the release lives in (its parent); the new folder is created here.</param>
/// <param name="TrashFolder">Where stale per-episode .nfo files go (inside the library, a dot-folder Jellyfin ignores).</param>
/// <param name="Templates">Templates.</param>
/// <param name="EpisodeTitles">Episode title per video path (missing → no title in the name).</param>
/// <param name="Qualities">Quality label per video path.</param>
/// <param name="Tracks">Resolved descriptor per track path (outer tracks and adjacent track sidecars).</param>
/// <param name="ExistingFiles">Files that exist now in the release and in the target folder.</param>
/// <param name="ExistingDirs">Folders that exist now (release, target, library folder).</param>
public sealed record RenameContext(
    Inventory Inventory,
    Identity Identity,
    string LibraryFolder,
    string TrashFolder,
    Templates Templates,
    IReadOnlyDictionary<string, string> EpisodeTitles,
    IReadOnlyDictionary<string, string> Qualities,
    IReadOnlyDictionary<string, TrackDescriptor> Tracks,
    IReadOnlySet<string> ExistingFiles,
    IReadOnlySet<string> ExistingDirs);

/// <summary>
/// One file move as shown to the user.
/// </summary>
/// <param name="From">Current path.</param>
/// <param name="To">New path.</param>
/// <param name="What">video, sidecar, track, meta, font, trash.</param>
public sealed record PlannedMove(string From, string To, string What);

/// <summary>
/// Result of planning.
/// </summary>
/// <param name="TargetRoot">New release folder.</param>
/// <param name="Plan">Operations.</param>
/// <param name="Moves">Moves for display.</param>
/// <param name="LeftInPlace">Files the plan does not touch.</param>
/// <param name="Conflicts">Reasons the plan must not be applied.</param>
/// <param name="VideoTargets">New path per moved video.</param>
public sealed record RenamePlan(string TargetRoot, Plan Plan, IReadOnlyList<PlannedMove> Moves, IReadOnlyList<string> LeftInPlace, IReadOnlyList<Conflict> Conflicts, IReadOnlyDictionary<string, string> VideoTargets);

/// <summary>
/// Turns decisions into a reversible plan: videos to the template, every related file with its video,
/// tracks from sub folders next to the video under names Jellyfin reads by itself, stale per-episode nfo
/// to the trash folder, emptied folders removed (docs/ARCHITECTURE.md, section 7.1 step 4).
/// </summary>
public static class RenamePlanner
{
    /// <summary>A planned file already exists and is not moved away by the plan.</summary>
    public const string TargetExists = "target-exists";

    /// <summary>
    /// Plans the reorganization of one release.
    /// </summary>
    /// <param name="context">Context.</param>
    /// <param name="decisions">One decision per video.</param>
    /// <returns>The plan.</returns>
    public static RenamePlan Build(RenameContext context, IReadOnlyList<RowDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decisions);
        var inv = context.Inventory;
        var id = context.Identity;
        var t = context.Templates;
        var root = inv.Root;
        var targetRoot = Path.Combine(context.LibraryFolder, NameTemplate.Render(id.IsMovie ? t.MovieFolder : t.SeriesFolder, Values(id)));

        var moves = new List<PlannedMove>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videoTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        var byVideo = inv.Videos.ToDictionary(v => v.Path, StringComparer.Ordinal);
        var movieRows = decisions.Count(d => d.Action == RowAction.Movie);

        foreach (var d in decisions.Where(d => d.Action != RowAction.Skip && byVideo.ContainsKey(d.Video)))
        {
            var video = byVideo[d.Video];
            var ext = Path.GetExtension(video.Path).ToLowerInvariant();
            var target = Unique(VideoTarget(context, targetRoot, d, video, movieRows > 1), ext, taken);
            taken.Add(target);
            videoTargets[video.Path] = target;
            moves.Add(new PlannedMove(video.Path, target, "video"));
            var dir = Path.GetDirectoryName(target)!;
            var newStem = Path.GetFileNameWithoutExtension(target);

            foreach (var sc in video.Sidecars)
            {
                if (sc.Suffix.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
                {
                    // Per-episode nfo files carry the old numbers; Jellyfin would read them back. Jellyfin
                    // writes fresh ones if the nfo saver is enabled.
                    moves.Add(new PlannedMove(sc.Path, Trash(context, sc.Path), "trash"));
                    continue;
                }

                var kind = MediaKinds.Of(sc.Path);
                if (kind is FileKind.Subtitle or FileKind.Audio)
                {
                    // Tracks get the same normalized names as tracks from sub folders:
                    // ".rus.19.srt" → ".rus.srt", ".en.hi.srt" → ".sdh.eng.srt", ".subrip.3.srt" → ".<guessed>.srt".
                    var trackStem = Path.GetFileNameWithoutExtension(sc.Path);
                    var descriptor = context.Tracks.TryGetValue(sc.Path, out var sd)
                        ? sd
                        : TrackDescriber.Describe(new TrackMatch(video.Path, sc.Path, kind, trackStem[Math.Min(video.Stem.Length, trackStem.Length)..], []));
                    var scExt = Path.GetExtension(sc.Path);
                    var name = LinkPlanner.LinkName(newStem, descriptor, scExt);
                    for (var n = 2; !taken.Add(Path.Combine(dir, name)); n++)
                    {
                        name = LinkPlanner.LinkName(newStem, descriptor, scExt, (string.IsNullOrEmpty(descriptor.Studio) ? "#" : " ") + n.ToString(CultureInfo.InvariantCulture));
                    }

                    moves.Add(new PlannedMove(sc.Path, Path.Combine(dir, name), "sidecar"));
                    continue;
                }

                var to = Path.Combine(dir, newStem + sc.Suffix);
                if (taken.Add(to))
                {
                    moves.Add(new PlannedMove(sc.Path, to, "sidecar"));
                }
            }

            foreach (var track in video.OuterTracks)
            {
                var descriptor = context.Tracks.TryGetValue(track.Track, out var td) ? td : TrackDescriber.Describe(track);
                var trackExt = Path.GetExtension(track.Track);
                var name = LinkPlanner.LinkName(newStem, descriptor, trackExt);
                for (var n = 2; !taken.Add(Path.Combine(dir, name)); n++)
                {
                    name = LinkPlanner.LinkName(newStem, descriptor, trackExt, (string.IsNullOrEmpty(descriptor.Studio) ? "#" : " ") + n.ToString(CultureInfo.InvariantCulture));
                }

                moves.Add(new PlannedMove(track.Track, Path.Combine(dir, name), "track"));
            }
        }

        foreach (var meta in inv.FolderMeta)
        {
            var atRoot = string.Equals(Path.GetDirectoryName(meta), root, StringComparison.Ordinal);
            if (atRoot && !string.Equals(targetRoot, root, StringComparison.Ordinal))
            {
                var to = Path.Combine(targetRoot, Path.GetFileName(meta));
                if (taken.Add(to))
                {
                    moves.Add(new PlannedMove(meta, to, "meta"));
                }
            }
            else if (!atRoot && moves.Any(m => m.What == "video" && IsUnder(m.From, Path.GetDirectoryName(meta)!)))
            {
                // Artwork/nfo of an old season or release folder that is being emptied.
                moves.Add(new PlannedMove(meta, Trash(context, meta), "trash"));
            }
        }

        foreach (var font in inv.Fonts)
        {
            // Releases ship the same fonts once per subtitle group (EN Subs/fonts, RU Subs/Fonts): one copy per
            // file name is kept, the others go to the trash folder like stale nfo files.
            var to = Path.Combine(targetRoot, "fonts", Path.GetFileName(font));
            if (!taken.Add(to))
            {
                moves.Add(new PlannedMove(font, Trash(context, font), "trash"));
                continue;
            }

            if (!string.Equals(font, to, StringComparison.Ordinal))
            {
                moves.Add(new PlannedMove(font, to, "font"));
            }
        }

        moves.RemoveAll(m => string.Equals(m.From, m.To, StringComparison.Ordinal));
        var movedFrom = moves.Select(m => m.From).ToHashSet(StringComparer.Ordinal);
        var allFiles = AllFiles(inv).ToList();
        var leftInPlace = allFiles.Where(f => !movedFrom.Contains(f)).ToList();

        var conflicts = new List<Conflict>();
        foreach (var m in moves.Where(m => context.ExistingFiles.Contains(m.To) && !movedFrom.Contains(m.To)))
        {
            conflicts.Add(new Conflict(TargetExists, m.To, "A different file already has this name."));
        }

        var ops = new List<Operation>();
        ops.AddRange(Mkdirs(moves.Select(m => Path.GetDirectoryName(m.To)!), context.ExistingDirs));
        ops.AddRange(MoveOps(moves));
        ops.AddRange(EmptyDirs(root, allFiles, movedFrom, moves.Select(m => m.To).ToList(), context.ExistingDirs, targetRoot));

        var title = $"Organize {Path.GetFileName(root)} as {Path.GetFileName(targetRoot)}";
        return new RenamePlan(targetRoot, new Plan(title, ops), moves, leftInPlace, conflicts, videoTargets);
    }

    private static string VideoTarget(RenameContext c, string targetRoot, RowDecision d, InventoryVideo video, bool versions)
    {
        var t = c.Templates;
        var values = Values(c.Identity);
        values["quality"] = c.Qualities.TryGetValue(video.Path, out var q) ? q : string.Empty;
        switch (d.Action)
        {
            case RowAction.Extra:
                return Path.Combine(targetRoot, "extras", Path.GetFileNameWithoutExtension(video.Path));

            case RowAction.Movie:
                var movie = NameTemplate.Render(t.MovieFile, values);
                if (versions)
                {
                    // Jellyfin groups versions named "<folder> - <label>".
                    movie = NameTemplate.Render(t.MovieFolder, values) + " - " + (string.IsNullOrEmpty(values["quality"] as string) ? "version" : values["quality"]);
                }

                return Path.Combine(targetRoot, movie);

            default:
                var season = d.Action == RowAction.Special ? 0 : d.Season ?? 1;
                values["season"] = season;
                values["episode"] = d.Episode ?? 0;
                values["end"] = d.EpisodeEnd is int end && end > (d.Episode ?? 0) ? end : null;
                values["episode title"] = string.Empty;
                var rest = NameTemplate.Render(t.EpisodeFile, values).Length;
                values["episode title"] = c.EpisodeTitles.TryGetValue(video.Path, out var et) ? NameTemplate.Fit(NameTemplate.Sanitize(et), rest + 3) : string.Empty;
                var name = NameTemplate.Render(t.EpisodeFile, values);
                if (d.Part is int part)
                {
                    name += " - pt" + part.ToString(CultureInfo.InvariantCulture);
                }

                var seasonFolder = NameTemplate.Render(t.SeasonFolder, new Dictionary<string, object?> { ["season"] = season });
                return Path.Combine(targetRoot, seasonFolder, name);
        }
    }

    private static Dictionary<string, object?> Values(Identity id) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["series title"] = id.IsMovie ? null : id.Title,
        ["movie title"] = id.IsMovie ? id.Title : null,
        ["title"] = id.Title,
        ["year"] = id.Year,
    };

    private static string Unique(string pathWithoutExt, string ext, HashSet<string> taken)
    {
        var candidate = pathWithoutExt + ext;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = pathWithoutExt + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext;
        }

        return candidate;
    }

    private static string Trash(RenameContext c, string path) =>
        Path.Combine(c.TrashFolder, Inventory.Relative(c.LibraryFolder, path));

    private static IEnumerable<string> AllFiles(Inventory inv) =>
        inv.Videos.SelectMany(v => new[] { v.Path }.Concat(v.Sidecars.Select(s => s.Path)).Concat(v.OuterTracks.Select(t => t.Track)))
            .Concat(inv.FolderMeta).Concat(inv.Fonts).Concat(inv.UnmatchedTracks).Concat(inv.Leftovers)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<Operation> Mkdirs(IEnumerable<string> dirs, IReadOnlySet<string> existing)
    {
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in dirs.Distinct(StringComparer.Ordinal))
        {
            for (var d = dir; !string.IsNullOrEmpty(d) && !existing.Contains(d); d = Path.GetDirectoryName(d))
            {
                needed.Add(d);
            }
        }

        return needed.OrderBy(d => d.Count(ch => ch == '/' || ch == '\\')).ThenBy(d => d, StringComparer.Ordinal)
            .Select(d => new CreateDirectory(d, "target folder"));
    }

    // A target that is another move's source would fail ("never overwrite"); such moves go through a
    // temporary name first, so swaps like E01↔E02 work.
    private static IEnumerable<Operation> MoveOps(List<PlannedMove> moves)
    {
        var sources = moves.Select(m => m.From).ToHashSet(StringComparer.Ordinal);
        var direct = new List<Operation>();
        var second = new List<Operation>();
        foreach (var m in moves)
        {
            if (sources.Contains(m.To))
            {
                var tmp = m.To + ".outer-files-tmp";
                direct.Add(new MoveFile(m.From, tmp, $"{m.What} (via temporary name)"));
                second.Add(new MoveFile(tmp, m.To, m.What));
            }
            else
            {
                direct.Add(new MoveFile(m.From, m.To, m.What));
            }
        }

        return direct.Concat(second);
    }

    private static IEnumerable<Operation> EmptyDirs(string root, List<string> allFiles, HashSet<string> movedFrom, List<string> targets, IReadOnlySet<string> existingDirs, string targetRoot)
    {
        // Folders below the release root that hold nothing once the moves are done (already-empty ones included).
        var remaining = allFiles.Where(f => !movedFrom.Contains(f)).ToList();
        var candidates = existingDirs.Where(d => IsUnder(d, root) || string.Equals(d, root, StringComparison.Ordinal)).ToList();
        return candidates
            .Where(d => !remaining.Any(f => IsUnder(f, d)))
            .Where(d => !targets.Any(t => IsUnder(t, d)) && !IsUnder(targetRoot, d) && !string.Equals(targetRoot, d, StringComparison.Ordinal))
            .OrderByDescending(d => d.Count(ch => ch == '/' || ch == '\\'))
            .ThenBy(d => d, StringComparer.Ordinal)
            .Select(d => new RemoveEmptyDirectory(d, "emptied by the move"));
    }

    private static bool IsUnder(string path, string dir) => PlanValidator.IsInside(path, dir) && !string.Equals(path, dir, StringComparison.Ordinal);
}
