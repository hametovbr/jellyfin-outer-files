using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Matching;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OuterFiles.Api;

/// <summary>
/// Plugin API endpoints.
/// </summary>
[ApiController]
[Route("OuterFiles")]
[Authorize(Policy = Policies.RequiresElevation)]
public class OuterFilesController : ControllerBase
{
    private readonly OuterFileScanner _scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OuterFilesController"/> class.
    /// </summary>
    /// <param name="scanner">Scanner.</param>
    public OuterFilesController(OuterFileScanner scanner)
    {
        _scanner = scanner;
    }

    /// <summary>
    /// Returns the last scan summary.
    /// </summary>
    /// <returns>Summary.</returns>
    [HttpGet("Status")]
    public ActionResult<OuterFilesStatus> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        return Ok(new OuterFilesStatus(
            config.LastScanUtc,
            config.LastScanScanned,
            config.LastScanChanged,
            config.LastScanTracks,
            config.LastScanErrors));
    }

    /// <summary>
    /// Returns the videos that received outer files, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of entries.</param>
    /// <returns>List of videos.</returns>
    [HttpGet("Videos")]
    public ActionResult<IEnumerable<VideoOuterFiles>> GetVideos([FromQuery] int limit = 50)
    {
        var entries = new ScanHistory(Plugin.Instance!.DataFolderPath).Load();
        return Ok(entries.Take(Math.Max(1, limit)));
    }

    /// <summary>
    /// Runs a scan now.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scan result.</returns>
    [HttpPost("Scan")]
    public async Task<ActionResult<ScanResult>> Scan(CancellationToken cancellationToken)
    {
        var result = await _scanner.ScanAsync(Plugin.Instance!.Configuration, null, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Explains which files would be attached to a single video, without touching the library.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <returns>List of matches.</returns>
    [HttpGet("Preview/{itemId}")]
    public ActionResult<IEnumerable<PreviewEntry>> Preview([FromRoute] Guid itemId)
    {
        var matches = _scanner.Preview(itemId);
        return matches is null ? NotFound() : Ok(matches);
    }
}

/// <summary>
/// Last scan summary.
/// </summary>
/// <param name="LastScanUtc">When the last scan ran.</param>
/// <param name="VideosScanned">Videos visited.</param>
/// <param name="VideosChanged">Videos that received tracks.</param>
/// <param name="Tracks">Tracks attached.</param>
/// <param name="Errors">Videos that failed.</param>
public sealed record OuterFilesStatus(DateTime? LastScanUtc, int VideosScanned, int VideosChanged, int Tracks, int Errors);
