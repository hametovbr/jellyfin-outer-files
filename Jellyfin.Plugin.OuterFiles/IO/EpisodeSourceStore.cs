using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;

namespace Jellyfin.Plugin.OuterFiles.IO;

/// <summary>
/// Reads and writes the episode source choice of a release (<see cref="EpisodeSources.FileName"/> in the release
/// folder). The file is small, plugin-owned and ignored by Jellyfin (dot file); a file with the same name that the
/// plugin did not write is never replaced or removed.
/// </summary>
public sealed class EpisodeSourceStore
{
    /// <summary>
    /// Resolves the source of a release.
    /// </summary>
    /// <param name="root">Release folder.</param>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <returns>The source.</returns>
    public EpisodeSource Resolve(string root, IReadOnlyDictionary<string, string> seriesIds) => EpisodeSources.Resolve(ReadText(root), seriesIds);

    /// <summary>
    /// Stores a choice, or removes the stored one when <paramref name="provider"/> is empty.
    /// </summary>
    /// <param name="root">Release folder.</param>
    /// <param name="provider">Provider name, or null/empty for the default.</param>
    /// <returns>Null on success, else the reason it was not done.</returns>
    public string? Save(string root, string? provider)
    {
        if (!Directory.Exists(root))
        {
            return "The release folder does not exist.";
        }

        var path = Path.Combine(root, EpisodeSources.FileName);
        var existing = ReadText(root);
        if (existing is not null && !EpisodeSources.IsOwned(existing))
        {
            return $"{path} exists and was not written by this plugin; it is left as it is.";
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            if (existing is not null)
            {
                File.Delete(path);
            }

            return null;
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, EpisodeSources.Write(provider));
        File.Move(temp, path, true);
        return null;
    }

    private static string? ReadText(string root)
    {
        var path = Path.Combine(root, EpisodeSources.FileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
