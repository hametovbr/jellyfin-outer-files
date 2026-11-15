namespace Jellyfin.Plugin.OuterFiles.Domain.Model;

/// <summary>
/// What a file is, by extension. The lists mirror Jellyfin 12.4 <c>NamingOptions</c> so the plugin
/// classifies files exactly like the host does.
/// </summary>
public static class MediaKinds
{
    /// <summary>Gets video extensions (Jellyfin <c>VideoFileExtensions</c>).</summary>
    public static IReadOnlySet<string> Video { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".001", ".3g2", ".3gp", ".amv", ".asf", ".asx", ".avi", ".bin", ".bivx", ".divx", ".dv", ".f4v", ".fli",
        ".flv", ".ifo", ".img", ".iso", ".m2t", ".m2ts", ".m2v", ".m4v", ".mkv", ".mk3d", ".mov", ".mp4", ".mpe",
        ".mpeg", ".mpg", ".mts", ".mxf", ".nrg", ".nsv", ".nuv", ".ogm", ".ogv", ".pva", ".qt", ".rec", ".rm",
        ".rmvb", ".strm", ".svq3", ".tp", ".ts", ".ty", ".viv", ".vob", ".vp3", ".webm", ".wmv", ".wtv", ".xvid",
    };

    /// <summary>Gets subtitle extensions (Jellyfin <c>SubtitleFileExtensions</c> plus <c>.idx</c>).</summary>
    public static IReadOnlySet<string> Subtitle { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".mks", ".sami", ".smi", ".srt", ".ssa", ".sub", ".sup", ".vtt", ".idx",
    };

    /// <summary>Gets audio track extensions: the part of Jellyfin <c>AudioFileExtensions</c> used for tracks.</summary>
    public static IReadOnlySet<string> Audio { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".ac3", ".dts", ".eac3", ".ec3", ".flac", ".m4a", ".mka", ".mp2", ".mp3", ".mpa", ".oga", ".ogg",
        ".opus", ".wav", ".wma", ".wv", ".alac", ".ape",
    };

    /// <summary>Gets subtitle extensions that hold text (language can be guessed from content).</summary>
    public static IReadOnlySet<string> TextSubtitle { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".ssa", ".srt", ".vtt", ".smi", ".sami",
    };

    /// <summary>
    /// Classifies a path.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <returns>The kind.</returns>
    public static FileKind Of(string path)
    {
        var ext = Path.GetExtension(path);
        if (Video.Contains(ext))
        {
            return FileKind.Video;
        }

        if (Subtitle.Contains(ext))
        {
            return FileKind.Subtitle;
        }

        return Audio.Contains(ext) ? FileKind.Audio : FileKind.Other;
    }
}

/// <summary>
/// Kind of a media file.
/// </summary>
public enum FileKind
{
    /// <summary>Anything else.</summary>
    Other,

    /// <summary>A video.</summary>
    Video,

    /// <summary>An external subtitle track.</summary>
    Subtitle,

    /// <summary>An external audio track.</summary>
    Audio,
}
