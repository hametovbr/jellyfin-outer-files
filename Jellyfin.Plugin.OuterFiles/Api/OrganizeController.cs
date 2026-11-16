using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OuterFiles.Api;

/// <summary>
/// Organize mode endpoints (docs/ARCHITECTURE.md, section 9). Thin: everything happens in
/// <see cref="OrganizeService"/>; applying requires the hash of the plan the user reviewed.
/// </summary>
[ApiController]
[Route("OuterFiles/Organize")]
[Authorize(Policy = Policies.RequiresElevation)]
public class OrganizeController : ControllerBase
{
    private readonly OrganizeService _organize;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeController"/> class.
    /// </summary>
    /// <param name="organize">Organize service.</param>
    public OrganizeController(OrganizeService organize)
    {
        _organize = organize;
    }

    /// <summary>Lists releases that do not follow the templates.</summary>
    /// <returns>Candidates.</returns>
    [HttpGet("Candidates")]
    public ActionResult<IReadOnlyList<Candidate>> Candidates() => Ok(_organize.Candidates(Plugin.Instance!.Configuration));

    /// <summary>Analyzes a release folder.</summary>
    /// <param name="body">Folder.</param>
    /// <returns>Analysis.</returns>
    [HttpPost("Analyze")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Analysis> Analyze([FromBody, Required] AnalyzeBody body)
    {
        var analysis = _organize.Analyze(body.Root);
        return analysis is null ? NotFound() : Ok(analysis);
    }

    /// <summary>Re-proposes a session with absolute numbers converted by season sizes.</summary>
    /// <param name="body">Session and series ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis.</returns>
    [HttpPost("Absolute")]
    public async Task<ActionResult<Analysis>> Absolute([FromBody, Required] AbsoluteBody body, CancellationToken cancellationToken)
    {
        var analysis = await _organize.ProposeWithAbsoluteNumbersAsync(body.SessionId, body.ProviderIds, Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false);
        return analysis is null ? NotFound() : Ok(analysis);
    }

    /// <summary>Re-proposes the rows by episode titles in the file names.</summary>
    /// <param name="body">Session and series ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis.</returns>
    [HttpPost("Titles")]
    public async Task<ActionResult<Analysis>> Titles([FromBody, Required] AbsoluteBody body, CancellationToken cancellationToken)
    {
        var analysis = await _organize.ProposeByTitlesAsync(body.SessionId, body.ProviderIds, Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false);
        return analysis is null ? NotFound() : Ok(analysis);
    }

    /// <summary>Stores the episode source of a release, or removes it for the default.</summary>
    /// <param name="body">Release folder and provider.</param>
    /// <returns>Error, or 204 when saved.</returns>
    [HttpPost("Source")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Source([FromBody, Required] SourceBody body)
    {
        var error = _organize.SaveSource(body.Root, body.Provider);
        return error is null ? NoContent() : BadRequest(error);
    }

    /// <summary>Searches metadata providers.</summary>
    /// <param name="name">Name.</param>
    /// <param name="isMovie">Movie or series.</param>
    /// <param name="year">Year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hits.</returns>
    [HttpGet("Search")]
    public async Task<ActionResult<IReadOnlyList<MetadataHit>>> Search([FromQuery, Required] string name, [FromQuery] bool isMovie, [FromQuery] int? year, CancellationToken cancellationToken)
        => Ok(await _organize.SearchAsync(isMovie, name, year, Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false));

    /// <summary>Builds the plan for a session.</summary>
    /// <param name="request">Identity and decisions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Plan.</returns>
    [HttpPost("Plan")]
    public async Task<ActionResult<PlanView>> Plan([FromBody, Required] PlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await _organize.PlanAsync(request, Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>Applies the reviewed plan.</summary>
    /// <param name="body">Session and plan hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    [HttpPost("Apply")]
    public async Task<ActionResult<ExecutionResult>> Apply([FromBody, Required] ApplyBody body, CancellationToken cancellationToken)
        => Ok(await _organize.ApplyAsync(body.SessionId, body.Hash, cancellationToken).ConfigureAwait(false));

    /// <summary>Undoes an organize run.</summary>
    /// <param name="journalId">Journal id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    [HttpPost("Undo/{journalId}")]
    public async Task<ActionResult<ExecutionResult>> Undo([FromRoute] string journalId, CancellationToken cancellationToken)
        => Ok(await _organize.UndoAsync(journalId, cancellationToken).ConfigureAwait(false));
}

/// <summary>Body of Analyze.</summary>
/// <param name="Root">Release folder.</param>
public sealed record AnalyzeBody(string Root);

/// <summary>Body of Absolute.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="ProviderIds">Series provider ids.</param>
public sealed record AbsoluteBody(string SessionId, IReadOnlyDictionary<string, string> ProviderIds);

/// <summary>Body of Apply.</summary>
/// <param name="SessionId">Session.</param>
/// <param name="Hash">Plan hash.</param>
public sealed record ApplyBody(string SessionId, string Hash);

/// <summary>Body of Source.</summary>
/// <param name="Root">Release folder.</param>
/// <param name="Provider">Provider name, or null/empty for the default (delete the stored choice).</param>
public sealed record SourceBody(string Root, string? Provider);
