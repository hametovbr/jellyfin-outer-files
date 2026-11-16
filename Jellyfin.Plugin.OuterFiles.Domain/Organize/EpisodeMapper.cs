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
    /// <param name="known">Season/episode pairs the metadata source has; a file named <c>SxxEyy</c> (season 1+)
    /// whose title is not found keeps that number when the source has it and no title match took it (the
    /// release and the source titled the episode differently: "Abolition of the Club!" / "Disband the Club!").
    /// Null: any number.</param>
    /// <returns>One decision per video, in inventory order.</returns>
    public static IReadOnlyList<RowDecision> ProposeByTitles(Inventory inventory, IReadOnlyList<TitleMatch> matches, IReadOnlyDictionary<string, int> heights, IReadOnlySet<(int Season, int Episode)>? known = null)
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
        // A file without a title that carries the same number as a titled copy (Henshin "sp_01" and Sonarr's
        // "S00E01 - URA-ON! …", Henshin "_13_" and "S01E13 - Winter Days!") is the same episode: it follows
        // the titled copy, and the duplicate is resolved below.
        var original = inventory.Videos.ToDictionary(v => v.Path, v => ProposeOne(v, null), StringComparer.Ordinal);
        var renumbered = matches
            .Where(m => m.Match is not null && original[m.Video].Action is RowAction.Episode or RowAction.Special)
            .GroupBy(m => (original[m.Video].Season, original[m.Video].Episode))
            .Where(g => g.Select(m => (m.Match!.Season, m.Match.Episode)).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        var takenByTitle = matches.Where(m => m.Match is not null)
            .SelectMany(m => Enumerable.Range(m.Match!.Episode, (m.MatchEnd?.Episode ?? m.Match.Episode) - m.Match.Episode + 1).Select(n => (m.Match.Season, n)))
            .ToHashSet();

        // A file's own SxxEyy is trusted for a title the source does not know only when the release is
        // numbered like the source: the titles that did match sit on their own numbers.
        var named = matches.Where(m => m.Match is not null)
            .Select(m => (m, Own: ReleaseNameParser.Parse(Path.GetFileNameWithoutExtension(m.Video))))
            .Where(x => x.Own.Rule == "SxxEyy" && x.Own.Season is >= 1)
            .ToList();
        var sameNumbering = named.Count(x => (x.Own.Season, x.Own.Episode) == (x.m.Match!.Season, x.m.Match.Episode)) * 5 >= named.Count * 4;
        var rows = inventory.Videos.Select(v =>
        {
            var row = original[v.Path];
            if (!byVideo.TryGetValue(v.Path, out var m))
            {
                if (row.Action is RowAction.Episode or RowAction.Special
                    && renumbered.TryGetValue((row.Season, row.Episode), out var twin)
                    && (twin.Match!.Season, twin.Match.Episode) != (row.Season, row.Episode))
                {
                    var t = twin.Match;
                    return row with
                    {
                        Action = t.Season == 0 ? RowAction.Special : RowAction.Episode,
                        Season = t.Season,
                        Episode = t.Episode,
                        EpisodeEnd = null,
                        Reason = $"same number as {inventory.Videos.First(x => x.Path == twin.Video).Relative}, which is \"{t.Title}\"",
                    };
                }

                return row;
            }

            if (row.Action == RowAction.Extra)
            {
                return row;
            }

            var score = m.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (m.Match is not { } e)
            {
                var own = ReleaseNameParser.Parse(v.Stem);
                if (sameNumbering && own.Rule == "SxxEyy" && own.Season is >= 1 && own.Episode is { } ep
                    && !takenByTitle.Contains((own.Season.Value, ep))
                    && (known is null || known.Contains((own.Season.Value, ep))))
                {
                    return row with { Reason = $"title \"{m.FileTitle}\" not found (best {score}); kept S{own.Season:00}E{ep:00} from the name" };
                }

                return row with { Action = RowAction.Skip, Season = null, Episode = null, EpisodeEnd = null, Part = null, Reason = $"title \"{m.FileTitle}\" not found in the metadata source (best {score})" };
            }

            return new RowDecision(
                v.Path,
                e.Season == 0 ? RowAction.Special : RowAction.Episode,
                e.Season,
                e.Episode,
                m.MatchEnd?.Episode,
                shared.Contains(v.Path) ? m.Part : null,
                m.MatchEnd is { } end
                    ? $"title \"{m.FileTitle}\" = \"{e.Title}\" + \"{end.Title}\" ({score})"
                    : $"title \"{m.FileTitle}\" = \"{e.Title}\" ({score})");
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
        // A double file (S10E25-E26) makes single copies of its episodes (S10E25, S10E26) duplicates.
        foreach (var wide in rows.Where(r => r.Action is RowAction.Episode or RowAction.Special && r.Part is null && r.EpisodeEnd > r.Episode).ToList())
        {
            foreach (var r in rows.Where(r => r != wide && r.Action == wide.Action && r.Season == wide.Season && r.Part is null
                && r.Episode >= wide.Episode && (r.EpisodeEnd ?? r.Episode) <= wide.EpisodeEnd
                && (r.Episode, r.EpisodeEnd ?? r.Episode) != (wide.Episode, wide.EpisodeEnd)).ToList())
            {
                rows[rows.IndexOf(r)] = r with { Action = RowAction.Skip, Reason = $"duplicate of {relative[wide.Video]}, which holds S{wide.Season:00}E{wide.Episode:00}-E{wide.EpisodeEnd:00}" };
            }
        }

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
