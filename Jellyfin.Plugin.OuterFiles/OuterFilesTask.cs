using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Scheduled task that links outer audio and subtitle files to videos.
/// </summary>
public class OuterFilesTask : IScheduledTask
{
    private readonly OuterFileScanner _scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OuterFilesTask"/> class.
    /// </summary>
    /// <param name="scanner">Scanner.</param>
    public OuterFilesTask(OuterFileScanner scanner)
    {
        _scanner = scanner;
    }

    /// <inheritdoc />
    public string Name => "Attach outer audio and subtitle files";

    /// <inheritdoc />
    public string Key => "OuterFilesAttach";

    /// <inheritdoc />
    public string Description =>
        "Walks the folder of every video, matches audio and subtitle files from sub folders to the video and links them as external tracks. Media files are never modified.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _scanner.ScanAsync(Plugin.Instance!.Configuration, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks,
        },
    ];
}
