using Jellyfin.Plugin.OuterFiles.Domain.Matching;
using Jellyfin.Plugin.OuterFiles.Domain.Model;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

/// <summary>
/// Opt-in: OUTER_FILES_CORPUS=/path/listing.txt (one absolute path per line, or the TSV produced by
/// `find -printf '%y\t%n\t%s\t%TY-%Tm-%Td\t%p\t%l\n'`) and OUTER_FILES_CORPUS_DEPTH = number of path
/// segments of a release root (4 for /mnt/media/series/Show). Not committed: a full listing is private.
/// </summary>
public sealed class FullLibraryCorpusTests
{
    [Fact]
    public void EveryTrackOfTheLibraryIsAccountedForAndDescribed()
    {
        var file = Environment.GetEnvironmentVariable("OUTER_FILES_CORPUS");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return;
        }

        var depth = int.TryParse(Environment.GetEnvironmentVariable("OUTER_FILES_CORPUS_DEPTH"), out var d) ? d : 4;
        var paths = File.ReadLines(file)
            .Select(l => l.Contains('\t') ? (l.StartsWith("f\t", StringComparison.Ordinal) ? l.Split('\t')[4] : null) : l)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        int tracks = 0, outer = 0, unmatched = 0, noLanguage = 0;
        foreach (var release in paths.GroupBy(p => string.Join('/', p.Split('/').Take(depth + 1))))
        {
            var r = StemMatcher.Match(release);
            var t = release.Count(f => MediaKinds.Of(f) is FileKind.Audio or FileKind.Subtitle);
            Assert.Equal(t, r.Matches.Count + r.HostHandled.Count + r.Unmatched.Count);
            tracks += t;
            outer += r.Matches.Count;
            unmatched += r.Unmatched.Count;
            noLanguage += r.Matches.Count(m => TrackDescriber.Describe(m).Language is null);
        }

        Console.WriteLine($"corpus: {tracks} tracks, {outer} outer, {unmatched} unmatched, {noLanguage} outer without language from names");
    }
}
