namespace Jellyfin.Plugin.OuterFiles.Domain.Tracks;

/// <summary>
/// Words found in release folder and file names, taken from real releases
/// (<c>RU Sound [Anilibria]</c>, <c>RUS Subs/Crunchyroll/Надписи</c>, <c>Rus Sub by A-D Team</c>,
/// <c>Sounds/Wakanim</c>, <c>EN Sound/signs</c>, <c>.ru_signs_CR</c>).
/// </summary>
public static class FolderVocabulary
{
    /// <summary>Gets language words mapped to ISO 639-2 codes.</summary>
    public static IReadOnlyDictionary<string, string> Languages { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "rus", ["rus"] = "rus", ["russian"] = "rus", ["рус"] = "rus", ["русские"] = "rus", ["русская"] = "rus", ["русский"] = "rus",
        ["en"] = "eng", ["eng"] = "eng", ["english"] = "eng", ["англ"] = "eng", ["английские"] = "eng",
        ["jp"] = "jpn", ["jpn"] = "jpn", ["ja"] = "jpn", ["jap"] = "jpn", ["japanese"] = "jpn", ["японские"] = "jpn", ["японская"] = "jpn",
        ["ua"] = "ukr", ["ukr"] = "ukr", ["ukrainian"] = "ukr", ["укр"] = "ukr",
        ["kz"] = "kaz", ["kaz"] = "kaz",
        ["zh"] = "zho", ["chi"] = "zho", ["chs"] = "zho", ["cht"] = "zho", ["chinese"] = "zho",
        ["ko"] = "kor", ["kor"] = "kor", ["korean"] = "kor",
        ["fr"] = "fre", ["fre"] = "fre", ["fra"] = "fre", ["french"] = "fre",
        ["de"] = "ger", ["ger"] = "ger", ["deu"] = "ger", ["german"] = "ger",
        ["es"] = "spa", ["spa"] = "spa", ["spanish"] = "spa",
        ["it"] = "ita", ["ita"] = "ita", ["italian"] = "ita",
        ["pt"] = "por", ["por"] = "por", ["portuguese"] = "por",
    };

    /// <summary>Gets words that only say "this is audio" or "these are subtitles".</summary>
    public static IReadOnlySet<string> TypeWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sound", "sounds", "audio", "audios", "dub", "dubs", "voice", "voices", "track", "tracks",
        "sub", "subs", "subtitle", "subtitles", "sbt",
        "озвучка", "озвучки", "звук", "звуковые", "дорожки", "аудио", "субтитры", "сабы",
    };

    /// <summary>Gets words that mark signs-only (forced) subtitles.</summary>
    public static IReadOnlySet<string> ForcedWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "signs", "sign", "forced", "надписи", "надпись", "inscriptions",
    };

    /// <summary>Gets words that carry no information about a track (container folders, glue words).</summary>
    public static IReadOnlySet<string> NoiseWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "by", "from", "от", "bonus", "extra", "extras", "other", "misc", "full", "and", "&", "+",
    };
}
