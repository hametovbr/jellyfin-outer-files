using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

public sealed class LinkReconcilerTests
{
    private const string Rel = "lib/Jitsu [2023][BDRip]";
    private const string Stem = "[VCB-Studio] Jitsu [01][Ma10p_1080p]";

    [Fact]
    public async Task CreatesNamedLinksIsIdempotentAndRemovesStaleOnes()
    {
        using var tree = new TempTree();
        var video = tree.File($"{Rel}/{Stem}.mkv");
        tree.File($"{Rel}/{Stem}.ass"); // adjacent: Jellyfin finds it itself
        var dub = tree.File($"{Rel}/RU Sound [Anilibria]/{Stem}.ru_Anilibria.mka");
        tree.File($"{Rel}/RU Sound [Anilibria]/надписи/{Stem}.ru_signs_CR.ass", "x");
        tree.File($"{Rel}/Sounds/Wakanim/{Stem}.mka");
        tree.File($"{Rel}/Subtitles/YakuSub/{Stem}.ass", RussianAss());
        tree.File($"{Rel}/RU Subs/other episode.ass");
        Directory.CreateDirectory(tree.P("meta"));
        var item = Guid.NewGuid();
        var library = new FakeLibrary(tree, [new VideoRef(item, video, tree.P(Rel), tree.P("meta/library/ab/" + item.ToString("N")), "episode")]);
        var refresh = new FakeRefresh();
        var reconciler = Create(tree, library, refresh);
        var config = new PluginConfiguration { DefaultAudioLanguage = "rus" };

        var first = await reconciler.RunAsync(config, null, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Empty(first!.Errors);
        Assert.Equal(4, first.Created);
        Assert.Equal([item], refresh.Items);
        Assert.Equal(1, first.Unmatched);
        var folder = library.Single.InternalMetadataPath;
        Assert.Equal(
            [
                $"{Stem}.Anilibria.rus.mka",
                $"{Stem}.CR.forced.rus.ass",
                $"{Stem}.Wakanim.rus.mka",
                $"{Stem}.YakuSub.rus.ass",
            ],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(dub, new FileInfo(Path.Combine(folder, $"{Stem}.Anilibria.rus.mka")).LinkTarget);

        var second = await reconciler.RunAsync(config, null, CancellationToken.None);
        Assert.Equal((0, 0, null), (second!.Created, second.Removed, second.JournalId));
        Assert.Single(refresh.Items);

        File.Delete(dub);
        var third = await reconciler.RunAsync(config, null, CancellationToken.None);
        Assert.Equal((0, 1), (third!.Created, third.Removed));
        Assert.Equal(3, Directory.EnumerateFiles(folder).Count());
    }

    [Fact]
    public void PreviewExplainsWithoutChangingAnything()
    {
        using var tree = new TempTree();
        var video = tree.File($"{Rel}/{Stem}.mkv");
        tree.File($"{Rel}/{Stem}.ass");
        tree.File($"{Rel}/EN Subs/{Stem}.en_CR.ass");
        Directory.CreateDirectory(tree.P("meta"));
        var item = Guid.NewGuid();
        var library = new FakeLibrary(tree, [new VideoRef(item, video, tree.P(Rel), tree.P("meta/x/" + item.ToString("N")), "episode")]);
        var preview = Create(tree, library, new FakeRefresh()).Preview(item, new PluginConfiguration())!;

        Assert.Single(preview.Desired);
        Assert.Equal($"{Stem}.CR.eng.ass", preview.Desired[0].Name);
        Assert.Single(preview.HostHandled);
        Assert.Equal(3, preview.Operations.Count); // two folders + one link
        Assert.False(Directory.Exists(tree.P("meta/x")));
    }

    private static LinkReconciler Create(TempTree tree, ILibrarySource library, IRefresher refresh)
    {
        var journals = new JournalStore(tree.P("_data/journals"));
        return new LinkReconciler(
            library,
            new FileSystemReader(),
            new PlanExecutor(journals, NullLogger<PlanExecutor>.Instance),
            refresh,
            new OperationGate(),
            new ReportStore(tree.P("_data/reports")),
            NullLogger<LinkReconciler>.Instance);
    }

    private static string RussianAss() =>
        "[Events]\n" + string.Concat(Enumerable.Repeat("Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Я не могу поверить, что это происходит на самом деле.\n", 8));

    private sealed class FakeLibrary(TempTree tree, IReadOnlyList<VideoRef> videos) : ILibrarySource
    {
        public VideoRef Single => videos[0];

        public string InternalMetadataRoot => tree.P("meta");

        public IReadOnlyList<string> LibraryRoots() => [tree.P("lib")];

        public IReadOnlyList<VideoRef> Videos() => videos;

        public VideoRef? Video(Guid id) => videos.FirstOrDefault(v => v.Id == id);

        public IReadOnlyList<(Guid Id, string Path, int? Season, int? Episode)> Episodes() => [];
    }

    private sealed class FakeRefresh : IRefresher
    {
        public List<Guid> Items { get; } = [];

        public void QueueRefresh(Guid itemId) => Items.Add(itemId);

        public void QueueReplaceAll(Guid itemId) => throw new InvalidOperationException("link mode never replaces metadata");
    }
}
