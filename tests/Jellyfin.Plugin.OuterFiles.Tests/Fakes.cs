using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Host;

namespace Jellyfin.Plugin.OuterFiles.Tests;

internal sealed class FakeLibrary(string metadataRoot, IReadOnlyList<string> libraryRoots, IReadOnlyList<VideoRef> videos, IReadOnlyList<ReleaseRef>? releases = null) : ILibrarySource
{
    public int Scans { get; private set; }

    public VideoRef Single => videos[0];

    public string InternalMetadataRoot => metadataRoot;

    public IReadOnlyList<string> LibraryRoots() => libraryRoots;

    public IReadOnlyList<VideoRef> Videos() => videos;

    public VideoRef? Video(Guid id) => videos.FirstOrDefault(v => v.Id == id);

    public IReadOnlyList<(Guid Id, string Path, int? Season, int? Episode)> Episodes() => [];

    public IReadOnlyList<ReleaseRef> Releases() => releases ?? [];

    public void QueueLibraryScan() => Scans++;
}

internal sealed class FakeMetadata(Func<int, int, string?> titles) : IMetadataSource
{
    public Task<IReadOnlyList<MetadataHit>> SearchAsync(bool isMovie, string name, int? year, string language, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MetadataHit>>([new MetadataHit(name, year, new Dictionary<string, string> { ["Tmdb"] = "1" }, "fake", null, null)]);

    public Task<string?> EpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesIds, int season, int episode, string language, CancellationToken cancellationToken) =>
        Task.FromResult(titles(season, episode));
}
