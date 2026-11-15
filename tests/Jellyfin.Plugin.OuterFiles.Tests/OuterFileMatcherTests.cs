using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.OuterFiles.Matching;
using Xunit;

namespace Jellyfin.Plugin.OuterFiles.Tests;

/// <summary>
/// Tests the matcher against the layout of a real release (VCB-Studio, 12 episodes, separate RU/EN folders).
/// </summary>
public sealed class OuterFileMatcherTests
{
    private const string Root = "/mnt/series2/Jitsu wa Ore, Saikyou deshita [2023][BDRip][1080p]";
    private const string Release = "[VCB-Studio] Jitsu wa Ore, Saikyou Deshita";

    private static string Video(int number, string tag = "[Ma10p_1080p][x265_flac]")
        => $"{Root}/{Release} [{number:00}]{tag}.mkv";

    private static VideoCandidate VideoElement(int number)
        => new(Video(number));

    private static List<TrackCandidate> Layout()
    {
        var list = new List<TrackCandidate>();
        for (var ep = 1; ep <= 12; ep++)
        {
            list.Add(new TrackCandidate($"{Root}/EN Subs/{Release} [{ep:00}][Ma10p_1080p][x265_flac].en_CR.ass", false));
            list.Add(new TrackCandidate($"{Root}/JP Subs/{Release} [{ep:00}][Ma10p_1080p][x265_flac].jp.srt", false));
            list.Add(new TrackCandidate($"{Root}/RU Subs/{Release} [{ep:00}][Ma10p_1080p][x265_flac].ru_CR.ass", false));
            list.Add(new TrackCandidate($"{Root}/EN Sound/{Release} [{ep:00}][Ma10p_1080p][x265_flac].en_CR.mka", true));
            list.Add(new TrackCandidate($"{Root}/EN Sound/signs/{Release} [{ep:00}][Ma10p_1080p][x265_flac].en_signs_CR.ass", false));
            list.Add(new TrackCandidate($"{Root}/RU Sound [Anilibria]/{Release} [{ep:00}][Ma10p_1080p][x265_flac].ru_Anilibria.mka", true));
            list.Add(new TrackCandidate($"{Root}/RU Sound [Anilibria]/надписи/{Release} [{ep:00}][Ma10p_1080p][x265_flac].ru_signs_CR.ass", false));
        }

        return list;
    }

    private static string Relative(string path) => path[Root.Length..].TrimStart('/');

    private static IReadOnlyList<MatchedTrack> Match(int ep)
        => new OuterFileMatcher().Match(VideoElement(ep), Layout(), Relative);

    [Fact]
    public void MatchesEveryTrackOfItsOwnEpisode()
    {
        var matched = Match(5);

        Assert.Equal(7, matched.Count);
        Assert.All(matched, m => Assert.Contains($"[{5:00}]", m.Track.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void NeverMatchesTracksOfAnotherEpisode()
    {
        for (var ep = 1; ep <= 12; ep++)
        {
            var matched = Match(ep);
            Assert.All(matched, m => Assert.Contains($"[{ep:00}]", m.Track.Path, StringComparison.Ordinal));
            Assert.Equal(7, matched.Count);
        }
    }

    [Fact]
    public void DetectsAudioAndSubtitles()
    {
        var matched = Match(1);
        Assert.Equal(2, matched.Count(m => m.Track.IsAudio));
        Assert.Equal(5, matched.Count(m => !m.Track.IsAudio));
    }

    [Fact]
    public void DetectsLanguagesFromFolders()
    {
        var matched = Match(3);

        var jp = Assert.Single(matched, m => m.Track.Path.Contains("/JP Subs/", StringComparison.Ordinal));
        Assert.Equal("jpn", jp.Language);
        Assert.False(jp.Track.IsAudio);


        // EN Subs + EN Sound (+ signs внутри EN Sound): язык берётся из имени папки.
        // Вложенный каталог "EN Sound/signs" не содержит "/EN " с пробелом, поэтому проверяем по префиксу.
        var en = matched.Where(m => m.Track.Path.Contains("/EN", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, en.Count);
        Assert.All(en, m => Assert.Equal("eng", m.Language));

        // "RU Subs" + "RU Sound [Anilibria]" + "RU Sound [Anilibria]/надписи"
        var ru = matched.Where(m => m.Track.Path.Contains("/RU", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, ru.Count);
        Assert.All(ru, m => Assert.Equal("rus", m.Language));
    }

    [Fact]
    public void MarksSignsSubtitlesAsForced()
    {
        var matched = Match(4);

        var signs = matched.Where(m => OuterFileMatcher.ContainsForced(m.Track.Path)).ToList();
        Assert.Equal(2, signs.Count);
        Assert.All(signs, m => Assert.True(m.IsForced));

        Assert.All(matched.Where(m => !OuterFileMatcher.ContainsForced(m.Track.Path)), m => Assert.False(m.IsForced));
    }

    [Fact]
    public void UsesStudioFromFlagSequenceAsTitle()
    {
        var matched = Match(6);

        var anilibria = Assert.Single(matched, m => m.Track.Path.EndsWith(".ru_Anilibria.mka", StringComparison.Ordinal));
        Assert.Equal("Anilibria", anilibria.Title);
    }

    [Fact]
    public void ExtractsEpisodeNumbersFromStems()
    {
        Assert.Equal(["1"], OuterFileMatcher.ExtractNumbers(OuterFileMatcher.StripReleaseTags("[VCB-Studio] Show [01][Ma10p_1080p][x265_flac]")));
        Assert.Equal(["12"], OuterFileMatcher.ExtractNumbers(OuterFileMatcher.StripReleaseTags("[VCB-Studio] Show [12][Ma10p_1080p]")));
        Assert.Empty(OuterFileMatcher.ExtractNumbers(OuterFileMatcher.StripReleaseTags("[VCB-Studio] Show [NCOP][Ma10p_1080p]")));
        Assert.Equal(["5"], OuterFileMatcher.ExtractNumbers(OuterFileMatcher.StripReleaseTags("[NC-Raws] Chainsaw Man - 05 [WEBRip 1080p x264 E-AC-3]")));
    }

    [Fact]
    public void ExtractsSeasonAndEpisodeNumbers()
    {
        var numbers = OuterFileMatcher.ExtractNumbers("Show S02E07");
        Assert.Contains("7", numbers);
    }

    [Fact]
    public void ReadsLanguageFromText()
    {
        Assert.Equal("rus", OuterFileMatcher.LanguageFromText("RU Subs"));
        Assert.Equal("eng", OuterFileMatcher.LanguageFromText("EN Sound"));
        Assert.Equal("jpn", OuterFileMatcher.LanguageFromText("JP Subs"));
        Assert.Null(OuterFileMatcher.LanguageFromText("надписи"));
        Assert.Null(OuterFileMatcher.LanguageFromText("signs"));
    }

    [Fact]
    public void ReadsLanguageFromFlags()
    {
        Assert.Equal("rus", OuterFileMatcher.LanguageFromFlags($"{Release} [01][Ma10p_1080p][x265_flac].ru_CR"));
        Assert.Equal("eng", OuterFileMatcher.LanguageFromFlags($"{Release} [01][Ma10p_1080p][x265_flac].en_CR"));
        Assert.Equal("jpn", OuterFileMatcher.LanguageFromFlags($"{Release} [01][Ma10p_1080p][x265_flac].jp"));
        Assert.Null(OuterFileMatcher.LanguageFromFlags($"{Release} [01][Ma10p_1080p][x265_flac]"));
    }

    [Fact]
    public void ReadsStudioFromFlags()
    {
        Assert.Equal("Anilibria", OuterFileMatcher.StudioFromFlags($"{Release} [01][Ma10p_1080p][x265_flac].ru_Anilibria"));
        Assert.Null(OuterFileMatcher.StudioFromFlags($"{Release} [01][Ma10p_1080p][x265_flac].ru_CR"));
    }

    [Fact]
    public void RemovesReleaseTags()
    {
        Assert.Equal("Show 01", OuterFileMatcher.StripReleaseTags("[VCB-Studio] Show [01][Ma10p_1080p][x265_flac]"));
    }

    [Fact]
    public void NormalizesNames()
    {
        Assert.Equal(OuterFileMatcher.Normalize("Show - 01"), OuterFileMatcher.Normalize("Show_01"));
        Assert.Equal("show 01", OuterFileMatcher.Normalize("Show—01"));
    }

    [Fact]
    public void VideoWithoutEpisodeNumberGetsNoTracks()
    {
        var video = new VideoCandidate($"{Root}/SPs/{Release} [NCOP][Ma10p_1080p][x265_flac].mkv");
        var matched = new OuterFileMatcher().Match(video, Layout(), Relative);
        Assert.Empty(matched);
    }

    [Fact]
    public void PlainAdjacentFileStillMatches()
    {
        var video = new VideoCandidate("/media/show/S01/Show - S01E02.mkv");
        var tracks = new List<TrackCandidate>
        {
            new("/media/show/S01/Show - S01E02.ru.srt", false),
            new("/media/show/S01/Show - S01E03.ru.srt", false)
        };

        var matched = new OuterFileMatcher().Match(video, tracks, p => p);
        Assert.Single(matched);
        Assert.EndsWith("S01E02.ru.srt", matched[0].Track.Path, StringComparison.Ordinal);
        Assert.Equal("rus", matched[0].Language);
    }
}