using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.OuterFiles.IO;

/// <summary>
/// Keeps the last report of each kind for the status page. Diagnostics only, never read back as input
/// (docs/ARCHITECTURE.md, P6).
/// </summary>
public sealed class ReportStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportStore"/> class.
    /// </summary>
    /// <param name="folder">Folder for report files.</param>
    public ReportStore(string folder)
    {
        _folder = folder;
    }

    /// <summary>
    /// Saves a report.
    /// </summary>
    /// <typeparam name="T">Report type.</typeparam>
    /// <param name="name">Report name.</param>
    /// <param name="report">Report.</param>
    public void Save<T>(string name, T report)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Loads a report.
    /// </summary>
    /// <typeparam name="T">Report type.</typeparam>
    /// <param name="name">Report name.</param>
    /// <returns>The report, or default when missing or unreadable.</returns>
    public T? Load<T>(string name)
    {
        var path = Path.Combine(_folder, name + ".json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return default;
        }
    }
}
