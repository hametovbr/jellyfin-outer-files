using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;
using Jellyfin.Plugin.OuterFiles.IO;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

public sealed class PlanExecutorTests
{
    private static (PlanExecutor Executor, JournalStore Journals) Create(TempTree tree)
    {
        var journals = new JournalStore(tree.P("_journals"));
        return (new PlanExecutor(journals, NullLogger<PlanExecutor>.Instance), journals);
    }

    [Fact]
    public void AppliesAndUndoesToTheIdenticalTree()
    {
        using var tree = new TempTree();
        var video = tree.File("lib/Show [2023]/[Grp] Show [01].mkv", "video");
        var audio = tree.File("lib/Show [2023]/RU Sound/[Grp] Show [01].mka", "audio");
        Directory.CreateDirectory(tree.P("meta/ab"));
        var before = tree.Snapshot("_journals");
        var (executor, journals) = Create(tree);

        var plan = new Plan("organize", [
            new CreateDirectory(tree.P("lib/Show (2023)"), "series folder"),
            new CreateDirectory(tree.P("lib/Show (2023)/Season 01"), "season folder"),
            new MoveFile(video, tree.P("lib/Show (2023)/Season 01/Show (2023) - S01E01.mkv"), "episode"),
            new MoveFile(audio, tree.P("lib/Show (2023)/Season 01/Show (2023) - S01E01.rus.mka"), "track"),
            new RemoveEmptyDirectory(tree.P("lib/Show [2023]/RU Sound"), "emptied"),
            new RemoveEmptyDirectory(tree.P("lib/Show [2023]"), "emptied"),
            new CreateSymlink(tree.P("meta/ab/x.rus.mka"), tree.P("lib/Show (2023)/Season 01/Show (2023) - S01E01.rus.mka"), "link"),
        ]);

        var result = executor.Apply(plan, "organize", [tree.P("lib"), tree.P("meta")], CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(7, result.Done);
        Assert.True(File.Exists(tree.P("lib/Show (2023)/Season 01/Show (2023) - S01E01.mkv")));
        Assert.False(Directory.Exists(tree.P("lib/Show [2023]")));
        Assert.Equal("completed", journals.Read(result.JournalId!)!.State);

        var undo = executor.Undo(result.JournalId!, [tree.P("lib"), tree.P("meta")], CancellationToken.None);
        Assert.True(undo.Succeeded, undo.Error);
        Assert.Equal(before, tree.Snapshot("_journals"));
        Assert.Equal(undo.JournalId, journals.Read(result.JournalId!)!.UndoneBy);

        var again = executor.Undo(result.JournalId!, [tree.P("lib"), tree.P("meta")], CancellationToken.None);
        Assert.False(again.Succeeded);
    }

    [Fact]
    public void StopsAtFirstFailureNeverOverwritesAndUndoesThePartialRun()
    {
        using var tree = new TempTree();
        var a = tree.File("lib/a.mkv", "a");
        var b = tree.File("lib/b.mkv", "b");
        tree.File("lib/taken.mkv", "taken");
        var before = tree.Snapshot("_journals");
        var (executor, journals) = Create(tree);

        var plan = new Plan("clash", [
            new MoveFile(a, tree.P("lib/c.mkv"), "first"),
            new MoveFile(b, tree.P("lib/taken.mkv"), "would overwrite"),
            new MoveFile(tree.P("lib/c.mkv"), tree.P("lib/d.mkv"), "never runs"),
        ]);

        var result = executor.Apply(plan, "organize", [tree.P("lib")], CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Done);
        Assert.Equal("taken", File.ReadAllText(tree.P("lib/taken.mkv")));
        Assert.Equal("b", File.ReadAllText(b));
        Assert.Equal("failed", journals.Read(result.JournalId!)!.State);

        Assert.True(executor.Undo(result.JournalId!, [tree.P("lib")], CancellationToken.None).Succeeded);
        Assert.Equal(before, tree.Snapshot("_journals"));
    }

    [Fact]
    public void UndoKeepsFoldersJellyfinWroteMetadataInto()
    {
        using var tree = new TempTree();
        var video = tree.File("lib/Show [2023]/[Grp] Show [01].mkv", "video");
        var (executor, _) = Create(tree);
        var plan = new Plan("organize", [
            new CreateDirectory(tree.P("lib/Show (2023)"), "series folder"),
            new MoveFile(video, tree.P("lib/Show (2023)/Show (2023) - S01E01.mkv"), "episode"),
            new RemoveEmptyDirectory(tree.P("lib/Show [2023]"), "emptied"),
        ]);
        var applied = executor.Apply(plan, "organize", [tree.P("lib")], CancellationToken.None);
        tree.File("lib/Show (2023)/tvshow.nfo", "written by Jellyfin");

        var undo = executor.Undo(applied.JournalId!, [tree.P("lib")], CancellationToken.None);

        Assert.True(undo.Succeeded, undo.Error);
        Assert.Equal([tree.P("lib/Show (2023)")], undo.Kept);
        Assert.Equal("video", File.ReadAllText(video));
        Assert.True(File.Exists(tree.P("lib/Show (2023)/tvshow.nfo")));
    }

    [Fact]
    public void RetriesAPartialRunWithoutTouchingWhatIsAlreadyInPlace()
    {
        using var tree = new TempTree();
        var a = tree.File("lib/R/a.mkv", "a");
        var b = tree.File("lib/R/b.mkv", "b");
        var (executor, _) = Create(tree);
        var plan = new Plan("organize", [
            new CreateDirectory(tree.P("lib/N"), "folder"),
            new MoveFile(a, tree.P("lib/N/a.mkv"), "episode"),
            new MoveFile(b, tree.P("lib/N/b.mkv"), "episode"),
        ]);
        File.Move(a, tree.P("lib/N-a.tmp"));
        Directory.CreateDirectory(tree.P("lib/N"));
        File.Move(tree.P("lib/N-a.tmp"), tree.P("lib/N/a.mkv"));

        var result = executor.Apply(plan, "organize", [tree.P("lib")], CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((1, 2), (result.Done, result.Skipped));
        Assert.Equal("b", File.ReadAllText(tree.P("lib/N/b.mkv")));
    }

    [Fact]
    public void RejectsPlansThatTouchPathsOutsideTheRoots()
    {
        using var tree = new TempTree();
        var a = tree.File("lib/a.mkv");
        var (executor, _) = Create(tree);
        var result = executor.Apply(new Plan("escape", [new MoveFile(a, tree.P("elsewhere/a.mkv"), "x")]), "organize", [tree.P("lib")], CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Conflicts, c => c.Code == PlanValidator.OutsideRoots);
        Assert.True(File.Exists(a));
        Assert.Null(result.JournalId);
    }

    [Fact]
    public void LinkStepsAreIdempotentAndNeverRemoveForeignFiles()
    {
        using var tree = new TempTree();
        var target = tree.File("lib/a.rus.mka");
        Directory.CreateDirectory(tree.P("meta"));
        var foreign = tree.File("meta/downloaded.rus.srt");
        var (executor, _) = Create(tree);
        var link = new CreateSymlink(tree.P("meta/a.rus.mka"), target, "link");

        Assert.Equal(1, executor.Apply(new Plan("links", [link]), "links", [tree.P("meta")], CancellationToken.None).Done);
        var second = executor.Apply(new Plan("links", [link]), "links", [tree.P("meta")], CancellationToken.None);
        Assert.Equal((0, 1), (second.Done, second.Skipped));

        var removeForeign = executor.Apply(new Plan("links", [new RemoveSymlink(foreign, target, "stale")]), "links", [tree.P("meta")], CancellationToken.None);
        Assert.False(removeForeign.Succeeded);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void ReadsBackAnInterruptedJournal()
    {
        using var tree = new TempTree();
        var journals = new JournalStore(tree.P("_journals"));
        var op = new CreateDirectory(tree.P("x"), "dir");
        string id;
        using (var writer = journals.Begin(new Plan("crash", [op]), "organize"))
        {
            var seq = writer.Start(op);
            writer.Finish(seq, op, "done");
            id = writer.Id;
        }

        File.AppendAllText(Path.Combine(tree.P("_journals"), id + ".jsonl"), "{\"type\":\"step\",\"se");
        var journal = journals.Read(id)!;
        Assert.Equal("interrupted", journal.State);
        Assert.Single(journal.Applied);
        Assert.Single(journals.List());
    }
}
