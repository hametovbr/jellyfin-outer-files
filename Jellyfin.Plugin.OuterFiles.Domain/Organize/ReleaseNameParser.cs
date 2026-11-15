using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// What a release file name says about its place in a series.
/// </summary>
/// <param name="Season">Season, when the name states it.</param>
/// <param name="Episode">Episode number within the season or, when <see cref="IsAbsolute"/>, overall.</param>
/// <param name="EpisodeEnd">Last episode of a multi-episode file.</param>
/// <param name="IsAbsolute">The number counts across seasons (<c>136. Title</c>, <c>[61_of_62]</c>).</param>
/// <param name="Segment">Segment letter of a split episode (<c>JN.02a</c>), or null.</param>
/// <param name="Extra">Extra kind (NCOP, NCED, Menu, PV, …) when the file is not an episode.</param>
/// <param name="IsSpecial">An SP/OVA/OAD: belongs to season 0.</param>
/// <param name="Rule">Which rule matched, for previews.</param>
public sealed record ParsedName(int? Season, int? Episode, int? EpisodeEnd, bool IsAbsolute, string? Segment, string? Extra, bool IsSpecial, string Rule)
{
    /// <summary>Gets a result for names nothing could be read from.</summary>
    public static ParsedName Nothing { get; } = new(null, null, null, false, null, null, false, "none");
}

/// <summary>
/// The plugin's own reading of release names, used to propose a mapping in organize mode. Independent of
/// Jellyfin's parser on purpose: organize mode must work for names Jellyfin gets wrong.
/// </summary>
public static partial class ReleaseNameParser
{
    /// <summary>
    /// Parses a file name (without extension).
    /// </summary>
    /// <param name="name">File name without extension.</param>
    /// <returns>What it says.</returns>
    public static ParsedName Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // An explicit SxxEyy wins over everything, including words like "OP" in an episode title.
        var m = SxxEyyRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(Int(m.Groups["s"].Value), Int(m.Groups["e"].Value), Int(m.Groups["end"].Value), false, null, null, Int(m.Groups["s"].Value) == 0, "SxxEyy");
        }

        var extra = ExtraRegex().Match(name);
        if (extra.Success)
        {
            var kind = new string(extra.Groups["kind"].Value.ToUpperInvariant().Where(char.IsLetter).ToArray());
            var number = Int(extra.Groups["n"].Value);
            if (kind is "SP" or "OVA" or "OAD" or "SPECIAL")
            {
                return new ParsedName(0, number ?? 1, null, false, null, null, true, "special");
            }

            kind = kind switch
            {
                "NOCREDITOPENING" or "CREDITLESSOP" => "NCOP",
                "NOCREDITENDING" or "CREDITLESSED" => "NCED",
                _ => kind,
            };
            return new ParsedName(null, null, null, false, null, kind + (number is null ? string.Empty : number.Value.ToString("00", CultureInfo.InvariantCulture)), false, "extra");
        }

        m = TvSeasonRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(Int(m.Groups["s"].Value), Int(m.Groups["e"].Value), null, false, null, null, false, "TV-N - NN");
        }

        var season = SeasonTokenRegex().Match(name) is { Success: true } st ? Int(st.Groups["s"].Value) : null;

        m = OfRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(null, Int(m.Groups["e"].Value), null, true, null, null, false, "[NN of MM]");
        }

        m = BracketRegex().Match(name);
        if (m.Success && !IsYear(m.Groups["e"].Value))
        {
            return new ParsedName(season, Int(m.Groups["e"].Value), null, season is null, null, null, false, "[NN]");
        }

        m = DashRegex().Match(name);
        if (m.Success && !IsYear(m.Groups["e"].Value))
        {
            return new ParsedName(season, Int(m.Groups["e"].Value), Int(m.Groups["end"].Value), season is null, null, null, false, " - NN");
        }

        m = UnderscoreRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(season, Int(m.Groups["e"].Value), null, season is null, null, null, false, "_NN_");
        }

        m = CodeSegmentRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(null, Int(m.Groups["e"].Value), null, true, m.Groups["seg"].Value.ToLowerInvariant() is { Length: > 0 } seg ? seg : null, null, false, "XX.NNa.Title");
        }

        m = LeadingNumberRegex().Match(name);
        if (m.Success && !IsYear(m.Groups["e"].Value))
        {
            return new ParsedName(null, Int(m.Groups["e"].Value), null, true, null, null, false, "NNN. Title");
        }

        m = EpisodeTokenRegex().Match(name);
        if (m.Success)
        {
            return new ParsedName(season, Int(m.Groups["e"].Value), null, season is null, null, null, false, "E NN");
        }

        m = TrailingNumberRegex().Match(name);
        if (m.Success && !IsYear(m.Groups["e"].Value))
        {
            return new ParsedName(season, Int(m.Groups["e"].Value), null, season is null, null, null, false, "Name NN [tags]");
        }

        return ParsedName.Nothing;
    }

    /// <summary>
    /// Reads a season number from a folder name: <c>Season 2</c>, <c>Сезон 3 (BDRip)</c>, <c>S01</c>,
    /// <c>Jujutsu.Kaisen.Season3</c>, <c>[TV-2]</c>, <c>Jujutsu Kaisen II</c>, <c>Specials</c>.
    /// </summary>
    /// <param name="folder">Folder name.</param>
    /// <returns>Season or null.</returns>
    public static int? SeasonOfFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (SpecialsFolderRegex().IsMatch(folder))
        {
            return 0;
        }

        var m = FolderSeasonRegex().Match(folder);
        if (m.Success)
        {
            return Int(m.Groups["s"].Value);
        }

        m = RomanRegex().Match(folder);
        return m.Success ? m.Groups["r"].Value.ToUpperInvariant() switch
        {
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            _ => null,
        } : null;
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool IsYear(string s) => s.Length == 4 && (s.StartsWith("19", StringComparison.Ordinal) || s.StartsWith("20", StringComparison.Ordinal));

    [GeneratedRegex(@"(?:^|[\s._\[(-])(?<kind>NCOP|NCED|no-?credit[ _-]?opening|no-?credit[ _-]?ending|creditless[ _-]?(?:op|ed)|OP|ED|Menu|PV|CM|Preview|Trailer|Teaser|SP|OVA|OAD|Special)[ _-]?(?<n>\d{1,2})?(?=$|[\s._\])-])", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraRegex();

    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]*e(?<e>\d{1,4})(?:[-e]+(?<end>\d{1,4}))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyyRegex();

    [GeneratedRegex(@"TV-?(?<s>\d{1,2})[ ._]*-[ ._]*(?<e>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TvSeasonRegex();

    [GeneratedRegex(@"(?<![a-z0-9])S(?<s>\d{1,2})(?![0-9e])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonTokenRegex();

    [GeneratedRegex(@"\[(?<e>\d{1,4})[ _]of[ _]\d{1,4}\]", RegexOptions.IgnoreCase)]
    private static partial Regex OfRegex();

    [GeneratedRegex(@"\[(?<e>\d{1,4})(?:v\d)?\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s-\s(?<e>\d{1,4})(?:-(?<end>\d{1,4}))?(?:v\d)?(?=$|[\s\[(.])")]
    private static partial Regex DashRegex();

    [GeneratedRegex(@"_(?<e>\d{1,3})(?:v\d)?_")]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"^[A-Za-z]{1,5}\.(?<e>\d{1,3})(?<seg>[ab])?\.", RegexOptions.IgnoreCase)]
    private static partial Regex CodeSegmentRegex();

    [GeneratedRegex(@"^(?<e>\d{1,4})[.\s]")]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:E|EP|Episode)[ ._]?(?<e>\d{1,4})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeTokenRegex();

    [GeneratedRegex(@"[ ._](?<e>\d{1,3})(?:v\d)?[ ._]*[\[(]")]
    private static partial Regex TrailingNumberRegex();

    [GeneratedRegex(@"^(?:specials?|sps?|extras?|bonus|сп|спешлы)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderRegex();

    [GeneratedRegex(@"(?:season|сезон|series)[ ._]*(?<s>\d{1,2})(?![0-9])|(?<![a-z0-9])S(?<s>\d{1,2})(?![0-9e])|\bTV-?(?<s>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex FolderSeasonRegex();

    [GeneratedRegex(@"\s(?<r>II|III|IV|V|VI)(?:$|\s)")]
    private static partial Regex RomanRegex();
}
