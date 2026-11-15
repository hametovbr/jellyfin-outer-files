using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class TrackDescriberTests
{
    private static TrackDescriptor D(string chain, string suffix = "") =>
        TrackDescriber.Describe(new TrackMatch("/v.mkv", "/t.mka", FileKind.Audio, suffix, chain.Split('/', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    // JUJUTSU KAISEN S3
    [InlineData("RUS Sound/ForceMedia", "", "rus", "ForceMedia", false)]
    [InlineData("RUS Sound/Дубляжная", "", "rus", "Дубляжная", false)]
    [InlineData("RUS Subs/Crunchyroll/Надписи", "", "rus", "Crunchyroll", true)]
    // VCB-Studio (Jitsu wa Ore)
    [InlineData("RU Sound [Anilibria]", ".ru_Anilibria", "rus", "Anilibria", false)]
    [InlineData("RU Sound [Anilibria]/надписи", ".ru_signs_CR", "rus", "CR", true)]
    [InlineData("EN Sound/signs", ".en_signs_CR", "eng", "CR", true)]
    [InlineData("EN Subs", ".en_CR", "eng", "CR", false)]
    [InlineData("JP Subs", ".jp", "jpn", null, false)]
    // K-ON! [Henshin]
    [InlineData("Rus Sound by Ancord", "", "rus", "Ancord", false)]
    [InlineData("Rus Sub by A-D Team", "", "rus", "A-D Team", false)]
    // Shinchou Yuusha: folders carry no language
    [InlineData("Sounds/AniLibria", "", null, "AniLibria", false)]
    [InlineData("Subtitles/YakuSub Studio", "", null, "YakuSub Studio", false)]
    // Yakusoku no Neverland
    [InlineData("Rus Sound", "", "rus", null, false)]
    [InlineData("Rus Subs/Bazaar Zetsubou", "", "rus", "Bazaar Zetsubou", false)]
    [InlineData("Bonus/Audio Commentary", ".Audio Commentary", null, "Commentary", false)]
    // Extraction-tool suffixes next to the video
    [InlineData("", ".rus.19", "rus", null, false)]
    [InlineData("", ".subrip.3", null, null, false)]
    [InlineData("", ".hdmv_pgs_subtitle.22", null, null, false)]
    public void DescribesRealReleaseLayouts(string chain, string suffix, string? language, string? studio, bool forced)
    {
        var d = D(chain, suffix);
        Assert.Equal(language, d.Language);
        Assert.Equal(studio, d.Studio);
        Assert.Equal(forced, d.Forced);
    }

    [Fact]
    public void SuffixBeatsFolderAndSdhIsRecognised()
    {
        var d = D("RU Subs", ".eng.SDH");
        Assert.Equal(("eng", "suffix", true), (d.Language, d.LanguageSource, d.HearingImpaired));
    }

    [Fact]
    public void FallbackOnlyFillsAnUnknownLanguage()
    {
        Assert.Equal("rus", D("Sounds/AniLibria").WithFallbackLanguage("rus", "default").Language);
        Assert.Equal("eng", D("EN Sound").WithFallbackLanguage("rus", "default").Language);
    }
}
