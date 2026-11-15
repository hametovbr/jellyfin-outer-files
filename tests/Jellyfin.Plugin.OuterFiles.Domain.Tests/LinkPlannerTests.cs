using Jellyfin.Plugin.OuterFiles.Domain.Links;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class LinkPlannerTests
{
    private const string Stem = "[VCB-Studio] Jitsu wa Ore, Saikyou Deshita [01][Ma10p_1080p][x265_flac]";

    [Fact]
    public void NamesLinksSoJellyfinReadsLanguageLastAndTitleAndFlags()
    {
        Assert.Equal(Stem + ".Anilibria.rus.mka", LinkPlanner.LinkName(Stem, new TrackDescriptor("rus", "suffix", "Anilibria", false, false), ".mka"));
        Assert.Equal(Stem + ".CR.forced.eng.ass", LinkPlanner.LinkName(Stem, new TrackDescriptor("eng", "suffix", "CR", true, false), ".ASS"));
        Assert.Equal(Stem + ".und.ass", LinkPlanner.LinkName(Stem, new TrackDescriptor(null, "unknown", null, false, false), ".ass"));
        Assert.Equal(Stem + ".sdh.eng.srt", LinkPlanner.LinkName(Stem, new TrackDescriptor("eng", "suffix", null, false, true), ".srt"));
    }

    [Fact]
    public void RemovesWordsJellyfinWouldReadAsFlagsAndDotsFromTitles()
    {
        var name = LinkPlanner.LinkName("v", new TrackDescriptor("rus", "folder", "Default.Studio HI Forced-Team", false, false), ".mka");
        Assert.Equal("v.Studio.rus.mka", name);
    }

    [Fact]
    public void DisambiguatesEqualNames()
    {
        var d = new TrackDescriptor("rus", "folder", null, false, false);
        var links = LinkPlanner.Desire("v", [("/a/x.mka", d), ("/b/x.mka", d)]);
        Assert.Equal(["v.rus.mka", "v.#2.rus.mka"], links.Select(l => l.Name));
    }

    [Fact]
    public void ReconcilesCreatesRemovesAndIsIdempotent()
    {
        var desired = new[] { new DesiredLink("v.A.rus.mka", "/lib/A/v.mka", "r"), new DesiredLink("v.B.rus.mka", "/lib/B/v.mka", "r") };
        var fresh = new VideoLinkState("/meta/ab/abc", ["/meta/ab", "/meta/ab/abc"], desired, []);
        var (plan, changed) = LinkPlanner.Plan([fresh]);
        Assert.Equal(["mkdir", "mkdir", "link", "link"], plan.Operations.Select(Kind));
        Assert.Equal(["/meta/ab/abc"], changed);

        var done = fresh with { MissingFolders = [], Existing = desired.Select(d => new ExistingLink(d.Name, d.Target)).ToList() };
        Assert.True(LinkPlanner.Plan([done]).Plan.IsEmpty);

        var moved = done with { Desired = [desired[0], desired[1] with { Target = "/lib/B2/v.mka" }] };
        Assert.Equal(["unlink", "link"], LinkPlanner.Plan([moved]).Plan.Operations.Select(Kind));

        var gone = done with { Desired = [] };
        Assert.Equal(["unlink", "unlink"], LinkPlanner.Plan([gone]).Plan.Operations.Select(Kind));
    }

    private static string Kind(Operation op) => op switch
    {
        CreateDirectory => "mkdir",
        CreateSymlink => "link",
        RemoveSymlink => "unlink",
        _ => op.GetType().Name,
    };
}
