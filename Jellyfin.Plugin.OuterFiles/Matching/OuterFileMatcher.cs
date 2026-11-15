using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Matching;

/// <summary>
/// A track file found somewhere below the folder of a video, already classified.
/// </summary>
/// <param name="Path">Absolute path of the track file.</param>
/// <param name="IsAudio">True for audio tracks, false for subtitles.</param>
public sealed record TrackCandidate(string Path, bool IsAudio)
{
    /// <summary>
    /// Gets the file name without extension, used to validate episode numbers.
    /// </summary>
    public string FileNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// Gets the file extension in lower case, including the dot.
    /// </summary>
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>
/// The video file a track can be attached to.
/// </summary>
/// <param name="Path">Absolute path of the video file.</param>
public sealed record VideoCandidate(string Path)
{
    /// <summary>
    /// Gets the file name without extension, e.g. "[VCB-Studio] Show [01][Ma10p_1080p][x265_flac]".</summary>
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// Gets the name of the folder containing the video.</summary>
    public string DirectoryName => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path) ?? string.Empty);
}

/// <summary>
/// A track that was matched to a video.
/// </summary>
/// <param name="Track">The matched track.</param>
/// <param name="Language">Three letter language code, or null when unknown.</param>
/// <param name="Title">Human readable title shown in the client (studio / release group), or null.</param>
/// <param name="IsForced">True for signs-only subtitle tracks.</param>
public sealed record MatchedTrack(TrackCandidate Track, string? Language, string? Title, bool IsForced)
{
    /// <summary>
    /// Applies the language, title and forced flag of this match to a probed stream.
    /// </summary>
    /// <param name="stream">Stream returned by the media encoder.</param>
    /// <returns>The same stream, adjusted.</returns>
    public MediaBrowser.Model.Entities.MediaStream Adjust(MediaBrowser.Model.Entities.MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Path = Track.Path;
        stream.IsExternal = true;
        stream.IsForced = IsForced || stream.IsForced;
        if (!string.IsNullOrEmpty(Language))
        {
            stream.Language = Language;
        }

        if (!string.IsNullOrEmpty(Title))
        {
            stream.Title = Title;
        }

        return stream;
    }
}

/// <summary>
/// Matches external audio and subtitle files that live in sub folders to the video they belong to.
/// </summary>
/// <remarks>
/// The built in Jellyfin logic only looks at files next to the video and only accepts names that start
/// with the video file name. Release groups store extra audio tracks and subtitles in sibling folders
/// ("RU Subs", "EN Sound", "RU Sound [Anilibria]/надписи"), so those files never reach the library.
/// This matcher walks the whole folder of the video and decides by release metadata instead:
/// season/episode numbers, a shared common prefix and the language/studio hints in folder and file names.
/// </remarks>
public sealed partial class OuterFileMatcher
{
    private static readonly Dictionary<string, string> LanguageByToken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jp"] = "jpn",
        ["jpn"] = "jpn",
        ["ja"] = "jpn",
        ["jap"] = "jpn",
        ["ru"] = "rus",
        ["rus"] = "rus",
        ["ru-ru"] = "rus",
        ["en"] = "eng",
        ["eng"] = "eng",
        ["us"] = "eng",
        ["uk"] = "ukr",
        ["ua"] = "ukr",
        ["ukr"] = "ukr",
        ["kz"] = "kaz",
        ["zh"] = "zho",
        ["cn"] = "zho",
        ["chs"] = "zho",
        ["cht"] = "zho",
        ["ko"] = "kor",
        ["kr"] = "kor",
        ["fr"] = "fra",
        ["fre"] = "fra",
        ["de"] = "deu",
        ["ger"] = "deu",
        ["es"] = "spa",
        ["esp"] = "spa",
        ["it"] = "ita",
        ["ita"] = "ita",
        ["pt"] = "por",
        ["br"] = "por"
    };

    private static readonly HashSet<string> ReleaseTagWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ma10p", "10bit", "8bit", "1080p", "720p", "2160p", "480p", "x265", "x264", "h264", "h265",
        "hevc", "avc", "flac", "aac", "ac3", "dts", "webrip", "webdl", "bdrip", "bluray", "blu-ray",
        "bd", "tv", "remux", "hr", "hdr", "sdr", "mkv"
    };

    private static readonly HashSet<string> ExcludedStudioTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "CR", "signs", "forced", "default", "sudo", "ext", "hi", "sdh", "cc"
    };

    /// <summary>
    /// Gets the subtitle extensions the scanner treats as external tracks.
    /// </summary>
    public static IReadOnlyCollection<string> SubtitleExtensions { get; } =
        [".ass", ".ssa", ".srt", ".vtt", ".sub", ".idx", ".smi", ".sami", ".mks"];

    private static readonly char[] WordSeparators = [' ', '_', '-', '.', '&', '+', ',', '(', ')', '[', ']'];

    /// <summary>
    /// Extracts release tags from a name like "[VCB-Studio] Show [01][Ma10p_1080p][x265_flac]".
    /// </summary>
    [GeneratedRegex(@"\[(?<tag>[^\[\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketTagRegex();

    /// <summary>
    /// Extracts leading numbers from a word, e.g. "01" or "S01E01".
    /// </summary>
    [GeneratedRegex(@"(?:^|[^0-9])(?:S(?<season>\d{1,2}))?(?:[^0-9]|^)?(?<episode>\d{1,4})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeNumberRegex();

    /// <summary>
    /// Language token at the start or end of a dotted flag sequence: ".ru_Anilibria", ".en_CR".
    /// </summary>
    [GeneratedRegex(@"(?:^|[._\-\s])(?<lang>[A-Za-z]{2,3})(?=[._\-\s]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FlagLanguageRegex();

    /// <summary>
    /// Selects the tracks that belong to <paramref name="video"/>.
    /// </summary>
    /// <param name="video">The video to attach tracks to.</param>
    /// <param name="candidates">Every audio/subtitle candidate found below the video's folder.</param>
    /// <param name="relativePathProvider">Returns the path of the candidate relative to the scan root, used for language hints of parent folders.</param>
    /// <returns>Matched tracks in a stable order: subtitles first, then audio.</returns>
    public IReadOnlyList<MatchedTrack> Match(VideoCandidate video, IEnumerable<TrackCandidate> candidates, Func<string, string> relativePathProvider)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(relativePathProvider);

        var videoNumbers = ExtractNumbers(StripReleaseTags(video.Stem));
        var videoStem = Normalize(video.Stem);
        var candidatesList = candidates.ToList();
        var stems = candidatesList.Select(c => Normalize(c.FileNameWithoutExtension)).Distinct(StringComparer.Ordinal).ToList();
        var folderTokens = ExtractFolderLanguageTokens(video.DirectoryName);

        var matched = new List<MatchedTrack>();
        foreach (var candidate in candidatesList)
        {
            if (!IsSameEpisode(video, videoStem, videoNumbers, candidate, stems))
            {
                continue;
            }

            var (language, isForced, studio) = Describe(candidate, relativePathProvider, folderTokens);
            var title = BuildTitle(candidate, studio, isForced);
            matched.Add(new MatchedTrack(candidate, language, title, isForced));
        }

        return matched
            .OrderBy(m => m.Track.IsAudio)
            .ThenBy(m => m.Track.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Decides whether a candidate file belongs to the video.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="videoStem">Normalized video stem.</param>
    /// <param name="videoNumbers">Episode numbers found in the video stem.</param>
    /// <param name="candidate">The candidate track.</param>
    /// <param name="allStems">Normalized stems of every candidate in the same batch.</param>
    /// <returns>True when the track belongs to the video.</returns>
    public static bool IsSameEpisode(VideoCandidate video, string videoStem, IReadOnlyCollection<string> videoNumbers, TrackCandidate candidate, IReadOnlyCollection<string> allStems)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(candidate);

        var stem = Normalize(candidate.FileNameWithoutExtension);

        // Exact stem (or an extension flag) always wins: that is the documented Jellyfin behaviour.
        if (stem.Equals(videoStem, StringComparison.Ordinal) || stem.StartsWith(videoStem, StringComparison.Ordinal))
        {
            return true;
        }

        // The video must carry an explicit episode number, otherwise we cannot tell the tracks apart.
        if (videoNumbers.Count == 0)
        {
            return false;
        }

        return ContainsNumber(stem, videoNumbers)
            && !ContainsNumber(stem, ExtractNumbers(StripReleaseTags(video.DirectoryName)))
            && IsAgainstOtherEpisodes(stem, videoStem, videoNumbers, allStems);
    }

    private static bool IsAgainstOtherEpisodes(string stem, string videoStem, IReadOnlyCollection<string> videoNumbers, IReadOnlyCollection<string> allStems)
    {
        // A track whose numbers include another episode number must be rejected, unless it also has the video's number
        // and that number is the episode (not the year / season).
        var numbers = ExtractNumbers(stem).ToList();
        if (numbers.Count == 0)
        {
            return false;
        }

        var shared = numbers.Intersect(videoNumbers, StringComparer.Ordinal).ToList();
        if (shared.Count == 0)
        {
            return false;
        }

        var otherEpisodes = allStems.Count(s => s.Contains(stem, StringComparison.Ordinal));

        // If several different episode videos exist and this track is the same distance from the video stem as from
        // another stem, it is ambiguous; only accept it when the stems share a long common prefix.
        return otherEpisodes == 0 || CommonPrefixLength(stem, videoStem) >= 12;
    }

    private static (string? Language, bool IsForced, string? Studio) Describe(TrackCandidate candidate, Func<string, string> relativePathProvider, IReadOnlyList<string> folderTokens)
    {
        var relative = relativePathProvider(candidate.Path);
        var segments = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var folderSegments = segments.Length > 1 ? segments[..^1] : [];

        string? language = null;
        foreach (var segment in folderSegments)
        {
            var lang = LanguageFromText(segment);
            if (lang is not null)
            {
                language = lang;
            }
        }

        if (language is null)
        {
            language = LanguageFromFlags(candidate.FileNameWithoutExtension) ?? (folderTokens.Count > 0 ? folderTokens[^1] : null);
        }

        var isForced = ContainsForced(relative);
        var studio = StudioFromFlags(candidate.FileNameWithoutExtension);
        return (language, isForced, studio);
    }

    private static string? BuildTitle(TrackCandidate candidate, string? studio, bool isForced)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(studio))
        {
            parts.Add(studio);
        }

        if (isForced)
        {
            parts.Add("Signs");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Normalizes a name for comparisons: lower case, forged dashes, collapsed separators.
    /// </summary>
    /// <param name="value">Name.</param>
    /// <returns>Normalized name.</returns>
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var length = 0;
        var previousIsSeparator = true;
        foreach (var raw in value.ToLowerInvariant())
        {
            var c = NormalizeSeparator(raw);
            var isSeparator = c == ' ';
            if (isSeparator && previousIsSeparator)
            {
                continue;
            }

            buffer[length++] = c;
            previousIsSeparator = isSeparator;
        }

        return new string(buffer, 0, length).TrimEnd();
    }

    private static char NormalizeSeparator(char c)
    {
        switch (c)
        {
            case '—':
            case '–':
            case '-':
            case '_':
            case '.':
                return ' ';
            default:
                return c;
        }
    }

    /// <summary>
    /// Removes bracketed release tags: "[VCB-Studio] Show [01][Ma10p_1080p]" → "Show 01".
    /// </summary>
    /// <param name="value">Name.</param>
    /// <returns>Name without bracket tags.</returns>
    public static string StripReleaseTags(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // "[01]" is the episode number of nearly every anime release and has to survive, everything else in
        // brackets is a release tag ("[VCB-Studio]", "[Ma10p_1080p]").
        var replaced = BracketTagRegex().Replace(
            value,
            match => match.Groups["tag"].Value.Trim().All(char.IsAsciiDigit) ? " " + match.Groups["tag"].Value : " ");

        return string.Join(' ', replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Extracts the numeric tokens of a name, e.g. "Show 01" → ["01"].
    /// </summary>
    /// <param name="value">Name without release tags.</param>
    /// <returns>Numbers as strings without leading zeros.</returns>
    public static IReadOnlyCollection<string> ExtractNumbers(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var numbers = new List<string>();
        foreach (var token in value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = EpisodeNumberRegex().Match(token);
            if (match.Success)
            {
                var episode = match.Groups["episode"].Value.TrimStart('0');
                if (episode.Length > 0)
                {
                    numbers.Add(episode);
                }
            }
        }

        return numbers;
    }

    /// <summary>
    /// Detects the language of a folder name: "RU Subs" → rus, "EN Sound" → eng.
    /// </summary>
    /// <param name="folderName">Folder name.</param>
    /// <returns>Three letter language code or null.</returns>
    public static string? LanguageFromText(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var tokens = folderName.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var normalized = token.ToUpperInvariant();
            if (normalized == "UP" || normalized == "US" || normalized == "IT")
            {
                // Latin tokens that are not languages in our context.
                continue;
            }

            if (LanguageByToken.TryGetValue(token, out var language) && token.Length <= 3)
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the language from a dotted flag sequence at the end of a stem: "....ru_Anilibria" → rus.
    /// </summary>
    /// <param name="stem">File name without extension.</param>
    /// <returns>Language or null.</returns>
    public static string? LanguageFromFlags(string stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        var lastSegment = stem.Split('.').LastOrDefault();
        if (string.IsNullOrEmpty(lastSegment))
        {
            return null;
        }

        var match = FlagLanguageRegex().Match(lastSegment);
        if (!match.Success)
        {
            return null;
        }

        var token = match.Groups["lang"].Value;
        return LanguageByToken.TryGetValue(token, out var language) ? language : null;
    }

    /// <summary>
    /// Reads the studio from the flag sequence: "....ru_Anilibria" → "Anilibria".
    /// </summary>
    /// <param name="stem">File name without extension.</param>
    /// <returns>Studio or null.</returns>
    public static string? StudioFromFlags(string stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        var lastSegment = stem.Split('.').LastOrDefault();
        if (string.IsNullOrEmpty(lastSegment))
        {
            return null;
        }

        var parts = lastSegment.Split(['_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && LanguageByToken.ContainsKey(parts[0]))
        {
            var studio = string.Join(" ", parts.Skip(1)).Trim();
            return ExcludedStudioTokens.Contains(studio) || ReleaseTagWords.Contains(studio) ? null : studio;
        }

        if (parts.Length == 1
            && !ContainsForced(parts[0])
            && !ReleaseTagWords.Contains(parts[0])
            && !ExcludedStudioTokens.Contains(parts[0])
            && !LanguageByToken.ContainsKey(parts[0])
            && parts[0].Length > 2)
        {
            return parts[0];
        }

        return null;
    }

    /// <summary>
    /// Detects forced (signs only) subtitle tracks.
    /// </summary>
    /// <param name="value">Path or name.</param>
    /// <returns>True for signs-only subtitles.</returns>
    public static bool ContainsForced(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var token in value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("signs", StringComparison.OrdinalIgnoreCase)
                || token.Equals("forced", StringComparison.OrdinalIgnoreCase)
                || token.Equals("надписи", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects language hints from the folder name of the video, used as a fallback.
    /// </summary>
    /// <param name="seriesFolderName">Name of the folder holding the series or movie.</param>
    /// <returns>Hints, most specific last.</returns>
    public static IReadOnlyList<string> ExtractFolderLanguageTokens(string seriesFolderName)
    {
        var result = new List<string>();
        var token = LanguageFromText(seriesFolderName);
        if (token is not null)
        {
            result.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Subtitle extensions accepted as external tracks.
    /// </summary>
    /// <summary>
    /// Gets the subtitle file extensions accepted as outer tracks.
    /// </summary>
    /// <param name="extension">Extension with the dot, lower case.</param>
    /// <returns>True for a subtitle file.</returns>
    public static bool IsSubtitleExtension(string extension)
        => extension is ".ass" or ".ssa" or ".srt" or ".vtt" or ".sub" or ".idx" or ".smi" or ".sami" or ".mks";

    private static bool ContainsNumber(string normalizedStem, IReadOnlyCollection<string> numbers)
    {
        var candidates = ExtractNumbers(StripReleaseTags(normalizedStem));
        return numbers.Any(n => candidates.Contains(n, StringComparer.Ordinal));
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        for (; i < length; i++)
        {
            if (a[i] != b[i])
            {
                break;
            }
        }

        return i;
    }

    /// <summary>
    /// Formats a language code for the track title.
    /// </summary>
    /// <param name="language">Three letter code.</param>
    /// <returns>Readable name or null.</returns>
    public static string? LanguageDisplayName(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}