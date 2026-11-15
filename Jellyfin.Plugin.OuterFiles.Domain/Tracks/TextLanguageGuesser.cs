using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tracks;

/// <summary>
/// Guesses the language of a text subtitle by its script, for tracks whose names say nothing
/// (<c>Subtitles/Wakanim/…ass</c>). Conservative: returns null unless one script clearly dominates;
/// Latin text is only called English when common English words make up a real share of it.
/// </summary>
public static partial class TextLanguageGuesser
{
    private const int MinLetters = 80;

    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "you", "to", "is", "it", "that", "of", "what", "i", "a", "this", "in", "we", "my", "me", "your", "are", "for", "no",
    };

    /// <summary>
    /// Guesses a language.
    /// </summary>
    /// <param name="content">Subtitle file content (the first tens of kilobytes are enough).</param>
    /// <param name="extension">File extension, used to strip format noise.</param>
    /// <returns>ISO 639-2 code or null.</returns>
    public static string? Guess(string content, string extension)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = extension.ToLowerInvariant() is ".ass" or ".ssa" ? AssDialogue(content) : StripMarkup(content);

        int cyr = 0, ukr = 0, lat = 0, kana = 0, han = 0, hangul = 0;
        foreach (var c in text)
        {
            if (c is >= 'Ѐ' and <= 'ӿ')
            {
                cyr++;
                if (c is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ')
                {
                    ukr++;
                }
            }
            else if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                lat++;
            }
            else if (c is >= '぀' and <= 'ヿ')
            {
                kana++;
            }
            else if (c is >= '一' and <= '鿿')
            {
                han++;
            }
            else if (c is >= '가' and <= '힯')
            {
                hangul++;
            }
        }

        var total = cyr + lat + kana + han + hangul;
        if (total < MinLetters)
        {
            return null;
        }

        if (cyr >= total * 0.6)
        {
            return ukr * 100 >= cyr ? "ukr" : "rus";
        }

        if (kana >= total * 0.1 && kana + han >= total * 0.6)
        {
            return "jpn";
        }

        if (han >= total * 0.6)
        {
            return "zho";
        }

        if (hangul >= total * 0.6)
        {
            return "kor";
        }

        if (lat >= total * 0.8)
        {
            var words = WordRegex().Matches(text).Select(m => m.Value).ToList();
            var english = words.Count(w => EnglishWords.Contains(w));
            return words.Count >= 30 && english * 100 >= words.Count * 12 ? "eng" : null;
        }

        return null;
    }

    private static string AssDialogue(string content)
    {
        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Text is everything after the 9th comma.
            var idx = -1;
            for (var i = 0; i < 9 && (idx = line.IndexOf(',', idx + 1)) >= 0; i++)
            {
            }

            if (idx >= 0)
            {
                sb.Append(OverrideRegex().Replace(line[(idx + 1)..], " ").Replace("\\N", " ", StringComparison.Ordinal)).Append(' ');
            }
        }

        return sb.ToString();
    }

    private static string StripMarkup(string content) => MarkupRegex().Replace(content, " ");

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideRegex();

    [GeneratedRegex(@"<[^>]*>|\d{1,2}:\d{2}:\d{2}[,.]\d{3}|-->")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"[A-Za-z']+")]
    private static partial Regex WordRegex();
}
