using System.Text;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Links;

/// <summary>
/// A link the plugin wants in an item's internal metadata folder.
/// </summary>
/// <param name="Name">File name of the link.</param>
/// <param name="Target">Absolute path of the track.</param>
/// <param name="Reason">Why, for previews.</param>
public sealed record DesiredLink(string Name, string Target, string Reason);

/// <summary>
/// A symlink owned by the plugin that exists now.
/// </summary>
/// <param name="Name">File name of the link.</param>
/// <param name="Target">Where it points.</param>
public sealed record ExistingLink(string Name, string Target);

/// <summary>
/// Everything the planner needs for one video.
/// </summary>
/// <param name="Folder">The item's internal metadata folder.</param>
/// <param name="MissingFolders">Folders on the way to <paramref name="Folder"/> that do not exist yet, outermost first (including the folder itself).</param>
/// <param name="Desired">Desired links.</param>
/// <param name="Existing">Plugin-owned links present now.</param>
public sealed record VideoLinkState(string Folder, IReadOnlyList<string> MissingFolders, IReadOnlyList<DesiredLink> Desired, IReadOnlyList<ExistingLink> Existing);

/// <summary>
/// Names links so that Jellyfin's own external-file parser reads the right language, title and flags,
/// and turns desired-vs-existing link sets into a plan (reconciliation, docs/ARCHITECTURE.md P5).
/// </summary>
public static class LinkPlanner
{
    /// <summary>
    /// Builds the link file name: <c>&lt;video stem&gt;[.&lt;studio&gt;][.forced][.sdh].&lt;lang&gt;&lt;ext&gt;</c>.
    /// The language goes last because Jellyfin takes the first language token counting from the end;
    /// the title is cleaned of words Jellyfin would read as flags.
    /// </summary>
    /// <param name="videoStem">Video file name without extension.</param>
    /// <param name="track">Track descriptor with the language already resolved.</param>
    /// <param name="extension">Track extension including the dot.</param>
    /// <param name="titleSuffix">Disambiguator appended to the title (" 2"), or empty.</param>
    /// <returns>The file name.</returns>
    public static string LinkName(string videoStem, TrackDescriptor track, string extension, string titleSuffix = "")
    {
        ArgumentNullException.ThrowIfNull(videoStem);
        ArgumentNullException.ThrowIfNull(track);
        var sb = new StringBuilder(videoStem);
        var title = SanitizeTitle(track.Studio) + titleSuffix;
        if (title.Trim().Length > 0)
        {
            sb.Append('.').Append(title.Trim());
        }

        if (track.Forced)
        {
            sb.Append(".forced");
        }

        if (track.HearingImpaired)
        {
            sb.Append(".sdh");
        }

        sb.Append('.').Append(string.IsNullOrEmpty(track.Language) ? "und" : track.Language);
        sb.Append(extension.ToLowerInvariant());
        return sb.ToString();
    }

    /// <summary>
    /// Builds desired links for the tracks of one video, giving equal names a numeric suffix.
    /// </summary>
    /// <param name="videoStem">Video stem.</param>
    /// <param name="tracks">Tracks with resolved descriptors.</param>
    /// <returns>Desired links in a stable order.</returns>
    public static IReadOnlyList<DesiredLink> Desire(string videoStem, IEnumerable<(string Track, TrackDescriptor Descriptor)> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var result = new List<DesiredLink>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (track, d) in tracks.OrderBy(t => t.Track, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(track);
            var name = LinkName(videoStem, d, ext);
            for (var n = 2; !used.Add(name); n++)
            {
                name = LinkName(videoStem, d, ext, (string.IsNullOrEmpty(d.Studio) ? "#" : " ") + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var reason = $"{d.Language ?? "und"} ({d.LanguageSource})" + (d.Studio is null ? string.Empty : $", {d.Studio}") + (d.Forced ? ", signs" : string.Empty);
            result.Add(new DesiredLink(name, track, reason));
        }

        return result;
    }

    /// <summary>
    /// Plans the changes for many videos at once.
    /// </summary>
    /// <param name="videos">State per video.</param>
    /// <returns>The plan and the folders whose link set changes (their items need a refresh).</returns>
    public static (Plan Plan, IReadOnlyList<string> ChangedFolders) Plan(IEnumerable<VideoLinkState> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        var ops = new List<Operation>();
        var changed = new List<string>();
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in videos)
        {
            var before = ops.Count;
            var desired = v.Desired.ToDictionary(d => d.Name, StringComparer.Ordinal);
            var existing = v.Existing.ToDictionary(e => e.Name, StringComparer.Ordinal);

            foreach (var e in v.Existing)
            {
                if (!desired.TryGetValue(e.Name, out var d) || !string.Equals(d.Target, e.Target, StringComparison.Ordinal))
                {
                    ops.Add(new RemoveSymlink(Path.Combine(v.Folder, e.Name), e.Target, "track no longer matches"));
                }
            }

            var toCreate = v.Desired.Where(d => !existing.TryGetValue(d.Name, out var e) || !string.Equals(d.Target, e.Target, StringComparison.Ordinal)).ToList();
            if (toCreate.Count > 0)
            {
                foreach (var folder in v.MissingFolders.Where(created.Add))
                {
                    ops.Add(new CreateDirectory(folder, "metadata folder for links"));
                }

                ops.AddRange(toCreate.Select(d => new CreateSymlink(Path.Combine(v.Folder, d.Name), d.Target, d.Reason)));
            }

            if (ops.Count > before)
            {
                changed.Add(v.Folder);
            }
        }

        return (new Plan("Link outer tracks", ops), changed);
    }

    private static string SanitizeTitle(string? studio)
    {
        if (string.IsNullOrWhiteSpace(studio))
        {
            return string.Empty;
        }

        // A dot would split the title into several flag slices.
        var words = studio.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !IsFlagWord(w));
        return string.Join(' ', words);
    }

    // Jellyfin's ExternalPathParser: "default"/"forced"/"foreign" match as substrings, "hi"/"cc"/"sdh" as whole slices.
    private static bool IsFlagWord(string w) =>
        w.Contains("default", StringComparison.OrdinalIgnoreCase)
        || w.Contains("forced", StringComparison.OrdinalIgnoreCase)
        || w.Contains("foreign", StringComparison.OrdinalIgnoreCase)
        || w.Equals("hi", StringComparison.OrdinalIgnoreCase)
        || w.Equals("cc", StringComparison.OrdinalIgnoreCase)
        || w.Equals("sdh", StringComparison.OrdinalIgnoreCase);
}
