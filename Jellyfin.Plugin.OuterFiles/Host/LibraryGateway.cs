using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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
/// An episode with its stored numbers.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="Path">Path.</param>
/// <param name="Season">Stored season.</param>
/// <param name="Episode">Stored episode.</param>
/// <param name="NfoOverridesPath">A local <c>.nfo</c> sits next to the video and the library does not save
/// metadata into media folders: Jellyfin reads the numbers from that nfo again whenever the video changes.</param>
/// <param name="Locked">Metadata locked by the user (numbers set by hand).</param>
public sealed record EpisodeRef(Guid Id, string Path, int? Season, int? Episode, bool NfoOverridesPath, bool Locked = false);

/// <summary>
/// A series or movie folder as organize mode needs it.
/// </summary>
/// <param name="Root">Release folder.</param>
/// <param name="LibraryFolder">Library folder that contains it.</param>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Title Jellyfin knows it as.</param>
/// <param name="Year">Year Jellyfin knows.</param>
/// <param name="ProviderIds">Provider ids Jellyfin knows.</param>
/// <param name="Videos">Videos with their stored numbers and height.</param>
public sealed record ReleaseRef(string Root, string LibraryFolder, bool IsMovie, string Title, int? Year, IReadOnlyDictionary<string, string> ProviderIds, IReadOnlyList<ReleaseVideo> Videos);

/// <summary>
/// A video of a release as Jellyfin stores it.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="Path">Path.</param>
/// <param name="Season">Stored season.</param>
/// <param name="Episode">Stored episode.</param>
/// <param name="Height">Video height.</param>
public sealed record ReleaseVideo(Guid Id, string Path, int? Season, int? Episode, int? Height);

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
    IReadOnlyList<EpisodeRef> Episodes();

    /// <summary>Clears the stored season/episode numbers of an episode (a metadata edit).</summary>
    /// <param name="id">Item id.</param>
    /// <param name="clearSeason">Also clear the season number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task ClearEpisodeNumbersAsync(Guid id, bool clearSeason, CancellationToken cancellationToken);

    /// <summary>Gets every series and movie folder (not the library folders themselves).</summary>
    /// <returns>Releases.</returns>
    IReadOnlyList<ReleaseRef> Releases();

    /// <summary>Queues a library scan.</summary>
    void QueueLibraryScan();
}

/// <summary>
/// Reads the library through <see cref="ILibraryManager"/>. The only class of link mode that knows Jellyfin entities.
/// </summary>
public sealed class LibraryGateway : ILibrarySource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _paths;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryGateway"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="paths">Server paths.</param>
    /// <param name="providerManager">Provider manager.</param>
    public LibraryGateway(ILibraryManager libraryManager, IServerApplicationPaths paths, IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _paths = paths;
        _providerManager = providerManager;
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
    public IReadOnlyList<EpisodeRef> Episodes()
    {
        var savingByLibrary = new Dictionary<Guid, bool>();
        return _libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false, Recursive = true })
            .OfType<Episode>()
            .Where(e => e.IsFileProtocol && !string.IsNullOrEmpty(e.Path))
            .Select(e =>
            {
                var nfo = System.IO.Path.ChangeExtension(e.Path, ".nfo");
                var overrides = System.IO.File.Exists(nfo) && !Saves(e, savingByLibrary);
                return new EpisodeRef(e.Id, e.Path, e.ParentIndexNumber, e.IndexNumber, overrides, e.IsLocked);
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task ClearEpisodeNumbersAsync(Guid id, bool clearSeason, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(id) is not Episode episode)
        {
            return;
        }

        episode.IndexNumber = null;
        episode.IndexNumberEnd = null;
        if (clearSeason)
        {
            episode.ParentIndexNumber = null;
        }

        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    // Whether a refresh writes the nfo back. GetMetadataSavers answers for a manual edit, where an existing nfo
    // counts as enabled even with local saving off (ProviderManager.IsSaverEnabledForItem); after a refresh the
    // nfo is written only when the library saves local metadata or names its savers explicitly.
    private bool Saves(Episode episode, Dictionary<Guid, bool> cache)
    {
        var key = episode.SeriesId;
        if (!cache.TryGetValue(key, out var saves))
        {
            var options = _libraryManager.GetLibraryOptions(episode);
            saves = (options.MetadataSavers is not null || options.SaveLocalMetadata)
                && _providerManager.GetMetadataSavers(episode, options).Any();
            cache[key] = saves;
        }

        return saves;
    }

    /// <inheritdoc />
    public IReadOnlyList<ReleaseRef> Releases()
    {
        var roots = LibraryRoots();
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Video>().Where(v => v.IsFileProtocol && !string.IsNullOrEmpty(v.Path));

        var result = new List<ReleaseRef>();
        foreach (var group in items.GroupBy(v => ToRef(v)?.ReleaseRoot ?? string.Empty, StringComparer.Ordinal))
        {
            var library = roots.FirstOrDefault(r => System.IO.Path.GetDirectoryName(group.Key) == r);
            if (group.Key.Length == 0 || library is null)
            {
                continue; // videos directly in a library folder, or in nested folders: not organizable as one release
            }

            var first = group.First();
            BaseItem owner = first is Episode e && e.Series is not null ? e.Series : first;
            var videos = group.Select(v => new ReleaseVideo(v.Id, v.Path, v.ParentIndexNumber, v.IndexNumber, v.Height > 0 ? v.Height : null)).ToList();
            var folderName = System.IO.Path.GetFileName(group.Key);
            var title = owner.Name is null || owner.Name == folderName || owner.Name == System.IO.Path.GetFileNameWithoutExtension(first.Path)
                ? ReleaseNameParser.GuessTitle(owner.Name ?? folderName)
                : owner.Name;
            result.Add(new ReleaseRef(group.Key, library, first is Movie, title, owner.ProductionYear, new Dictionary<string, string>(owner.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase), videos));
        }

        return result.OrderBy(r => r.Root, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public void QueueLibraryScan() => _libraryManager.QueueLibraryScan();

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
