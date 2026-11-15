using System;
using System.Collections.Generic;
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
        return Ok(new OuterFilesStatus(config.LastScanUtc, config.LastScanChanged, config.LastScanTracks, config.Videos.Count));
    }

    /// <summary>
    /// Returns the videos that received outer files, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of entries.</param>
    /// <returns>List of videos.</returns>
    [HttpGet("Videos")]
    public ActionResult<IEnumerable<VideoOuterFiles>> GetVideos([FromQuery] int limit = 50)
    {
        var config = Plugin.Instance!.Configuration;
        return Ok(config.Videos.Count > limit ? config.Videos.GetRange(0, Math.Max(0, limit)) : config.Videos);
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
/// <param name="VideosChanged">Videos changed.</param>
/// <param name="Tracks">Tracks attached.</param>
/// <param name="HistoryCount">Entries in history.</param>
public sealed record OuterFilesStatus(DateTime? LastScanUtc, int VideosChanged, int Tracks, int HistoryCount);
