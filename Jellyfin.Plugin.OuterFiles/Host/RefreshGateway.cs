using System;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.OuterFiles.Host;

/// <summary>
/// Queue of item refreshes. Implemented by <see cref="RefreshGateway"/>; tests use a fake.
/// </summary>
public interface IRefresher
{
    /// <summary>Queues a metadata refresh of an item.</summary>
    /// <param name="itemId">Item id.</param>
    void QueueRefresh(Guid itemId);

    /// <summary>Queues a "replace all metadata" refresh, which makes Jellyfin re-read numbers from the path.</summary>
    /// <param name="itemId">Item id.</param>
    void QueueReplaceAll(Guid itemId);
}

/// <summary>
/// Hands items to Jellyfin's own refresh queue. A normal (non replace-all) refresh is enough: the probe
/// provider sees that the external files changed and re-probes the video.
/// </summary>
public sealed class RefreshGateway : IRefresher
{
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshGateway"/> class.
    /// </summary>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    public RefreshGateway(IProviderManager providerManager, IFileSystem fileSystem)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Queues a metadata refresh of an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    public void QueueRefresh(Guid itemId) =>
        _providerManager.QueueRefresh(
            itemId,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default,
            },
            RefreshPriority.Normal);

    /// <inheritdoc />
    public void QueueReplaceAll(Guid itemId) =>
        _providerManager.QueueRefresh(
            itemId,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = true,
            },
            RefreshPriority.Normal);
}
