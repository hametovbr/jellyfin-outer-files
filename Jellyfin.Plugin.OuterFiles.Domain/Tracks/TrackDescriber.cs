using System.Text.RegularExpressions;
using Jellyfin.Plugin.OuterFiles.Domain.Matching;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tracks;

/// <summary>
/// What a track is: language, studio (shown as the track title), signs-only, hearing impaired.
/// </summary>
/// <param name="Language">ISO 639-2 code, or null when nothing in the names says it.</param>
/// <param name="LanguageSource">Where the language came from: suffix, folder, text, default, unknown.</param>
/// <param name="Studio">Dub/sub studio or release group, or null.</param>
/// <param name="Forced">Signs-only subtitles.</param>
/// <param name="HearingImpaired">SDH subtitles.</param>
public sealed record TrackDescriptor(string? Language, string LanguageSource, string? Studio, bool Forced, bool HearingImpaired)
{
    /// <summary>
    /// Returns a copy with the language filled in when it is still unknown.
    /// </summary>
    /// <param name="language">Language to use.</param>
    /// <param name="source">Where it came from.</param>
    /// <returns>The descriptor.</returns>
    public TrackDescriptor WithFallbackLanguage(string? language, string source)
        => Language is null && !string.IsNullOrEmpty(language) ? this with { Language = language, LanguageSource = source } : this;
}

/// <summary>
/// Describes a matched track from the folders between it and its video and from the name suffix.
/// Precedence: the suffix (closest to the file) beats folders; a deeper folder beats an outer one.
/// </summary>
public static partial class TrackDescriber
{
    /// <summary>
    /// Describes a track.
    /// </summary>
    /// <param name="match">The match.</param>
    /// <returns>The descriptor; language may still be null.</returns>
    public static TrackDescriptor Describe(TrackMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        string? language = null;
        string? studio = null;
        var source = "unknown";
        var forced = false;
        var hearingImpaired = false;

        foreach (var folder in match.FolderChain)
        {
            var f = AnalyzeFolder(folder);
            if (f.Language is not null)
            {
                language = f.Language;
                source = "folder";
            }

            studio = f.Studio ?? studio;
            forced |= f.Forced;
        }

        var s = AnalyzeSuffix(match.Suffix);
        if (s.Language is not null)
        {
            language = s.Language;
            source = "suffix";
        }

        studio = s.Studio ?? studio;
        forced |= s.Forced;
        hearingImpaired |= s.HearingImpaired;
        return new TrackDescriptor(language, language is null ? "unknown" : source, studio, forced, hearingImpaired);
    }

    /// <summary>
    /// Reads one folder name, e.g. <c>RU Sound [Anilibria]</c>, <c>Rus Sub by A-D Team</c>, <c>ForceMedia</c>.
    /// </summary>
    /// <param name="name">Folder name.</param>
    /// <returns>What the folder says.</returns>
    public static (string? Language, string? Studio, bool Forced) AnalyzeFolder(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? language = null;
        string? studio = null;
        var forced = false;

        // Bracket contents: "[Anilibria]", "(Wakanim)".
        foreach (Match m in BracketRegex().Matches(name))
        {
            var inner = m.Groups["in"].Value.Trim();
            var words = Words(inner);
            if (words.Length == 1 && FolderVocabulary.Languages.TryGetValue(words[0], out var lang))
            {
                language = lang;
            }
            else if (words.Any(w => FolderVocabulary.ForcedWords.Contains(w)))
            {
                forced = true;
            }
            else if (words.Length > 0 && !words.All(IsInformationless))
            {
                studio = inner;
            }
        }

        var rest = BracketRegex().Replace(name, " ");
        var tokens = Words(rest);
        var by = Array.FindIndex(tokens, t => t.Equals("by", StringComparison.OrdinalIgnoreCase) || t.Equals("от", StringComparison.OrdinalIgnoreCase));
        if (by >= 0 && by < tokens.Length - 1)
        {
            studio = string.Join(' ', tokens[(by + 1)..]);
            tokens = tokens[..by];
        }

        var residue = new List<string>();
        foreach (var t in tokens)
        {
            if (FolderVocabulary.Languages.TryGetValue(t, out var lang))
            {
                language = lang;
            }
            else if (FolderVocabulary.ForcedWords.Contains(t))
            {
                forced = true;
            }
            else if (!IsInformationless(t))
            {
                residue.Add(t);
            }
        }

        if (studio is null && residue.Count > 0)
        {
            studio = string.Join(' ', residue);
        }

        return (language, studio, forced);
    }

    /// <summary>
    /// Reads the part of a track name after the video stem, e.g. <c>.ru_Anilibria</c>, <c>.en_signs_CR</c>,
    /// <c>.rus.19</c>, <c>.eng.SDH</c>, <c>.Audio Commentary</c>.
    /// </summary>
    /// <param name="suffix">The suffix (may be empty).</param>
    /// <returns>What the suffix says.</returns>
    public static (string? Language, string? Studio, bool Forced, bool HearingImpaired) AnalyzeSuffix(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        string? language = null;
        var forced = false;
        var hi = false;
        var studio = new List<string>();

        foreach (var segment in suffix.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var token in segment.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (t.Length == 0 || t.All(char.IsAsciiDigit))
                {
                    continue; // ".rus.19": stream index from an extraction tool
                }

                if (language is null && FolderVocabulary.Languages.TryGetValue(t, out var lang))
                {
                    language = lang;
                }
                else if (FolderVocabulary.ForcedWords.Contains(t))
                {
                    forced = true;
                }
                else if (t.Equals("sdh", StringComparison.OrdinalIgnoreCase) || t.Equals("hi", StringComparison.OrdinalIgnoreCase) || t.Equals("cc", StringComparison.OrdinalIgnoreCase))
                {
                    hi = true;
                }
                else if (!t.Equals("default", StringComparison.OrdinalIgnoreCase) && !IsCodecName(t) && !IsInformationless(t))
                {
                    studio.Add(t);
                }
            }
        }

        return (language, studio.Count == 0 ? null : string.Join(' ', studio), forced, hi);
    }

    /// <summary>
    /// Tells whether a track name suffix was produced by an extraction tool (<c>.rus.19</c>, <c>.subrip.3</c>,
    /// <c>.hdmv_pgs_subtitle.22</c>): Jellyfin shows such leftovers as track titles ("19").
    /// </summary>
    /// <param name="suffix">Suffix after the video stem, without extension.</param>
    /// <returns>True when it contains a stream index or codec name.</returns>
    public static bool HasToolSuffix(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return suffix.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.All(char.IsAsciiDigit) || IsCodecName(t));
    }

    private static bool IsInformationless(string word)
        => FolderVocabulary.TypeWords.Contains(word)
            || FolderVocabulary.NoiseWords.Contains(word)
            || word.All(c => char.IsDigit(c) || char.IsPunctuation(c));

    // Track names produced by extraction tools: ".subrip.3", ".hdmv_pgs_subtitle.22", ".ass.4".
    private static bool IsCodecName(string token) => token.ToLowerInvariant() is
        "subrip" or "hdmv" or "pgs" or "subtitle" or "ass" or "ssa" or "dvd" or "dvb" or "mov" or "text" or "webvtt" or "srt";

    private static string[] Words(string text) => text.Split([' ', '_', '.', '\t'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"[\[(](?<in>[^\[\]()]*)[\])]")]
    private static partial Regex BracketRegex();
}
