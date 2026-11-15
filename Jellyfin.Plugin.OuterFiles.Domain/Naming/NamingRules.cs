namespace Jellyfin.Plugin.OuterFiles.Domain.Naming;

/// <summary>
/// An episode expression in Jellyfin's format (regex with <c>seriesname</c>/<c>seasonnumber</c>/<c>epnumber</c>
/// groups when named).
/// </summary>
/// <param name="Expression">Regular expression.</param>
/// <param name="Name">Short description for the status page.</param>
public sealed record EpisodeRule(string Expression, string Name);

/// <summary>
/// A path with the season/episode Jellyfin must read once the rules are installed.
/// </summary>
/// <param name="Path">Full path as the server sees it.</param>
/// <param name="Season">Expected season, or null.</param>
/// <param name="Episode">Expected episode, or null when the name must stay unparsed.</param>
public sealed record NamingCase(string Path, int? Season, int? Episode);

/// <summary>
/// The changes the plugin makes to Jellyfin's episode recognition (docs/ARCHITECTURE.md, section 6.4).
/// All of them were measured against every video path of a real library (4365 files): only the intended
/// series change, everything else parses exactly as before.
/// <list type="number">
/// <item>Core expressions that know nothing about path separators are confined to the file name. Two of
/// them caused real damage: the anime expression read <c>[2023]</c> from a folder name as the episode
/// number, and <c>([0-9]+)-([0-9]+)</c> read <c>1-3</c> from <c>(Сезоны 1-3)</c> as S1E3 for every file.</item>
/// <item><see cref="Prepend"/> rules run first because a core expression would otherwise produce a wrong
/// number (<c>Show TV-2 - 10</c> → E2).</item>
/// <item><see cref="Append"/> rules run last and only catch names no core expression parses.</item>
/// </list>
/// </summary>
public static class NamingRules
{
    /// <summary>Gets rules that run before Jellyfin's own expressions.</summary>
    public static IReadOnlyList<EpisodeRule> Prepend { get; } =
    [
        new(@"[\\/](?<seriesname>[^\\/]+?)[ ._]+TV-?(?<seasonnumber>[0-9]{1,2})[ ._]*-[ ._]*(?<epnumber>[0-9]{1,3})(?![0-9])[^\\/]*$", "Show TV-2 - 10"),
    ];

    /// <summary>Gets rules that run after Jellyfin's own expressions.</summary>
    public static IReadOnlyList<EpisodeRule> Append { get; } =
    [
        new(@"[\\/](?:\[[^\]\\/]+\][ _.]*)?(?<seriesname>[^\\/\[\]]+?)(?<![ _.-](?:sp|op|ed|nc|ova|oad|opening|ending|menu|preview|pv|cm))_(?<epnumber>[0-9]{1,3})(?:v[0-9])?_[\[(][^\\/]*$", "[Group]_Show_07_(tags)"),
        new(@"[\\/](?<seriesname>[^\\/\[\]]+?)[ _.]*\[(?<epnumber>[0-9]{1,4})[ _]of[ _][0-9]{1,4}\][^\\/]*$", "Show_[61_of_62]"),
        new(@"[\\/](?<seriesname>[^\\/\[\]()]+?)[ _.]+(?!(?:19|20)[0-9]{2}(?![0-9]))(?<epnumber>[0-9]{1,3})(?:v[0-9])?[ _.]*[\[(][^\\/]*$", "Show 08 [tags]"),
    ];

    /// <summary>
    /// Gets paths the installed rules must parse as stated. The self-test refuses to install the rules when
    /// any of them fails on the running server.
    /// </summary>
    public static IReadOnlyList<NamingCase> SelfTest { get; } =
    [
        // Fixed by the plugin.
        new("/tv/Jitsu wa Ore, Saikyou deshita [2023][BDRip][1080p]/[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [07][Ma10p_1080p][x265_flac].mkv", null, 7),
        new("/tv/K-ON!/[Henshin] K-ON! (BDrip)/[Henshin]_K-ON!_07_(BD_x264_1920x1080_FLAC).mkv", null, 7),
        new("/tv/Hunter X Hunter/Hunter_X_Hunter_[61_of_62]_[ru_jp]_[MifSnaiper_&_Animereactor_Ru].mkv", null, 61),
        new("/tv/Yakusoku no Neverland/Yakusoku no Neverland [BDRip] [1080p]/Yakusoku no Neverland 08 [BDRip 1920x1080 x264 FLAC].mkv", null, 8),
        new("/tv/Yakusoku no Neverland/Yakusoku no Neverland [TV-2] [2021] [WEB-DL] [1080p] [RUS + JAP]/Yakusoku no Neverland TV-2 - 10 (WEB-DL 1920x1080 x264 AAC Rus + Jap).mkv", 2, 10),
        new("/tv/Show (Сезоны 1-3)/JN.16.Title.avi", null, null),

        // Must stay exactly as Jellyfin parses them.
        new("/tv/Murderbot/Season 1/Murderbot - S01E09 - All Systems Red WEBDL-1080p.mkv", 1, 9),
        new("/tv/Bluey (2018)/Bluey (2018) - S03E05 - Omelette WEBDL-720p.mp4", 3, 5),
        new("/tv/Paw Patrol/Season 1/Paw Patrol - S01E11-E12 - Pups on Ice + Pups and the Snow Monster WEBDL-720p.mp4", 1, 11),
        new("/tv/Brooklyn Nine-Nine/Brooklyn Nine-Nine Season 3 1080p  Paramount Comedy/Brooklyn.Nine.Nine.S03E07.The.Mattress.1080p.WEBDL.Paramount.Comedy.mkv", 3, 7),
        new("/tv/DreamWorks Dragons/[S01] Dragons - Riders of Berk (2012-2013) WEB-DL 1080p [Rus Dub, Eng]/[S01E08] Portrait of Hiccup as a Buff Man.mkv", 1, 8),
        new("/tv/Kiseijuu.Sei.no.Kakuritsu/Kiseijuu.Sei.no.Kakuritsu.E05.mkv", null, 5),
        new("/tv/[NC-Raws] Chainsaw Man [WEBRip 1080p x264 E-AC-3]/[NC-Raws] Chainsaw Man - 03 [WEBRip 1080p x264 E-AC-3].mkv", null, 3),
        new("/tv/JUJUTSU KAISEN/Jujutsu Kaisen II/Jujutsu Kaisen II - 01 [BDRip 1080p HEVC 10bits FLAC].mkv", null, 1),
        new("/tv/K-ON!/[Henshin] K-ON! (BDrip)/Special/[Henshin]_K-ON!_sp_01_(BD_x264_1920x1080_FLAC).mkv", null, null),
        new("/tv/Is It Wrong to Try to Pick Up Girls in a Dungeon!/Specials/Is It Wrong to Try to Pick Up Girls in a Dungeon! - S00E01 - OVA - Is It Wrong to Expect a Hot Spring in a Dungeon Bluray-1080p.mp4", 0, 1),
        new("/tv/Show/Season 2/05.mkv", null, 5), // season comes from the season folder, not from the path parse
        new("/tv/Show/Show 2024-03-15.mkv", null, null),
    ];

    /// <summary>
    /// Tells whether an expression refers to path separators (and therefore already controls which path
    /// segment it reads).
    /// </summary>
    /// <param name="expression">Regex.</param>
    /// <returns>True when path aware.</returns>
    public static bool IsPathAware(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression.Contains(@"\/", StringComparison.Ordinal) || expression.Contains(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Wraps an expression so it only matches inside the last path segment. Lookarounds add no groups, so
    /// numbered and named groups keep their meaning.
    /// </summary>
    /// <param name="expression">Regex.</param>
    /// <returns>The confined regex.</returns>
    public static string ConfineToFileName(string expression) => @"(?<=(?:^|[\\/])[^\\/]*)(?:" + expression + @")(?=[^\\/]*$)";
}
