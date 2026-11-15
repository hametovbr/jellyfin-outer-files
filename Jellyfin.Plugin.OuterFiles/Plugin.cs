using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.OuterFiles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Plugin entry point. Only Jellyfin-facing glue uses <see cref="Instance"/>; services receive what they
/// need through constructor injection (docs/ARCHITECTURE.md, P12).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Raised after the configuration was saved from the settings page.
    /// </summary>
    public static event EventHandler<PluginConfiguration>? ConfigurationSaved;

    /// <summary>
    /// Gets the running instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Outer Files";

    /// <inheritdoc />
    public override string Description =>
        "Links audio and subtitle files from sub folders, fixes episode recognition of release names, and organizes releases into one naming scheme.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("3dcc5bff-46c4-4ed9-aa71-67b520faee96");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "OuterFiles",
            DisplayName = "Outer Files",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
        },
        new PluginPageInfo
        {
            Name = "OuterFilesOrganize",
            DisplayName = "Outer Files: Organize",
            EnableInMainMenu = true,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.organizePage.html", GetType().Namespace),
        },
    ];

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, Configuration);
    }
}
