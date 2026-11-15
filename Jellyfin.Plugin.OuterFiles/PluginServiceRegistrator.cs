using Jellyfin.Plugin.OuterFiles.Matching;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OuterFileMatcher>();
        serviceCollection.AddSingleton<OuterFileScanner>();
        serviceCollection.AddSingleton<IScheduledTask, OuterFilesTask>();
    }
}
