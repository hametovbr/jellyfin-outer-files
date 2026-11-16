using System.IO;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Jellyfin.Plugin.OuterFiles.Services;
using Jellyfin.Plugin.OuterFiles.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
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
        serviceCollection.AddSingleton(sp => new JournalStore(DataFolder(sp, "journals")));
        serviceCollection.AddSingleton(sp => new ReportStore(DataFolder(sp, "reports")));
        serviceCollection.AddSingleton<PlanExecutor>();
        serviceCollection.AddSingleton<OperationGate>();
        serviceCollection.AddSingleton<FileSystemReader>();
        serviceCollection.AddSingleton<ILibrarySource, LibraryGateway>();
        serviceCollection.AddSingleton<IRefresher, RefreshGateway>();
        serviceCollection.AddSingleton<FallbackFonts>();
        serviceCollection.AddSingleton<LinkReconciler>();
        serviceCollection.AddSingleton<IScheduledTask, LinkScheduledTask>();
        serviceCollection.AddSingleton<NamingRulesService>();
        serviceCollection.AddSingleton<IMetadataSource, MetadataGateway>();
        serviceCollection.AddSingleton<OrganizeService>();
        serviceCollection.AddHostedService<NamingRulesHostedService>();
    }

    private static string DataFolder(System.IServiceProvider sp, string name)
        => Path.Combine(sp.GetRequiredService<IServerApplicationPaths>().DataPath, "outer-files", name);
}
