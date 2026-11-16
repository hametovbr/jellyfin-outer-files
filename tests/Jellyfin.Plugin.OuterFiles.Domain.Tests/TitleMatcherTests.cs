using Jellyfin.Plugin.OuterFiles.Domain.Organize;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class TitleMatcherTests
{
    [Theory]
    [InlineData("JN.02a.Обычный мальчик", "Обычный мальчик", null)]
    [InlineData("JN.23.Операция Спасение Джета Фьюжена (Часть 1) ", "Операция Спасение Джета Фьюжена", 1)]
    [InlineData("K-ON! - S01E13 - Winter Days! Bluray-1080p", "Winter Days!", null)]
    [InlineData("Hetalia - Axis Powers - S01E40 - Everyone Wants America 2 DVD", "Everyone Wants America 2", null)]
    [InlineData("JUJUTSU KAISEN - S01E14 - Kyoto Sister School Exchange Event - Group Battle 0 - WEBDL-1080p Proper", "Kyoto Sister School Exchange Event - Group Battle 0", null)]
    [InlineData("Show.S02E05.The.Title.1080p.WEB-DL", "The Title", null)]
    public void ReadsTheEpisodeTitleFromTheName(string stem, string title, int? part)
    {
        Assert.Equal((title, part), TitleMatcher.TitleOf(stem));
    }

    [Theory]
    [InlineData("[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [04][Ma10p_1080p][x265_flac]")]
    [InlineData("[Henshin]_K-ON!_14_(BD_x264_1920x1080_FLAC)")]
    [InlineData("Show 08 [BDRip 1920x1080 x264 FLAC]")]
    [InlineData("Yakusoku no Neverland TV-2 - 10 (WEB-DL 1920x1080 x264 AAC Rus + Jap)")]
    public void NamesWithoutATitleGiveNothing(string stem)
    {
        Assert.Null(TitleMatcher.TitleOf(stem));
    }

    [Fact]
    public void MatchesSegmentsSpecialsAndPartsOfJimmyNeutron()
    {
        CatalogEpisode[] catalog =
        [
            new(1, 1, "Когда атакуют штаны"), new(1, 1, "When Pants Attack"),
            new(1, 2, "Обычный мальчик"), new(1, 2, "Normal Boy"),
            new(1, 3, "Рождение коммерсанта"), new(1, 3, "Birth of a Salesman"),
            new(1, 14, "Фантом Рэтроленда"),
            new(2, 9, "Любовный напиток № 976J"),
            new(3, 8, "Борьба кузенов"),
            new(0, 4, "Операция «Спасение Джета Фьюжена»"),
        ];
        (string, string)[] files =
        [
            ("/s/JN.01.Когда атакуют штаны.avi", "JN.01.Когда атакуют штаны"),
            ("/s/JN.02a.Обычный мальчик.avi", "JN.02a.Обычный мальчик"),
            ("/s/JN.02b.Рождение коммерсанта.avi", "JN.02b.Рождение коммерсанта"),
            ("/s/JN.08a.Фантом Ретролэнда.avi", "JN.08a.Фантом Ретролэнда"),
            ("/s/JN.29.Любовный напиток №926J.avi", "JN.29.Любовный напиток №926J"),
            ("/s/JN.45a.Борьба кузенов.net.avi", "JN.45a.Борьба кузенов.net"),
            ("/s/JN.23.Операция Спасение Джета Фьюжена (Часть 1) .avi", "JN.23.Операция Спасение Джета Фьюжена (Часть 1) "),
            ("/s/JN.24.Операция Спасение Джета Фьюжена (Часть 2) .avi", "JN.24.Операция Спасение Джета Фьюжена (Часть 2) "),
            ("/s/JN.59.Лига злодеев.avi", "JN.59.Лига злодеев"),
        ];

        var m = TitleMatcher.Match(files, catalog).ToDictionary(x => Path.GetFileName(x.Video), x => x.Match is null ? null : $"S{x.Match.Season}E{x.Match.Episode}/{x.Part}");

        Assert.Equal("S1E1/", m["JN.01.Когда атакуют штаны.avi"]);
        Assert.Equal("S1E2/", m["JN.02a.Обычный мальчик.avi"]);
        Assert.Equal("S1E3/", m["JN.02b.Рождение коммерсанта.avi"]);
        Assert.Equal("S1E14/", m["JN.08a.Фантом Ретролэнда.avi"]);
        Assert.Equal("S2E9/", m["JN.29.Любовный напиток №926J.avi"]);
        Assert.Equal("S3E8/", m["JN.45a.Борьба кузенов.net.avi"]);
        Assert.Equal("S0E4/1", m["JN.23.Операция Спасение Джета Фьюжена (Часть 1) .avi"]);
        Assert.Equal("S0E4/2", m["JN.24.Операция Спасение Джета Фьюжена (Часть 2) .avi"]);
        Assert.Null(m["JN.59.Лига злодеев.avi"]);
    }

    [Fact]
    public void ProposesRowsAndKeepsFilesWithoutTitles()
    {
        const string root = "/tv/K-ON!";
        string[] files = [root + "/Season 1/K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv", root + "/[Henshin] K-ON!/[Henshin]_K-ON!_02_(BD_x264_1920x1080_FLAC).mkv", root + "/Season 1/K-ON! - S01E05 - Unknown Thing Bluray-1080p.mkv"];
        var inv = Inventory.Build(root, files);
        var matches = TitleMatcher.Match(inv.Videos.Select(v => (v.Path, Path.GetFileNameWithoutExtension(v.Path))), [new(0, 1, "Winter Days!"), new(1, 2, "Instruments!")]);

        var rows = EpisodeMapper.ProposeByTitles(inv, matches, new Dictionary<string, int>()).ToDictionary(r => Path.GetFileName(r.Video));

        Assert.Equal((RowAction.Special, 0, 1), (rows["K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv"].Action, rows["K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv"].Season, rows["K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv"].Episode));
        Assert.Equal((RowAction.Episode, 1, 2), (rows["[Henshin]_K-ON!_02_(BD_x264_1920x1080_FLAC).mkv"].Action, rows["[Henshin]_K-ON!_02_(BD_x264_1920x1080_FLAC).mkv"].Season, rows["[Henshin]_K-ON!_02_(BD_x264_1920x1080_FLAC).mkv"].Episode));
        Assert.Equal(RowAction.Skip, rows["K-ON! - S01E05 - Unknown Thing Bluray-1080p.mkv"].Action);
    }

    [Fact]
    public void UntitledCopiesFollowTheTitledCopyWithTheSameNumber()
    {
        const string root = "/tv/K-ON!";
        string[] files =
        [
            root + "/Specials/K-ON! - S00E01 - URA-ON! - Yui's Curiosity Series Bluray-1080p.mkv",
            root + "/[Henshin] K-ON! (BDrip)/Special/[Henshin]_K-ON!_sp_01_(BD_x264_1920x1080_FLAC).mkv",
            root + "/[Henshin] K-ON! (BDrip)/Rus Sub/[Henshin]_K-ON!_sp_01_(BD_x264_1920x1080_FLAC).ass",
            root + "/Season 1/K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv",
            root + "/[Henshin] K-ON! (BDrip)/[Henshin]_K-ON!_13_(BD_x264_1920x1080_FLAC).mkv",
            root + "/[Henshin] K-ON! (BDrip)/[Henshin]_K-ON!_14_(BD_x264_1920x1080_FLAC).mkv",
        ];
        var inv = Inventory.Build(root, files);
        var matches = TitleMatcher.Match(inv.Videos.Select(v => (v.Path, Path.GetFileNameWithoutExtension(v.Path))), [new(0, 1, "Winter Days!"), new(0, 2, "URA-ON! - Yui's Curiosity Series")]);

        var rows = EpisodeMapper.ProposeByTitles(inv, matches, new Dictionary<string, int>()).ToDictionary(r => Path.GetFileName(r.Video), r => (r.Action, r.Season, r.Episode));

        Assert.Equal((RowAction.Special, 0, 2), rows["[Henshin]_K-ON!_sp_01_(BD_x264_1920x1080_FLAC).mkv"]);
        Assert.Equal(RowAction.Skip, rows["K-ON! - S00E01 - URA-ON! - Yui's Curiosity Series Bluray-1080p.mkv"].Action);
        Assert.Equal((RowAction.Special, 0, 1), rows["K-ON! - S01E13 - Winter Days! Bluray-1080p.mkv"]);
        Assert.Equal((RowAction.Skip, 0, 1), rows["[Henshin]_K-ON!_13_(BD_x264_1920x1080_FLAC).mkv"]);
        Assert.Equal((RowAction.Episode, 1, 14), rows["[Henshin]_K-ON!_14_(BD_x264_1920x1080_FLAC).mkv"]);
    }
}
