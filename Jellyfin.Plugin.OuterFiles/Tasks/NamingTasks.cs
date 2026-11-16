using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.Tasks;

/// <summary>
/// Installs the naming rules when the server starts and follows the setting afterwards.
/// </summary>
public sealed class NamingRulesHostedService : IHostedService
{
    private readonly NamingRulesService _naming;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamingRulesHostedService"/> class.
    /// </summary>
    /// <param name="naming">Naming rules service.</param>
    public NamingRulesHostedService(NamingRulesService naming)
    {
        _naming = naming;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Plugin.ConfigurationSaved += OnConfigurationSaved;
        _naming.Apply(Plugin.Instance?.Configuration.EnableNamingRules ?? true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Plugin.ConfigurationSaved -= OnConfigurationSaved;
        return Task.CompletedTask;
    }

    private void OnConfigurationSaved(object? sender, PluginConfiguration config) => _naming.Apply(config.EnableNamingRules);
}

/// <summary>
/// After each library scan: episodes whose stored numbers are the known-wrong result of Jellyfin's own
/// expressions get a replace-all refresh, so Jellyfin re-reads the numbers with the installed rules.
/// </summary>
public sealed class NamingPostScanTask : ILibraryPostScanTask
{
    private readonly NamingRulesService _naming;
    private readonly ILibrarySource _library;
    private readonly IRefresher _refresh;
    private readonly ILogger<NamingPostScanTask> _logger;
    private readonly ReportStore _reports;

    // Each episode is handled once per server run, so an unexpected outcome can never turn into a refresh on every scan.
    private readonly HashSet<Guid> _attempted = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="NamingPostScanTask"/> class.
    /// </summary>
    /// <param name="naming">Naming rules service.</param>
    /// <param name="library">Library source.</param>
    /// <param name="refresh">Refresh queue.</param>
    /// <param name="reports">Report store.</param>
    /// <param name="logger">Logger.</param>
    public NamingPostScanTask(NamingRulesService naming, ILibrarySource library, IRefresher refresh, ReportStore reports, ILogger<NamingPostScanTask> logger)
    {
        _reports = reports;
        _naming = naming;
        _library = library;
        _refresh = refresh;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_naming.State.Installed)
        {
            return;
        }

        var fixes = _naming.FindMisnumbered(_library.Episodes());
        var refreshed = 0;
        var cleared = 0;
        foreach (var fix in fixes.Where(f => _attempted.Add(f.Id)))
        {
            // A stale nfo is skipped by a replace-all refresh (Jellyfin counts an existing nfo as a saver), so the
            // library shows the right number until the video file changes and the nfo is read again; reported below.
            if (fix.Action is RenumberAction.Refresh or RenumberAction.StaleNfo)
            {
                _refresh.QueueReplaceAll(fix.Id);
                refreshed++;
            }
            else
            {
                await _library.ClearEpisodeNumbersAsync(fix.Id, fix.ClearSeason, cancellationToken).ConfigureAwait(false);
                cleared++;
            }
        }

        var blocked = fixes.Where(f => f.Action == RenumberAction.StaleNfo).ToList();
        _reports.Save("naming", new RenumberReport(DateTime.UtcNow, refreshed, cleared, blocked.Count, blocked.Take(100).Select(f => f.Path).ToList()));
        if (refreshed + cleared + blocked.Count > 0)
        {
            _logger.LogInformation(
                "Outer Files: episode numbers: {Refreshed} re-read, {Cleared} cleared, {Blocked} with a stale .nfo file",
                refreshed,
                cleared,
                blocked.Count);
        }

        progress.Report(100);
    }
}

/// <summary>
/// Result of the last re-numbering pass.
/// </summary>
/// <param name="Utc">When.</param>
/// <param name="Refreshed">Episodes queued for a replace-all refresh.</param>
/// <param name="Cleared">Episodes whose wrong number was cleared.</param>
/// <param name="StaleNfo">Episodes fixed in the library whose local .nfo still holds the wrong number and brings it back when the video changes (enable "Save artwork and metadata into media folders", organize the release, or delete the nfo).</param>
/// <param name="StaleNfoSample">Up to 100 of them.</param>
public sealed record RenumberReport(DateTime Utc, int Refreshed, int Cleared, int StaleNfo, IReadOnlyList<string> StaleNfoSample);
