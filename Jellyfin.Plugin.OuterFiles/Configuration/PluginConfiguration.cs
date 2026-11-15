using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OuterFiles.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
/// <remarks>
/// Jellyfin stores plugin configuration with <see cref="System.Xml.Serialization.XmlSerializer"/>, which cannot
/// reflect generic collections of custom types. Every property here therefore stays a scalar; the scan history
/// is kept as JSON in the plugin data folder by <see cref="ScanHistory"/>.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the audio file extensions treated as external audio tracks, comma separated.
    /// </summary>
    public string AudioExtensions { get; set; } = ".mka,.flac,.aac,.ac3,.dts,.eac3,.opus,.mp3";

    /// <summary>
    /// Gets or sets the maximum number of entries kept in the scan history.
    /// </summary>
    public int HistoryLimit { get; set; } = 200;

    /// <summary>
    /// Gets or sets the UTC time of the last scan.
    /// </summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>
    /// Gets or sets the number of videos that changed during the last scan.
    /// </summary>
    public int LastScanChanged { get; set; }

    /// <summary>
    /// Gets or sets the number of tracks attached during the last scan.
    /// </summary>
    public int LastScanTracks { get; set; }

    /// <summary>
    /// Gets or sets the number of videos visited during the last scan.
    /// </summary>
    public int LastScanScanned { get; set; }

    /// <summary>
    /// Gets or sets the number of errors during the last scan.
    /// </summary>
    public int LastScanErrors { get; set; }

    /// <summary>
    /// Splits <see cref="AudioExtensions"/> into a set of lower case extensions with the dot.
    /// </summary>
    /// <returns>Extensions such as ".mka".</returns>
    public string[] GetAudioExtensions()
        => string.IsNullOrWhiteSpace(AudioExtensions)
            ? [".mka", ".flac"]
            : AudioExtensions.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}