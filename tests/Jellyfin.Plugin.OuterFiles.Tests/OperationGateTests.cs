using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Services;

namespace Jellyfin.Plugin.OuterFiles.Tests;

public sealed class OperationGateTests
{
    [Fact]
    public async Task SecondOperationIsRefusedWhileTheFirstRuns()
    {
        using var gate = new OperationGate();
        var release = new TaskCompletionSource();
        var first = gate.TryRunAsync("links", async _ => { await release.Task; return 1; }, CancellationToken.None);

        Assert.Equal("links", gate.Current);
        var second = await gate.TryRunAsync("organize", _ => Task.FromResult(2), CancellationToken.None);
        Assert.False(second.Ran);

        release.SetResult();
        Assert.Equal((true, 1), await first);
        Assert.Null(gate.Current);
        Assert.True((await gate.TryRunAsync("organize", _ => Task.FromResult(2), CancellationToken.None)).Ran);
    }
}
