using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.OuterFiles.Tasks;

/// <summary>
/// Runs link mode after every library scan, so new tracks show up without user action.
/// Jellyfin discovers <see cref="ILibraryPostScanTask"/> implementations in plugins by itself.
/// </summary>
public sealed class LinkPostScanTask : ILibraryPostScanTask
{
    private readonly LinkReconciler _reconciler;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPostScanTask"/> class.
    /// </summary>
    /// <param name="reconciler">Link reconciler.</param>
    public LinkPostScanTask(LinkReconciler reconciler)
    {
        _reconciler = reconciler;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is { EnableLinks: true })
        {
            await _reconciler.RunAsync(config, progress, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The same run as a scheduled task: visible in the dashboard, startable by hand or from the plugin page.
/// </summary>
public sealed class LinkScheduledTask : IScheduledTask
{
    private readonly LinkReconciler _reconciler;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkScheduledTask"/> class.
    /// </summary>
    /// <param name="reconciler">Link reconciler.</param>
    public LinkScheduledTask(LinkReconciler reconciler)
    {
        _reconciler = reconciler;
    }

    /// <inheritdoc />
    public string Name => "Link outer audio and subtitle tracks";

    /// <inheritdoc />
    public string Key => "OuterFilesLinks";

    /// <inheritdoc />
    public string Description => "Links audio and subtitle files stored in sub folders of a release to their video. Also runs after every library scan.";

    /// <inheritdoc />
    public string Category => "Outer Files";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _reconciler.RunAsync(Plugin.Instance!.Configuration, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
