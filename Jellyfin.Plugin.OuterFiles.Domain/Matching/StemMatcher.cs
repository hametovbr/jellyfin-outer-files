using Jellyfin.Plugin.OuterFiles.Domain.Model;

namespace Jellyfin.Plugin.OuterFiles.Domain.Matching;

/// <summary>
/// A track that belongs to a video.
/// </summary>
/// <param name="Video">Absolute video path.</param>
/// <param name="Track">Absolute track path.</param>
/// <param name="Kind">Subtitle or audio.</param>
/// <param name="Suffix">What follows the video stem in the track name, e.g. <c>.ru_Anilibria</c>; empty when the names are equal.</param>
/// <param name="FolderChain">Folders between the closest common folder of video and track and the track, outermost first.</param>
public sealed record TrackMatch(string Video, string Track, FileKind Kind, string Suffix, IReadOnlyList<string> FolderChain);

/// <summary>
/// Result of matching one release.
/// </summary>
/// <param name="Matches">Tracks the host does not find by itself, with their video.</param>
/// <param name="HostHandled">Tracks next to their video with a matching name: the host finds them, nothing to do.</param>
/// <param name="Unmatched">Track files no video claims.</param>
public sealed record MatchResult(IReadOnlyList<TrackMatch> Matches, IReadOnlyList<string> HostHandled, IReadOnlyList<string> Unmatched);

/// <summary>
/// Matches tracks to videos by name, the way Jellyfin does for adjacent files, but across the whole release
/// folder: a track belongs to a video when its name (without extension) equals the video's or continues it
/// with a dot. In the owner's library all 362 tracks stored in sub folders match this way; episode numbers
/// are not needed and are deliberately not used (docs/ARCHITECTURE.md, section 6.2).
/// </summary>
public static class StemMatcher
{
    /// <summary>
    /// Matches every track file of a release to its video.
    /// </summary>
    /// <param name="files">Every file below the release root (absolute paths).</param>
    /// <returns>The result.</returns>
    public static MatchResult Match(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();
        var videos = all.Where(f => MediaKinds.Of(f) == FileKind.Video)
            .Select(v => (Path: v, Stem: Path.GetFileNameWithoutExtension(v), Dir: Path.GetDirectoryName(v) ?? string.Empty))
            .ToList();

        // Longest stem first, so "Show 1.Part 2" wins over "Show 1" for "Show 1.Part 2.rus.srt".
        videos.Sort((a, b) => b.Stem.Length.CompareTo(a.Stem.Length));

        var matches = new List<TrackMatch>();
        var hostHandled = new List<string>();
        var unmatched = new List<string>();
        foreach (var track in all)
        {
            var kind = MediaKinds.Of(track);
            if (kind is not (FileKind.Subtitle or FileKind.Audio))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(track);
            var dir = Path.GetDirectoryName(track) ?? string.Empty;
            var owner = videos.FirstOrDefault(v => Continues(stem, v.Stem));
            if (owner.Path is null)
            {
                unmatched.Add(track);
                continue;
            }

            // Same rule, same folder: Jellyfin already shows it.
            var sameFolder = videos.FirstOrDefault(v => v.Dir.Equals(dir, StringComparison.Ordinal) && Continues(stem, v.Stem));
            if (sameFolder.Path is not null)
            {
                hostHandled.Add(track);
                continue;
            }

            matches.Add(new TrackMatch(owner.Path, track, kind, stem[owner.Stem.Length..], FolderChain(owner.Dir, dir)));
        }

        return new MatchResult(matches, hostHandled, unmatched);
    }

    /// <summary>
    /// Jellyfin's rule: <paramref name="name"/> equals <paramref name="stem"/> or continues it with a dot
    /// (case-insensitive, like <c>MediaInfoResolver.GetExternalFiles</c>).
    /// </summary>
    /// <param name="name">Track name without extension.</param>
    /// <param name="stem">Video name without extension.</param>
    /// <returns>True when the track name belongs to the video.</returns>
    public static bool Continues(string name, string stem)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stem);
        return name.Length >= stem.Length
            && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && (name.Length == stem.Length || name[stem.Length] == '.');
    }

    private static string[] FolderChain(string videoDir, string trackDir)
    {
        var v = videoDir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var t = trackDir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < v.Length && common < t.Length && v[common] == t[common])
        {
            common++;
        }

        return t[common..];
    }
}
