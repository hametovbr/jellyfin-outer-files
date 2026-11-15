using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.OuterFiles.Configuration;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Keeps the list of videos that received outer files as JSON in the plugin data folder.
/// </summary>
/// <remarks>
/// It cannot live in <see cref="PluginConfiguration"/>: Jellyfin serializes plugin configuration with
/// <see cref="System.Xml.Serialization.XmlSerializer"/> and fails on collections of custom types
/// ("There was an error reflecting type ...PluginConfiguration").
/// </remarks>
public sealed class ScanHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanHistory"/> class.
    /// </summary>
    /// <param name="dataFolder">Plugin data folder; null disables persistence.</param>
    public ScanHistory(string? dataFolder)
    {
        _path = string.IsNullOrEmpty(dataFolder) ? null : Path.Combine(dataFolder, "history.json");
    }

    /// <summary>
    /// Gets the file the history is stored in.
    /// </summary>
    public string? FilePath => _path;

    /// <summary>
    /// Reads the stored history, newest first.
    /// </summary>
    /// <returns>Stored entries; empty when nothing was stored yet.</returns>
    public IReadOnlyList<VideoOuterFiles> Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<VideoOuterFiles>>(json, Options) ?? [];
        }
#pragma warning disable CA1031 // History is cosmetic, never fail the scan because of it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the stored history.
    /// </summary>
    /// <param name="entries">Entries, newest first.</param>
    /// <param name="limit">Maximum number of entries to keep.</param>
    public void Save(IEnumerable<VideoOuterFiles> entries, int limit)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var trimmed = entries.Take(Math.Max(1, limit)).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(trimmed, Options));
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Never let history writing break a scan.
        }
    }
}

/// <summary>
/// A video that received outer files.
/// </summary>
/// <param name="ItemId">Jellyfin item id.</param>
/// <param name="Path">File path.</param>
/// <param name="TrackCount">Number of attached tracks.</param>
/// <param name="ScannedAt">When the tracks were attached.</param>
public sealed record VideoOuterFiles(Guid ItemId, string Path, int TrackCount, DateTime ScannedAt);