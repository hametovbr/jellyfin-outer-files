using System.Collections.Generic;
using Jellyfin.Plugin.OuterFiles.IO;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OuterFiles.Api;

/// <summary>
/// Thin HTTP surface. Endpoints only read state or hand work to services (docs/ARCHITECTURE.md, P9).
/// </summary>
[ApiController]
[Route("OuterFiles")]
[Authorize(Policy = Policies.RequiresElevation)]
public class OuterFilesController : ControllerBase
{
    private readonly JournalStore _journals;

    /// <summary>
    /// Initializes a new instance of the <see cref="OuterFilesController"/> class.
    /// </summary>
    /// <param name="journals">Journal store.</param>
    public OuterFilesController(JournalStore journals)
    {
        _journals = journals;
    }

    /// <summary>
    /// Lists applied operations, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of entries.</param>
    /// <returns>Journal summaries.</returns>
    [HttpGet("Journal")]
    public ActionResult<IReadOnlyList<JournalSummary>> GetJournal([FromQuery] int limit = 50) => Ok(_journals.List(limit));
}
