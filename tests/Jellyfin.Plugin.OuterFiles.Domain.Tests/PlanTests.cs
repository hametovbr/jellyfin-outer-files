using System.Text.Json;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class PlanTests
{
    private static readonly Operation[] Sample =
    [
        new CreateDirectory("/lib/Show (2023)", "series"),
        new MoveFile("/lib/a.mkv", "/lib/Show (2023)/a.mkv", "episode"),
        new CreateSymlink("/meta/a.rus.mka", "/lib/x.mka", "link"),
    ];

    [Fact]
    public void HashIsStableAndIgnoresTitle()
    {
        Assert.Equal(new Plan("one", Sample).Hash, new Plan("two", Sample.ToList()).Hash);
        Assert.NotEqual(new Plan("one", Sample).Hash, new Plan("one", Sample.Reverse().ToList()).Hash);
    }

    [Fact]
    public void UndoPlanInvertsInReverseOrder()
    {
        var undo = Plan.UndoOf("undo", Sample);
        Assert.IsType<RemoveSymlink>(undo.Operations[0]);
        Assert.Equal(new MoveFile("/lib/Show (2023)/a.mkv", "/lib/a.mkv", "undo: episode"), undo.Operations[1]);
        Assert.IsType<RemoveEmptyDirectory>(undo.Operations[2]);
        Assert.Equal(Sample, Plan.UndoOf("x", undo.Operations).Operations.Select(StripUndo));
    }

    [Fact]
    public void OperationsRoundTripThroughJson()
    {
        var json = JsonSerializer.Serialize<IReadOnlyList<Operation>>(Sample, PlanJson.Options);
        Assert.Contains("\"op\":\"move\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<IReadOnlyList<Operation>>(json, PlanJson.Options)!;
        Assert.Equal(Sample, back);
    }

    private static Operation StripUndo(Operation op) => op switch
    {
        CreateDirectory d => d with { Reason = d.Reason.Replace("undo: ", string.Empty, StringComparison.Ordinal) },
        MoveFile m => m with { Reason = m.Reason.Replace("undo: ", string.Empty, StringComparison.Ordinal) },
        CreateSymlink l => l with { Reason = l.Reason.Replace("undo: ", string.Empty, StringComparison.Ordinal) },
        _ => op,
    };
}
