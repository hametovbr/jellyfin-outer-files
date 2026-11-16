using Jellyfin.Plugin.OuterFiles.Domain.Model;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// What to do with one video of a release.
/// </summary>
public enum RowAction
{
    /// <summary>Leave the file where it is.</summary>
    Skip,

    /// <summary>A regular episode.</summary>
    Episode,

    /// <summary>A special: season 0.</summary>
    Special,

    /// <summary>Not an episode (NCOP, menu, bonus): goes to <c>extras/</c>.</summary>
    Extra,

    /// <summary>The main file of a movie (several rows mean several versions).</summary>
    Movie,
}

/// <summary>
/// The decision for one video. Proposed by <see cref="EpisodeMapper"/>, edited by the user, consumed by
/// <see cref="RenamePlanner"/>.
/// </summary>
/// <param name="Video">Absolute video path.</param>
/// <param name="Action">What to do.</param>
/// <param name="Season">Season (episodes; 0 for specials).</param>
/// <param name="Episode">Episode.</param>
/// <param name="EpisodeEnd">Last episode of a multi-episode file.</param>
/// <param name="Part">Part number of a split episode (stacked by Jellyfin as one item), or null.</param>
/// <param name="Reason">Why this was proposed.</param>
public sealed record RowDecision(string Video, RowAction Action, int? Season, int? Episode, int? EpisodeEnd, int? Part, string Reason);

/// <summary>
/// Proposes a decision for every video of a series release from what the names say.
/// </summary>
public static class EpisodeMapper
{
    /// <summary>
    /// Proposes decisions.
    /// </summary>
    /// <param name="inventory">The release.</param>
    /// <param name="seasonEpisodeCounts">Episodes per season from the metadata source (index 0 = season 1), used for absolute numbers; null when unknown.</param>
    /// <param name="heights">Video heights from the library (for picking the better duplicate), may be empty.</param>
    /// <returns>One decision per video, in inventory order.</returns>
    public static IReadOnlyList<RowDecision> Propose(Inventory inventory, IReadOnlyList<int>? seasonEpisodeCounts, IReadOnlyDictionary<string, int> heights)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(heights);
        var rows = inventory.Videos.Select(v => ProposeOne(v, seasonEpisodeCounts)).ToList();
        return ResolveDuplicates(rows, inventory, heights);
    }

    /// <summary>
    /// Proposes rows from episode titles: files whose name carries a title take the matched episode (special
    /// for season 0); files with a title the source does not know are skipped with the reason; files without a
    /// title keep the usual proposal. Duplicates are resolved afterwards as usual.
    /// </summary>
    /// <param name="inventory">The release.</param>
    /// <param name="matches">Results of <see cref="TitleMatcher.Match"/>.</param>
    /// <param name="heights">Video heights from the library.</param>
    /// <returns>One decision per video, in inventory order.</returns>
    public static IReadOnlyList<RowDecision> ProposeByTitles(Inventory inventory, IReadOnlyList<TitleMatch> matches, IReadOnlyDictionary<string, int> heights)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(heights);
        var byVideo = matches.ToDictionary(m => m.Video, StringComparer.Ordinal);
        var shared = matches.Where(m => m.Match is not null)
            .GroupBy(m => (m.Match!.Season, m.Match.Episode))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .Select(m => m.Video)
            .ToHashSet(StringComparer.Ordinal);
        var rows = inventory.Videos.Select(v =>
        {
            var row = ProposeOne(v, null);
            if (!byVideo.TryGetValue(v.Path, out var m) || row.Action == RowAction.Extra)
            {
                return row;
            }

            var score = m.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (m.Match is not { } e)
            {
                return row with { Action = RowAction.Skip, Season = null, Episode = null, EpisodeEnd = null, Part = null, Reason = $"title \"{m.FileTitle}\" not found in the metadata source (best {score})" };
            }

            return new RowDecision(
                v.Path,
                e.Season == 0 ? RowAction.Special : RowAction.Episode,
                e.Season,
                e.Episode,
                null,
                shared.Contains(v.Path) ? m.Part : null,
                $"title \"{m.FileTitle}\" = \"{e.Title}\" ({score})");
        }).ToList();
        return ResolveDuplicates(rows, inventory, heights);
    }

    /// <summary>
    /// Converts an absolute episode number to season/episode with per-season counts.
    /// </summary>
    /// <param name="absolute">Absolute number (1-based).</param>
    /// <param name="counts">Episodes per season, season 1 first.</param>
    /// <returns>(season, episode) or null when the number is beyond the known seasons.</returns>
    public static (int Season, int Episode)? FromAbsolute(int absolute, IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var left = absolute;
        for (var s = 0; s < counts.Count; s++)
        {
            if (left <= counts[s])
            {
                return (s + 1, left);
            }

            left -= counts[s];
        }

        return null;
    }

    private static RowDecision ProposeOne(InventoryVideo video, IReadOnlyList<int>? counts)
    {
        var p = ReleaseNameParser.Parse(video.Stem);
        var folderSeason = video.Folders.Select(ReleaseNameParser.SeasonOfFolder).LastOrDefault(s => s is not null);
        var inExtrasFolder = video.Folders.Any(f => f.Equals("extras", StringComparison.OrdinalIgnoreCase) || f.Equals("bonus", StringComparison.OrdinalIgnoreCase)
            || f.Equals("NC", StringComparison.OrdinalIgnoreCase) || f.Contains("menu", StringComparison.OrdinalIgnoreCase) || f.Contains("scans", StringComparison.OrdinalIgnoreCase));

        if (p.Extra is not null || (inExtrasFolder && p.Episode is null))
        {
            return new RowDecision(video.Path, RowAction.Extra, null, null, null, null, p.Extra is null ? "in an extras folder" : $"extra ({p.Extra})");
        }

        if (p.IsSpecial)
        {
            return new RowDecision(video.Path, RowAction.Special, 0, p.Episode, p.EpisodeEnd, null, "special (" + p.Rule + ")");
        }

        if (p.Episode is null)
        {
            return folderSeason == 0
                ? new RowDecision(video.Path, RowAction.Extra, null, null, null, null, "unnumbered file in a specials folder")
                : new RowDecision(video.Path, RowAction.Skip, null, null, null, null, "no episode number in the name");
        }

        var part = p.Segment is { Length: 1 } seg ? seg[0] - 'a' + 1 : (int?)null;
        if (p.Season is not null)
        {
            return new RowDecision(video.Path, p.Season == 0 ? RowAction.Special : RowAction.Episode, p.Season, p.Episode, p.EpisodeEnd, part, p.Rule);
        }

        if (folderSeason == 0)
        {
            return new RowDecision(video.Path, RowAction.Special, 0, p.Episode, p.EpisodeEnd, part, p.Rule + ", specials folder");
        }

        if (folderSeason is not null)
        {
            return new RowDecision(video.Path, RowAction.Episode, folderSeason, p.Episode, p.EpisodeEnd, part, p.Rule + ", season from folder");
        }

        if (p.IsAbsolute && counts is { Count: > 0 } && FromAbsolute(p.Episode.Value, counts) is { } se)
        {
            return new RowDecision(video.Path, RowAction.Episode, se.Season, se.Episode, null, part, $"{p.Rule}, absolute {p.Episode} → S{se.Season:00}E{se.Episode:00}");
        }

        return new RowDecision(video.Path, RowAction.Episode, 1, p.Episode, p.EpisodeEnd, part, p.Rule + (p.IsAbsolute ? ", absolute number, season 1 assumed" : ", season 1 assumed"));
    }

    // Two files for the same episode: keep the one with more attached tracks (a BD release with dubs and subs
    // beats a bare WEB-DL), then the higher resolution, then the one already in a season folder; skip the rest
    // and say why. Parts of a split episode are not duplicates.
    private static List<RowDecision> ResolveDuplicates(List<RowDecision> rows, Inventory inventory, IReadOnlyDictionary<string, int> heights)
    {
        var relative = inventory.Videos.ToDictionary(v => v.Path, v => v.Relative, StringComparer.Ordinal);
        var tracks = inventory.Videos.ToDictionary(
            v => v.Path,
            v => v.OuterTracks.Count + v.Sidecars.Count(s => MediaKinds.Of(s.Path) is FileKind.Audio or FileKind.Subtitle),
            StringComparer.Ordinal);
        foreach (var group in rows.Where(r => r.Action is RowAction.Episode or RowAction.Special).GroupBy(r => (r.Season, r.Episode, r.Part)).Where(g => g.Count() > 1))
        {
            var best = group
                .OrderByDescending(r => tracks[r.Video])
                .ThenByDescending(r => heights.TryGetValue(r.Video, out var h) ? h : 0)
                .ThenByDescending(r => relative[r.Video].Contains("Season ", StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Video, StringComparer.Ordinal)
                .First();
            foreach (var r in group.Where(r => r != best))
            {
                var i = rows.IndexOf(r);
                rows[i] = r with { Action = RowAction.Skip, Reason = $"duplicate of {relative[best.Video]}" };
            }
        }

        return rows;
    }
}
