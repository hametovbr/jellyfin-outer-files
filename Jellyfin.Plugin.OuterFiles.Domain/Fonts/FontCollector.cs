using Jellyfin.Plugin.OuterFiles.Domain.Links;

namespace Jellyfin.Plugin.OuterFiles.Domain.Fonts;

/// <summary>
/// Picks the font files of releases that Jellyfin can serve as fallback fonts for ASS subtitles
/// (<c>.ttf</c>, <c>.otf</c>, <c>.woff</c>, <c>.woff2</c>: the list of Jellyfin's FallbackFont endpoint).
/// Fallback fonts are one flat folder, so equal file names keep the first file in path order.
/// </summary>
public static class FontCollector
{
    /// <summary>Font extensions Jellyfin serves as fallback fonts.</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".woff", ".woff2" };

    /// <summary>Jellyfin stops sending fallback fonts after 20 MiB in total.</summary>
    public const long JellyfinLimitBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Builds the desired links for the fallback font folder.
    /// </summary>
    /// <param name="files">Files of all releases.</param>
    /// <returns>Desired links, one per distinct file name.</returns>
    public static IReadOnlyList<DesiredLink> Desire(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return files
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Where(f => seen.Add(Path.GetFileName(f)))
            .Select(f => new DesiredLink(Path.GetFileName(f), f, "font of " + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)) ?? string.Empty)))
            .ToList();
    }
}
