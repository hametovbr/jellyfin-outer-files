using System.IO;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Composition root: every service of the plugin is wired here (docs/ARCHITECTURE.md, P12).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new JournalStore(
            Path.Combine(sp.GetRequiredService<IServerApplicationPaths>().DataPath, "outer-files", "journals")));
        serviceCollection.AddSingleton<PlanExecutor>();
        serviceCollection.AddSingleton<OperationGate>();
    }
}
