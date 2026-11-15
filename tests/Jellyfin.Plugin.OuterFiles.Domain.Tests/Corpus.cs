namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

/// <summary>
/// Release layouts from a real library (paths only, as Jellyfin sees them inside its container).
/// The committed fixture covers the difficult releases; set OUTER_FILES_CORPUS to a full listing
/// (one path per line) to run the corpus tests against a whole library locally.
/// </summary>
internal static class Corpus
{
    public static IReadOnlyList<string> Releases { get; } = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "releases.txt"))
        .Where(l => l.Length > 0).ToList();

    public static IEnumerable<string> Under(string root) => Releases.Where(p => p.StartsWith(root + "/", StringComparison.Ordinal));

    public const string Series = "/mnt/series2";
}
