using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.OuterFiles.Host;

/// <summary>
/// A video as the plugin needs it: no Jellyfin types leak past the gateway.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="Path">Absolute file path.</param>
/// <param name="ReleaseRoot">Folder whose tree may hold the video's tracks: the series folder for episodes, the movie folder for movies.</param>
/// <param name="InternalMetadataPath">The item's internal metadata folder.</param>
/// <param name="Kind">episode, movie or video.</param>
public sealed record VideoRef(Guid Id, string Path, string ReleaseRoot, string InternalMetadataPath, string Kind);

/// <summary>
/// What services need from the library. Implemented by <see cref="LibraryGateway"/>; tests use a fake.
/// </summary>
public interface ILibrarySource
{
    /// <summary>Gets the root of all internal metadata folders.</summary>
    string InternalMetadataRoot { get; }

    /// <summary>Gets the folders of all movie and TV libraries.</summary>
    /// <returns>Absolute folder paths.</returns>
    IReadOnlyList<string> LibraryRoots();

    /// <summary>Gets every file-based video.</summary>
    /// <returns>Videos.</returns>
    IReadOnlyList<VideoRef> Videos();

    /// <summary>Gets one video.</summary>
    /// <param name="id">Item id.</param>
    /// <returns>The video or null.</returns>
    VideoRef? Video(Guid id);

    /// <summary>Gets every file-based episode with its stored numbers.</summary>
    /// <returns>Episodes.</returns>
    IReadOnlyList<(Guid Id, string Path, int? Season, int? Episode)> Episodes();
}

/// <summary>
/// Reads the library through <see cref="ILibraryManager"/>. The only class of link mode that knows Jellyfin entities.
/// </summary>
public sealed class LibraryGateway : ILibrarySource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryGateway"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="paths">Server paths.</param>
    public LibraryGateway(ILibraryManager libraryManager, IServerApplicationPaths paths)
    {
        _libraryManager = libraryManager;
        _paths = paths;
    }

    /// <summary>
    /// Gets the root of all internal metadata folders.
    /// </summary>
    public string InternalMetadataRoot => _paths.InternalMetadataPath;

    /// <summary>
    /// Gets the folders of all movie and TV libraries (and mixed ones).
    /// </summary>
    /// <returns>Absolute folder paths.</returns>
    public IReadOnlyList<string> LibraryRoots() => _libraryManager.GetVirtualFolders()
        .Where(f => f.CollectionType is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed or CollectionTypeOptions.homevideos)
        .SelectMany(f => f.Locations ?? [])
        .Where(l => !string.IsNullOrEmpty(l))
        .Select(l => l.TrimEnd('/', '\\'))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Gets every file-based video of the library.
    /// </summary>
    /// <returns>Videos.</returns>
    public IReadOnlyList<VideoRef> Videos()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo],
            IsVirtualItem = false,
            Recursive = true,
        });

        return items.Select(ToRef).Where(v => v is not null).Select(v => v!).ToList();
    }

    /// <summary>
    /// Gets one video.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>The video, or null when the item is not a file-based video.</returns>
    public VideoRef? Video(Guid id) => ToRef(_libraryManager.GetItemById(id));

    /// <inheritdoc />
    public IReadOnlyList<(Guid Id, string Path, int? Season, int? Episode)> Episodes() => _libraryManager
        .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false, Recursive = true })
        .OfType<Episode>()
        .Where(e => e.IsFileProtocol && !string.IsNullOrEmpty(e.Path))
        .Select(e => (e.Id, e.Path, e.ParentIndexNumber, e.IndexNumber))
        .ToList();

    private static VideoRef? ToRef(BaseItem? item)
    {
        if (item is not Video video
            || !video.IsFileProtocol
            || video.VideoType != VideoType.VideoFile
            || string.IsNullOrEmpty(video.Path)
            || string.IsNullOrEmpty(video.ContainingFolderPath))
        {
            return null;
        }

        var (root, kind) = video switch
        {
            Episode e when !string.IsNullOrEmpty(e.Series?.Path) => (e.Series!.Path, "episode"),
            Episode e => (e.ContainingFolderPath, "episode"),
            Movie m => (m.ContainingFolderPath, "movie"),
            _ => (video.ContainingFolderPath, "video"),
        };

        return new VideoRef(video.Id, video.Path, root.TrimEnd('/', '\\'), video.GetInternalMetadataPath(), kind);
    }
}
