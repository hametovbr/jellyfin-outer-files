using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using Jellyfin.Plugin.OuterFiles.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OuterFiles.Api;

/// <summary>
/// Thin HTTP surface for status and link mode. Endpoints only read state or hand work to services
/// (docs/ARCHITECTURE.md, P9). Organize endpoints live in OrganizeController.
/// </summary>
[ApiController]
[Route("OuterFiles")]
[Authorize(Policy = Policies.RequiresElevation)]
public class OuterFilesController : ControllerBase
{
    private readonly JournalStore _journals;
    private readonly ReportStore _reports;
    private readonly LinkReconciler _links;
    private readonly OperationGate _gate;
    private readonly ITaskManager _taskManager;
    private readonly NamingRulesService _naming;

    /// <summary>
    /// Initializes a new instance of the <see cref="OuterFilesController"/> class.
    /// </summary>
    /// <param name="journals">Journal store.</param>
    /// <param name="reports">Report store.</param>
    /// <param name="links">Link reconciler.</param>
    /// <param name="gate">Operation gate.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="naming">Naming rules service.</param>
    public OuterFilesController(JournalStore journals, ReportStore reports, LinkReconciler links, OperationGate gate, ITaskManager taskManager, NamingRulesService naming)
    {
        _journals = journals;
        _reports = reports;
        _links = links;
        _gate = gate;
        _taskManager = taskManager;
        _naming = naming;
    }

    /// <summary>
    /// Plugin status: running operation, naming rules, last link run.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult<OuterFilesStatus> GetStatus() => Ok(new OuterFilesStatus(
        _gate.Current,
        Plugin.Instance?.Configuration.EnableLinks ?? false,
        _naming.State,
        _reports.Load<LinkReport>("links"),
        _reports.Load<RenumberReport>("naming")));

    /// <summary>
    /// Shows what link mode would do for one video.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Preview.</returns>
    [HttpGet("Links/Preview/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LinkPreview> PreviewLinks([FromRoute] Guid itemId)
    {
        var preview = _links.Preview(itemId, Plugin.Instance!.Configuration);
        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>
    /// Queues a link run (the scheduled task); returns immediately.
    /// </summary>
    /// <returns>202 Accepted.</returns>
    [HttpPost("Links/Reconcile")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult ReconcileLinks()
    {
        _taskManager.QueueScheduledTask<LinkScheduledTask>();
        return Accepted();
    }

    /// <summary>
    /// Lists applied operations, newest first.
    /// </summary>
    /// <param name="limit">Maximum number of entries.</param>
    /// <returns>Journal summaries.</returns>
    [HttpGet("Journal")]
    public ActionResult<IReadOnlyList<JournalSummary>> GetJournal([FromQuery] int limit = 50) => Ok(_journals.List(limit));

    /// <summary>
    /// Queues the export task (releases, files, episode lists of the metadata sources). Changes nothing.
    /// </summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Export")]
    public ActionResult QueueExport()
    {
        _taskManager.QueueScheduledTask<ExportScheduledTask>();
        return Accepted();
    }

    /// <summary>
    /// Downloads the last export.
    /// </summary>
    /// <returns>The gzip-compressed JSON file, or 404 when no export was made yet.</returns>
    [HttpGet("Export")]
    public ActionResult GetExport()
    {
        var export = (Services.ExportService)HttpContext.RequestServices.GetService(typeof(Services.ExportService))!;
        return System.IO.File.Exists(export.File)
            ? PhysicalFile(export.File, "application/gzip", "outer-files-export.json.gz")
            : NotFound();
    }
}

/// <summary>
/// Plugin status.
/// </summary>
/// <param name="Running">Name of the running operation or null.</param>
/// <param name="LinksEnabled">Link mode enabled.</param>
/// <param name="NamingRules">State of the naming rules.</param>
/// <param name="LastLinkRun">Last link report.</param>
/// <param name="LastRenumbering">Last re-numbering pass.</param>
public sealed record OuterFilesStatus(string? Running, bool LinksEnabled, NamingRulesState NamingRules, LinkReport? LastLinkRun, RenumberReport? LastRenumbering);
