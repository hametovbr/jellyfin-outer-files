using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// Renders Sonarr-style templates: <c>{Series Title}</c>, <c>{Movie Title}</c>, <c>{Year}</c>,
/// <c>{season:00}</c>, <c>{episode:00}</c>, <c>{-Eend:00}</c> (renders <c>-E05</c> only for multi-episode
/// files), <c>{Episode Title}</c>, <c>{Quality}</c>. Empty values disappear with their brackets and dashes.
/// </summary>
public static partial class NameTemplate
{
    /// <summary>Longest file name the renderer produces (bytes are what file systems limit; this keeps a margin).</summary>
    public const int MaxNameLength = 180;

    /// <summary>
    /// Renders a template.
    /// </summary>
    /// <param name="template">Template.</param>
    /// <param name="values">Token values; numbers as <see cref="int"/>, text as <see cref="string"/>.</param>
    /// <returns>A file-system-safe name.</returns>
    public static string Render(string template, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var rendered = TokenRegex().Replace(template, m =>
        {
            var name = m.Groups["name"].Value.Trim();
            var format = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            if (name.StartsWith('-'))
            {
                // {-Eend:00}: optional "-E05" suffix
                var key = name[1..];
                var prefix = key.Length > 0 && char.IsLetter(key[0]) && key.EndsWith("end", StringComparison.OrdinalIgnoreCase) ? key[..^3] : string.Empty;
                return values.TryGetValue("end", out var end) && end is int e ? m.Groups["dash"].Value + "-" + prefix + e.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
            }

            var text = values.TryGetValue(name.ToLowerInvariant(), out var v) && v is not null
                ? v is int i ? i.ToString(format, CultureInfo.InvariantCulture) : Sanitize(v.ToString() ?? string.Empty)
                : string.Empty;

            // An empty token takes its " - " separator with it: "S01E01 - {Episode Title} {Quality}" → "S01E01 1080p".
            return text.Length == 0 ? string.Empty : m.Groups["dash"].Value + text;
        });

        return Tidy(rendered);
    }

    /// <summary>
    /// Makes a text safe for a file name: <c>a: b</c> → <c>a - b</c>, slashes → <c>+</c>, characters
    /// Windows/SMB reject are removed.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Safe text.</returns>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case ':':
                    sb.Append(i + 1 < text.Length && text[i + 1] == ' ' ? " -" : "-");
                    break;
                case '/':
                case '\\':
                    sb.Append('+');
                    break;
                case '*' or '?' or '"' or '<' or '>' or '|':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return SpacesRegex().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// Shortens <paramref name="title"/> so that a name rendered around it stays within <see cref="MaxNameLength"/>.
    /// </summary>
    /// <param name="title">Title.</param>
    /// <param name="restLength">Length of the name without the title.</param>
    /// <returns>Title, possibly shortened with an ellipsis.</returns>
    public static string Fit(string title, int restLength)
    {
        ArgumentNullException.ThrowIfNull(title);
        var room = MaxNameLength - restLength;
        return title.Length <= room ? title : title[..Math.Max(0, room - 1)].TrimEnd() + "…";
    }

    private static string Tidy(string name)
    {
        name = EmptyGroupRegex().Replace(name, string.Empty);
        name = SpacesRegex().Replace(name, " ");
        name = DoubleDashRegex().Replace(name, " - ");
        name = TrailingDashRegex().Replace(name, string.Empty);
        name = LeadingDashRegex().Replace(name, string.Empty);
        return name.Trim().TrimEnd('.', ' ');
    }

    [GeneratedRegex(@"(?<dash>\s+-\s+)?\{(?<name>[^}:]+)(?::(?<fmt>[^}]+))?\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyGroupRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"(?:\s-\s*){2,}")]
    private static partial Regex DoubleDashRegex();

    [GeneratedRegex(@"\s*-\s*$")]
    private static partial Regex TrailingDashRegex();

    [GeneratedRegex(@"^\s*-\s*")]
    private static partial Regex LeadingDashRegex();
}

/// <summary>
/// Quality label in Sonarr/Radarr style (<c>WEBDL-1080p</c>, <c>Bluray-720p</c>), from the existing name or
/// the video height.
/// </summary>
public static partial class QualityParser
{
    /// <summary>
    /// Finds the quality of a video.
    /// </summary>
    /// <param name="name">Current file name without extension.</param>
    /// <param name="height">Video height in pixels from the library, or null.</param>
    /// <returns>Label, or empty when nothing is known.</returns>
    public static string Of(string name, int? height)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sonarr = SonarrRegex().Match(name);
        if (sonarr.Success)
        {
            return sonarr.Value;
        }

        var source = SourceRegex().Match(name) is { Success: true } s ? s.Groups["src"].Value.ToLowerInvariant() switch
        {
            var x when x.Contains("remux", StringComparison.Ordinal) => "Remux",
            var x when x.StartsWith("web", StringComparison.Ordinal) && x.Contains("rip", StringComparison.Ordinal) => "WEBRip",
            var x when x.StartsWith("web", StringComparison.Ordinal) => "WEBDL",
            "hdtv" => "HDTV",
            "dvd" or "dvdrip" => "DVD",
            "tvrip" or "satrip" or "sdtv" => "SDTV",
            _ => "Bluray",
        } : null;

        var res = ResolutionRegex().Match(name) is { Success: true } r
            ? (r.Groups["p"].Success ? r.Groups["p"].Value : r.Groups["h"].Value) + "p"
            : height is > 0 ? Bucket(height.Value) + "p" : null;

        return (source, res) switch
        {
            (null, null) => string.Empty,
            (null, _) => res!,
            (_, null) => source,
            _ => source + "-" + res,
        };
    }

    private static int Bucket(int height) => height switch
    {
        >= 1800 => 2160,
        >= 900 => 1080,
        >= 650 => 720,
        >= 540 => 576,
        _ => 480,
    };

    [GeneratedRegex(@"\b(?:WEBDL|WEBRip|Bluray|HDTV|SDTV|DVD|Remux|Raw-HD)(?:-\d{3,4}p)?(?: Proper)?\b")]
    private static partial Regex SonarrRegex();

    [GeneratedRegex(@"(?<![a-z])(?<src>BD-?Remux|Remux|BDRip|BRRip|Blu-?Ray|BD|WEB-?DL|WEB-?Rip|WEB|HDTV|DVDRip|DVD|TVRip|SATRip|SDTV)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"(?<![0-9])(?:(?<p>2160|1080|720|576|480)[pi]|\d{3,4}x(?<h>2160|1080|720|576|480))(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
}
