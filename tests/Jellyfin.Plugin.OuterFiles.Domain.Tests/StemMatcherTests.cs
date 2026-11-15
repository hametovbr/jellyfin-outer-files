using Jellyfin.Plugin.OuterFiles.Domain.Matching;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class StemMatcherTests
{
    [Theory]
    [InlineData("Show - S01E01", "Show - S01E01", true)]
    [InlineData("Show - S01E01.ru", "Show - S01E01", true)]
    [InlineData("show - s01e01.RU", "Show - S01E01", true)]
    [InlineData("Show - S01E010", "Show - S01E01", false)]
    [InlineData("Show - S01E01 ru", "Show - S01E01", false)]
    [InlineData("Show", "Show - S01E01", false)]
    public void FollowsJellyfinsPrefixRule(string track, string video, bool expected) =>
        Assert.Equal(expected, StemMatcher.Continues(track, video));

    [Fact]
    public void MatchesEveryTrackInSubFoldersOfJujutsuKaisenToItsOwnEpisode()
    {
        var result = StemMatcher.Match(Corpus.Under(Corpus.Series + "/JUJUTSU KAISEN"));
        var s3 = result.Matches.Where(m => m.Video.Contains("Season3", StringComparison.Ordinal)).ToList();
        Assert.Equal(6 * 12 + 3 * 12, s3.Count); // six dubs, three subtitle folders
        Assert.All(s3, m => Assert.Equal(Path.GetFileNameWithoutExtension(m.Video), Path.GetFileNameWithoutExtension(m.Track)));
        Assert.Contains(s3, m => m.FolderChain.SequenceEqual(["RUS Subs", "Crunchyroll", "Надписи"]));
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void FindsTracksInSiblingFoldersOfTheVideoFolder()
    {
        // K-ON!: specials live in "[Henshin] K-ON! (BDrip)/Special", their subtitles in ".../Rus Sub by Primera Cuadrilla".
        var result = StemMatcher.Match(Corpus.Under(Corpus.Series + "/K-ON!"));
        var sp = result.Matches.Where(m => m.Track.Contains("_sp_", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, sp.Count);
        Assert.All(sp, m => Assert.Contains("/Special/", m.Video, StringComparison.Ordinal));
        Assert.All(sp, m => Assert.Equal(["Rus Sub by Primera Cuadrilla"], m.FolderChain));
    }

    [Fact]
    public void LeavesTracksWithoutAVideoUnmatchedInsteadOfGuessing()
    {
        var result = StemMatcher.Match(Corpus.Under(Corpus.Series + "/K-ON!"));
        Assert.Equal(
            ["[AcDie] K-On!! [14] [BDRip, 720p, AAC].ass", "[Winter] K-On! 14 [BDrip 1280x720 x264 Vorbis].ass"],
            result.Unmatched.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LeavesAdjacentSidecarsToTheHost()
    {
        var result = StemMatcher.Match(Corpus.Under(Corpus.Series + "/Scrubs/Season 8").Concat(Corpus.Under(Corpus.Series + "/Murderbot")));
        Assert.Empty(result.Matches);
        Assert.NotEmpty(result.HostHandled);
    }

    [Fact]
    public void EveryTrackOfTheCorpusIsAccountedForExactlyOnce()
    {
        var roots = Corpus.Releases.Select(p => string.Join('/', p.Split('/').Take(4))).Distinct().ToList();
        foreach (var root in roots)
        {
            var files = Corpus.Under(root).ToList();
            var tracks = files.Count(f => Model.MediaKinds.Of(f) is Model.FileKind.Audio or Model.FileKind.Subtitle);
            var r = StemMatcher.Match(files);
            Assert.Equal(tracks, r.Matches.Count + r.HostHandled.Count + r.Unmatched.Count);
        }
    }
}
