using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// One episode of the metadata source.
/// </summary>
/// <param name="Season">Season (0 = specials).</param>
/// <param name="Episode">Episode.</param>
/// <param name="Title">Title in one language.</param>
public sealed record CatalogEpisode(int Season, int Episode, string Title);

/// <summary>
/// The title read from a file name and what it matched.
/// </summary>
/// <param name="Video">Video path.</param>
/// <param name="FileTitle">Title read from the name (without part marks).</param>
/// <param name="Part">Part from "(Часть 2)", "Part 2", "pt2", or null.</param>
/// <param name="Match">The matched episode, or null.</param>
/// <param name="Score">Similarity of the best candidate (0..1).</param>
/// <param name="MatchEnd">Last episode of a double file ("A + B" matched to two adjacent episodes), or null.</param>
public sealed record TitleMatch(string Video, string FileTitle, int? Part, CatalogEpisode? Match, double Score, CatalogEpisode? MatchEnd = null);

/// <summary>
/// Matches videos to episodes by the episode title in the file name. Used when a release numbers episodes its
/// own way (<c>JN.02a/02b</c> where the metadata source has two episodes, specials mixed into the run).
/// Pure: the catalog is fetched by the caller.
/// </summary>
public static partial class TitleMatcher
{
    /// <summary>Minimum similarity for a match.</summary>
    public const double Threshold = 0.6;

    /// <summary>
    /// Reads the episode title from a file name: the text after the episode number
    /// (<c>Show - S01E13 - Winter Days! Bluray-1080p</c> → <c>Winter Days!</c>,
    /// <c>JN.02a.Обычный мальчик</c> → <c>Обычный мальчик</c>), without quality tags.
    /// </summary>
    /// <param name="stem">File name without extension.</param>
    /// <returns>Title and part, or null when the name carries no title.</returns>
    public static (string Title, int? Part)? TitleOf(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var text = TechnicalGroup().Replace(BracketGroup().Replace(stem, " "), " ");
        var marker = SeasonEpisode().Match(text);
        if (!marker.Success)
        {
            marker = LeadingNumber().Match(text);
        }

        if (!marker.Success)
        {
            return null;
        }

        text = text[(marker.Index + marker.Length)..];
        text = QualityTail().Replace(text, string.Empty);

        int? part = null;
        var p = PartMark().Match(text);
        if (p.Success)
        {
            part = int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
            text = text.Remove(p.Index, p.Length);
        }

        text = text.Replace('_', ' ');
        if (!text.Contains(' ', StringComparison.Ordinal))
        {
            text = text.Replace('.', ' ');
        }

        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '.', '–', '—');
        return Normalize(text).Count(char.IsLetter) >= 3 ? (text, part) : null;
    }

    /// <summary>
    /// Similarity of two titles: Dice coefficient over letter pairs of the normalized texts.
    /// </summary>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    /// <returns>0..1.</returns>
    public static double Similarity(string a, string b)
    {
        var x = Bigrams(a);
        var y = Bigrams(b);
        if (x.Count == 0 || y.Count == 0)
        {
            return 0;
        }

        var common = 0;
        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in y)
        {
            pool[g] = pool.GetValueOrDefault(g) + 1;
        }

        foreach (var g in x)
        {
            if (pool.TryGetValue(g, out var n) && n > 0)
            {
                pool[g] = n - 1;
                common++;
            }
        }

        return 2.0 * common / (x.Count + y.Count);
    }

    /// <summary>
    /// Matches files to catalog episodes. Each episode is taken once, except by the parts of one split episode
    /// ("(Часть 1)", "(Часть 2)"). A match needs <see cref="Threshold"/> and a clear lead over the next episode.
    /// </summary>
    /// <param name="files">Video path and stem.</param>
    /// <param name="catalog">Episodes, possibly in several languages (same season/episode repeated).</param>
    /// <returns>One result per file that has a title in its name.</returns>
    public static IReadOnlyList<TitleMatch> Match(IEnumerable<(string Video, string Stem)> files, IReadOnlyList<CatalogEpisode> catalog)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalog);
        var candidates = new List<(string Video, string Title, int? Part, List<(CatalogEpisode Episode, double Score)> Ranked)>();
        foreach (var (video, stem) in files)
        {
            if (TitleOf(stem) is not { } t)
            {
                continue;
            }

            // Best score per episode over its languages.
            var ranked = catalog
                .GroupBy(e => (e.Season, e.Episode))
                .Select(g => g.Select(e => (Episode: e, Score: Similarity(t.Title, e.Title))).MaxBy(x => x.Score))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Episode.Season == 0 ? 1 : 0)
                .ToList();
            candidates.Add((video, t.Title, t.Part, ranked));
        }

        var taken = new Dictionary<(int, int), List<int?>>();
        var result = new Dictionary<string, TitleMatch>(StringComparer.Ordinal);

        // Double files ("A + B") first: every half must be an episode, and the episodes must be neighbours.
        var episodes = catalog.GroupBy(e => (e.Season, e.Episode)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var c in candidates.Where(c => c.Title.Contains(" + ", StringComparison.Ordinal) && (c.Ranked.Count == 0 || c.Ranked[0].Score < 0.9)))
        {
            var halves = c.Title.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var found = halves.Select(h => Best(h, episodes)).ToList();
            if (found.Count(f => f is not null) == halves.Length - 1 && halves.Length == 2)
            {
                // One half is titled differently by the source: accept the neighbour that resembles it.
                var known = found.First(f => f is not null)!.Value.Episode;
                var other = halves[found.FindIndex(f => f is null)];
                var neighbour = new[] { known.Episode - 1, known.Episode + 1 }
                    .Where(n => episodes.ContainsKey((known.Season, n)))
                    .Select(n => (Episode: episodes[(known.Season, n)][0], Score: episodes[(known.Season, n)].Max(e => Similarity(other, e.Title))))
                    .Where(x => x.Score >= 0.45)
                    .OrderByDescending(x => x.Score)
                    .Cast<(CatalogEpisode Episode, double Score)?>()
                    .FirstOrDefault();
                found[found.FindIndex(f => f is null)] = neighbour;
            }

            var score = found.Min(f => f?.Score ?? 0);
            var numbers = found.Where(f => f is not null).Select(f => f!.Value.Episode).OrderBy(e => e.Episode).ToList();
            var adjacent = numbers.Count == halves.Length
                && numbers.Select(e => e.Season).Distinct().Count() == 1
                && numbers.Zip(numbers.Skip(1), (a, b) => b.Episode - a.Episode).All(d => d == 1);
            if (!adjacent)
            {
                result[c.Video] = new TitleMatch(c.Video, c.Title, c.Part, null, score);
                continue;
            }

            foreach (var e in numbers)
            {
                (taken[(e.Season, e.Episode)] = taken.GetValueOrDefault((e.Season, e.Episode)) ?? []).Add(null);
            }

            result[c.Video] = new TitleMatch(c.Video, c.Title, c.Part, numbers[0], score, numbers[^1]);
        }

        // Most certain first, so a weaker file cannot take an episode a better one needs.
        foreach (var c in candidates.Where(c => !result.ContainsKey(c.Video)).OrderByDescending(c => c.Ranked.Count > 0 ? c.Ranked[0].Score : 0))
        {
            TitleMatch? found = null;
            foreach (var (episode, score) in c.Ranked)
            {
                if (score < Threshold)
                {
                    break;
                }

                var key = (episode.Season, episode.Episode);
                var parts = taken.GetValueOrDefault(key);
                // A second copy of an episode (near-exact title) shares it; duplicates are resolved by the caller.
                var free = parts is null || score >= 0.9 || (c.Part is not null && parts.All(p => p is not null && p != c.Part));
                if (!free)
                {
                    continue;
                }

                var next = c.Ranked.Where(r => (r.Episode.Season, r.Episode.Episode) != key).Select(r => r.Score).FirstOrDefault();
                if (score < 0.9 && score - next < 0.1)
                {
                    break;
                }

                (taken[key] = parts ?? []).Add(c.Part);
                found = new TitleMatch(c.Video, c.Title, c.Part, episode, score);
                break;
            }

            result[c.Video] = found ?? new TitleMatch(c.Video, c.Title, c.Part, null, c.Ranked.Count > 0 ? c.Ranked[0].Score : 0);
        }

        return candidates.Select(c => result[c.Video]).ToList();
    }

    private static (CatalogEpisode Episode, double Score)? Best(string title, Dictionary<(int Season, int Episode), List<CatalogEpisode>> episodes)
    {
        var ranked = episodes.Values
            .Select(g => (Episode: g[0], Score: g.Max(e => Similarity(title, e.Title))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Episode.Season == 0 ? 1 : 0)
            .Take(2)
            .ToList();
        if (ranked.Count == 0 || ranked[0].Score < Threshold || (ranked[0].Score < 0.9 && ranked.Count > 1 && ranked[0].Score - ranked[1].Score < 0.1))
        {
            return null;
        }

        return ranked[0];
    }

    private static List<string> Bigrams(string s)
    {
        var n = " " + Normalize(s) + " ";
        var list = new List<string>(n.Length);
        for (var i = 0; i < n.Length - 1; i++)
        {
            list.Add(n.Substring(i, 2));
        }

        return n.Trim().Length == 0 ? [] : list;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }

        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketGroup();

    // "(WEB-DL 1920x1080 x264 AAC)", "(BD_x264_FLAC)": release details, not part of a title.
    [GeneratedRegex(@"\([^)]*(?:\d{3,4}p|\d{3,4}x\d{3,4}|x26[45]|HEVC|WEB-?DL|BD|DVD|AAC|FLAC)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalGroup();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[Ss]\d{1,2}[Ee]\d{1,3}(?:-?[Ee]\d{1,3})?(?![0-9])")]
    private static partial Regex SeasonEpisode();

    // "JN.02a.", "Show 07 - ", "[Grp]_Show_07_": the first stand-alone number with an optional segment letter.
    [GeneratedRegex(@"(?<![A-Za-z0-9-])\d{1,3}[ab]?(?=[.\s_-])")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"[\s._-]+(?:Bluray|BDRip|BRRip|WEB-?DL|WEB-?Rip|HDTV|SDTV|DVD(?:Rip)?|Proper|REPACK|x26[45]|HEVC|\d{3,4}p)(?:[\s._-].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTail();

    [GeneratedRegex(@"\(?\b(?:Часть|Part|pt\.?)\s*(\d)\)?", RegexOptions.IgnoreCase)]
    private static partial Regex PartMark();
}
