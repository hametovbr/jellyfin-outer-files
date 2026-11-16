using System.Text.RegularExpressions;
using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// A file next to a video that belongs to it by name: <c>X.ru.srt</c>, <c>X.nfo</c>, <c>X-thumb.jpg</c>.
/// </summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Suffix">Everything after the video stem, including the extension (<c>.ru.srt</c>, <c>-thumb.jpg</c>).</param>
public sealed record Sidecar(string Path, string Suffix);

/// <summary>
/// One video of a release with everything attached to it.
/// </summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Relative">Path relative to the release root.</param>
/// <param name="Sidecars">Files next to it that continue its name.</param>
/// <param name="OuterTracks">Tracks found elsewhere in the release.</param>
public sealed record InventoryVideo(string Path, string Relative, IReadOnlyList<Sidecar> Sidecars, IReadOnlyList<TrackMatch> OuterTracks)
{
    /// <summary>Gets the file name without extension.</summary>
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Gets the folders between the release root and the video.</summary>
    public IReadOnlyList<string> Folders => Relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)[..^1];
}

/// <summary>
/// Everything in a release folder, classified (docs/ARCHITECTURE.md, section 7.1 step 1).
/// </summary>
/// <param name="Root">Release root.</param>
/// <param name="Videos">Videos with their sidecars and outer tracks.</param>
/// <param name="FolderMeta">Artwork and nfo files that describe a folder (tvshow.nfo, poster.jpg, season.nfo …) and the
/// release's episode source choice (<see cref="EpisodeSources.FileName"/>).</param>
/// <param name="Fonts">Files inside font folders.</param>
/// <param name="UnmatchedTracks">Tracks no video claims.</param>
/// <param name="Leftovers">Everything else (scans, text files, unknown).</param>
public sealed partial record Inventory(string Root, IReadOnlyList<InventoryVideo> Videos, IReadOnlyList<string> FolderMeta, IReadOnlyList<string> Fonts, IReadOnlyList<string> UnmatchedTracks, IReadOnlyList<string> Leftovers)
{
    /// <summary>
    /// Builds the inventory of a release.
    /// </summary>
    /// <param name="root">Release root.</param>
    /// <param name="files">Every file below it.</param>
    /// <returns>The inventory.</returns>
    public static Inventory Build(string root, IReadOnlyCollection<string> files)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(files);
        root = root.TrimEnd('/', '\\');
        var match = StemMatcher.Match(files);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var outer = match.Matches.ToLookup(m => m.Video, StringComparer.Ordinal);
        foreach (var m in match.Matches)
        {
            claimed.Add(m.Track);
        }

        var byDir = files.ToLookup(f => System.IO.Path.GetDirectoryName(f) ?? string.Empty, StringComparer.Ordinal);
        var videos = new List<InventoryVideo>();
        foreach (var video in files.Where(f => MediaKinds.Of(f) == FileKind.Video).OrderBy(f => f, StringComparer.Ordinal))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(video);
            var dir = System.IO.Path.GetDirectoryName(video) ?? string.Empty;
            var sidecars = new List<Sidecar>();
            foreach (var f in byDir[dir])
            {
                var name = System.IO.Path.GetFileName(f);
                if (f == video || MediaKinds.Of(f) == FileKind.Video || name.Length <= stem.Length || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var suffix = name[stem.Length..];
                if (suffix[0] is '.' or '-' && !OwnedByLongerStem(name, stem, byDir[dir]))
                {
                    sidecars.Add(new Sidecar(f, suffix));
                    claimed.Add(f);
                }
            }

            claimed.Add(video);
            videos.Add(new InventoryVideo(video, Relative(root, video), sidecars, outer[video].ToList()));
        }

        var folderMeta = new List<string>();
        var fonts = new List<string>();
        var leftovers = new List<string>();
        var unmatched = new HashSet<string>(match.Unmatched, StringComparer.Ordinal);
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (claimed.Contains(f) || unmatched.Contains(f))
            {
                continue;
            }

            if (Relative(root, f).Split(['/', '\\']).SkipLast(1).Any(d => d.Equals("fonts", StringComparison.OrdinalIgnoreCase)))
            {
                fonts.Add(f);
            }
            else if (FolderMetaRegex().IsMatch(System.IO.Path.GetFileName(f))
                || (System.IO.Path.GetFileName(f) == EpisodeSources.FileName && System.IO.Path.GetDirectoryName(f) == root))
            {
                folderMeta.Add(f);
            }
            else
            {
                leftovers.Add(f);
            }
        }

        return new Inventory(root, videos, folderMeta, fonts, match.Unmatched.Order(StringComparer.Ordinal).ToList(), leftovers);
    }

    /// <summary>
    /// Path relative to a root, with forward slashes.
    /// </summary>
    /// <param name="root">Root.</param>
    /// <param name="path">Path below it.</param>
    /// <returns>Relative path.</returns>
    public static string Relative(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        return path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) ? path[(root.Length + 1)..] : path;
    }

    // "Show - S01E01.Part2.mkv" next to "Show - S01E01.mkv": "Show - S01E01.Part2.nfo" belongs to the longer stem.
    private static bool OwnedByLongerStem(string name, string stem, IEnumerable<string> siblings) => siblings
        .Where(s => MediaKinds.Of(s) == FileKind.Video)
        .Select(System.IO.Path.GetFileNameWithoutExtension)
        .Any(other => other!.Length > stem.Length && other.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && name.StartsWith(other, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(?:tvshow|season|movie|folder|poster|fanart|banner|backdrop\d*|landscape|logo|clearart|clearlogo|discart|thumb|season\d+(?:-\w+)?|season-specials(?:-\w+)?|season-all(?:-\w+)?)\.(?:nfo|jpg|jpeg|png|webp|tbn)$", RegexOptions.IgnoreCase)]
    private static partial Regex FolderMetaRegex();
}
