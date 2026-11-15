using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OuterFiles.Configuration;

/// <summary>
/// Plugin settings. Scalars only: Jellyfin stores plugin configuration with <c>XmlSerializer</c>, which
/// cannot reflect lists of custom types (that broke 0.1.0). Tested by <c>PluginConfigurationTests</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether link mode runs after library scans.</summary>
    public bool EnableLinks { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the plugin's episode naming rules are installed.</summary>
    public bool EnableNamingRules { get; set; } = true;

    /// <summary>Gets or sets the language for external audio whose language cannot be found (ISO 639-2).</summary>
    public string DefaultAudioLanguage { get; set; } = "rus";

    /// <summary>Gets or sets the series folder template.</summary>
    public string SeriesFolderTemplate { get; set; } = "{Series Title} ({Year})";

    /// <summary>Gets or sets the season folder template.</summary>
    public string SeasonFolderTemplate { get; set; } = "Season {season:00}";

    /// <summary>Gets or sets the episode file template (without extension).</summary>
    public string EpisodeFileTemplate { get; set; } = "{Series Title} ({Year}) - S{season:00}E{episode:00}{-Eend:00} - {Episode Title} {Quality}";

    /// <summary>Gets or sets the movie folder template.</summary>
    public string MovieFolderTemplate { get; set; } = "{Movie Title} ({Year})";

    /// <summary>Gets or sets the movie file template (without extension).</summary>
    public string MovieFileTemplate { get; set; } = "{Movie Title} ({Year}) {Quality}";
}
