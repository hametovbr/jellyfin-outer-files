using Jellyfin.Plugin.OuterFiles.Domain.Plans;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class PlanValidatorTests
{
    private static readonly string[] Roots = ["/lib", "/meta/"];

    private static IEnumerable<string> Codes(params Operation[] ops) =>
        PlanValidator.Validate(new Plan("t", ops), Roots).Select(c => c.Code);

    [Fact]
    public void AcceptsAPlanInsideTheRoots() =>
        Assert.Empty(Codes(new MoveFile("/lib/a.mkv", "/lib/b/a.mkv", "x"), new CreateSymlink("/meta/l.srt", "/lib/b/a.srt", "x")));

    [Fact]
    public void RejectsPathsOutsideTheRootsIncludingLookAlikePrefixes()
    {
        Assert.Contains(PlanValidator.OutsideRoots, Codes(new MoveFile("/lib/a.mkv", "/library2/a.mkv", "x")));
        Assert.Contains(PlanValidator.OutsideRoots, Codes(new CreateDirectory("/libx", "x")));
    }

    [Fact]
    public void RejectsDuplicateTargetsTraversalRelativeAndNoOps()
    {
        Assert.Contains(PlanValidator.DuplicateTarget, Codes(new MoveFile("/lib/a", "/lib/c", "x"), new MoveFile("/lib/b", "/lib/c", "x")));
        Assert.Contains(PlanValidator.Traversal, Codes(new MoveFile("/lib/a", "/lib/../etc/a", "x")));
        Assert.Contains(PlanValidator.NotAbsolute, Codes(new MoveFile("lib/a", "/lib/b", "x")));
        Assert.Contains(PlanValidator.NoOpMove, Codes(new MoveFile("/lib/a", "/lib/a", "x")));
    }

    [Fact]
    public void LinkTargetsMayPointAnywhereInsideTheLibrary() =>
        Assert.Empty(Codes(new CreateSymlink("/meta/x.srt", "/elsewhere/x.srt", "target is only read")));
}
