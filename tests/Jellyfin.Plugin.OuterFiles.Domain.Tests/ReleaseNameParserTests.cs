using Jellyfin.Plugin.OuterFiles.Domain.Organize;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class ReleaseNameParserTests
{
    [Theory]
    [InlineData("Murderbot - S01E09 - All Systems Red WEBDL-1080p", 1, 9, null, false)]
    [InlineData("Paw Patrol - S01E11-E12 - Pups on Ice + Pups and the Snow Monster WEBDL-720p", 1, 11, 12, false)]
    [InlineData("Brooklyn.Nine.Nine.S03E07.The.Mattress.1080p.WEBDL.Paramount.Comedy", 3, 7, null, false)]
    [InlineData("[S01E08] Portrait of Hiccup as a Buff Man", 1, 8, null, false)]
    [InlineData("[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [07][Ma10p_1080p][x265_flac]", null, 7, null, true)]
    [InlineData("[Henshin]_K-ON!_07_(BD_x264_1920x1080_FLAC)", null, 7, null, true)]
    [InlineData("Hunter_X_Hunter_[61_of_62]_[ru_jp]_[MifSnaiper_&_Animereactor_Ru]", null, 61, null, true)]
    [InlineData("Yakusoku no Neverland 08 [BDRip 1920x1080 x264 FLAC]", null, 8, null, true)]
    [InlineData("Yakusoku no Neverland TV-2 - 10 (WEB-DL 1920x1080 x264 AAC Rus + Jap)", 2, 10, null, false)]
    [InlineData("[BudLightSubs] Jujutsu Kaisen S3 - 11 [1080p]", 3, 11, null, false)]
    [InlineData("Jujutsu Kaisen II - 01 [BDRip 1080p HEVC 10bits FLAC]", null, 1, null, true)]
    [InlineData("[AniFilm] Akame ga Kill! - 24 END [WEBRip 720p x264] [MVO]", null, 24, null, true)]
    [InlineData("136. Индийский чай", null, 136, null, true)]
    [InlineData("Kiseijuu.Sei.no.Kakuritsu.E05", null, 5, null, true)]
    public void ReadsEpisodeNumbersFromRealNames(string name, int? season, int? episode, int? end, bool absolute)
    {
        var p = ReleaseNameParser.Parse(name);
        Assert.Equal((season, episode, end, absolute), (p.Season, p.Episode, p.EpisodeEnd, p.IsAbsolute));
    }

    [Fact]
    public void ReadsSegmentsSpecialsAndExtras()
    {
        Assert.Equal((2, "b"), (ReleaseNameParser.Parse("JN.02b.Рождение коммерсанта").Episode!.Value, ReleaseNameParser.Parse("JN.02b.Рождение коммерсанта").Segment));
        var sp = ReleaseNameParser.Parse("[Henshin]_K-ON!_sp_05_(BD_x264_1920x1080_FLAC)");
        Assert.Equal((true, 0, 5), (sp.IsSpecial, sp.Season!.Value, sp.Episode!.Value));
        Assert.Equal("NCOP", ReleaseNameParser.Parse("[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [NCOP][Ma10p_1080p][x265_flac]").Extra);
        Assert.Equal("NCOP01", ReleaseNameParser.Parse("[Henshin]_K-ON!_no-credit_opening_01_(BD_x264_1920x1080_FLAC)").Extra);
        Assert.Equal("MENU", ReleaseNameParser.Parse("Yakusoku no Neverland (Vol.01) (Menu) [BDRip 1920x1080 x264 FLAC]").Extra);
        Assert.Equal(1, ReleaseNameParser.Parse("K-ON! - S00E01 - URA-ON! - Yui's Curiosity Series Bluray-1080p").Episode);
    }

    [Theory]
    [InlineData("Season 2", 2)]
    [InlineData("Сезон 3 (BDRip 1080)", 3)]
    [InlineData("Jujutsu.Kaisen.Season3.WEB-DL.1080p", 3)]
    [InlineData("Jujutsu Kaisen S01 AniPlague", 1)]
    [InlineData("Jujutsu Kaisen II", 2)]
    [InlineData("Yakusoku no Neverland [TV-2] [2021] [WEB-DL] [1080p] [RUS + JAP]", 2)]
    [InlineData("Specials", 0)]
    [InlineData("SPs", 0)]
    [InlineData("[Henshin] K-ON! (BDrip)", null)]
    [InlineData("Brooklyn Nine-Nine Season 4 1080p  Paramount Comedy", 4)]
    public void ReadsSeasonsFromFolders(string folder, int? season) => Assert.Equal(season, ReleaseNameParser.SeasonOfFolder(folder));

    [Theory]
    [InlineData("Some.Movie.2010.1080p.BluRay.x264-GRP", "Some Movie")]
    [InlineData("Blade.Runner.2049.2017.2160p.UHD.BluRay.x265", "Blade Runner 2049")]
    [InlineData("2012.2009.1080p.BluRay", "2012")]
    [InlineData("[SubsPlease] Frieren (1080p)", "Frieren")]
    [InlineData("Movie_Title_BDRip_1080p", "Movie Title")]
    [InlineData("Already Clean Title", "Already Clean Title")]
    public void GuessesTitlesFromReleaseNames(string name, string expected) =>
        Assert.Equal(expected, ReleaseNameParser.GuessTitle(name));
}
