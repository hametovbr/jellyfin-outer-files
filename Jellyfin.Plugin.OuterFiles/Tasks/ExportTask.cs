using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.OuterFiles.Tasks;

/// <summary>
/// Writes the diagnostic export on demand.
/// </summary>
public sealed class ExportScheduledTask : IScheduledTask
{
    private readonly ExportService _export;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportScheduledTask"/> class.
    /// </summary>
    /// <param name="export">Export service.</param>
    public ExportScheduledTask(ExportService export)
    {
        _export = export;
    }

    /// <inheritdoc />
    public string Name => "Export releases and episode lists";

    /// <inheritdoc />
    public string Key => "OuterFilesExport";

    /// <inheritdoc />
    public string Description => "Writes every release with its files and the episode lists of its metadata source into one file (GET /OuterFiles/Export). Changes nothing.";

    /// <inheritdoc />
    public string Category => "Outer Files";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _export.RunAsync(Plugin.Instance!.Configuration, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
