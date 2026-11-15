using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OuterFiles.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the audio file extensions treated as external audio tracks.
    /// </summary>
    public List<string> AudioExtensions { get; set; } = [".mka", ".flac", ".aac", ".ac3", ".dts", ".eac3", ".opus", ".mp3"];

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
    /// Gets or sets the last attached videos, newest first.
    /// </summary>
    public List<VideoOuterFiles> Videos { get; set; } = [];
}