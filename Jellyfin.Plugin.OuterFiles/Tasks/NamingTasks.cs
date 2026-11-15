using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Host;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="NamingPostScanTask"/> class.
    /// </summary>
    /// <param name="naming">Naming rules service.</param>
    /// <param name="library">Library source.</param>
    /// <param name="refresh">Refresh queue.</param>
    /// <param name="logger">Logger.</param>
    public NamingPostScanTask(NamingRulesService naming, ILibrarySource library, IRefresher refresh, ILogger<NamingPostScanTask> logger)
    {
        _naming = naming;
        _library = library;
        _refresh = refresh;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_naming.State.Installed)
        {
            return Task.CompletedTask;
        }

        var fixes = _naming.FindMisnumbered(_library.Episodes());
        foreach (var fix in fixes)
        {
            _refresh.QueueReplaceAll(fix.Id);
        }

        if (fixes.Count > 0)
        {
            _logger.LogInformation(
                "Outer Files: re-numbering {Count} episodes, e.g. {Example}",
                fixes.Count,
                string.Join(", ", fixes.Take(3).Select(f => $"{System.IO.Path.GetFileName(f.Path)}: S{f.OldSeason}E{f.OldEpisode} -> S{f.NewSeason}E{f.NewEpisode}")));
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}
