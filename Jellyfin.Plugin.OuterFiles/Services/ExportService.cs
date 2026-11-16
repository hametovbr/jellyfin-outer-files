using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using Jellyfin.Plugin.OuterFiles.Host;
using Jellyfin.Plugin.OuterFiles.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles.Services;

/// <summary>
/// Writes everything the organize mode works from into one file: every release with its files and stored
/// numbers, and for every series the episode list of its metadata source as Jellyfin's providers return it.
/// The file lets the whole library be planned and checked outside the server before anything is changed.
/// </summary>
public sealed class ExportService
{
    private readonly ILibrarySource _library;
    private readonly IMetadataSource _metadata;
    private readonly FileSystemReader _reader;
    private readonly ILogger<ExportService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportService"/> class.
    /// </summary>
    /// <param name="library">Library.</param>
    /// <param name="metadata">Metadata source.</param>
    /// <param name="reader">File system reader.</param>
    /// <param name="folder">Folder the export is written to.</param>
    /// <param name="logger">Logger.</param>
    public ExportService(ILibrarySource library, IMetadataSource metadata, FileSystemReader reader, string folder, ILogger<ExportService> logger)
    {
        _library = library;
        _metadata = metadata;
        _reader = reader;
        _logger = logger;
        File = Path.Combine(folder, "export.json.gz");
    }

    /// <summary>
    /// Gets the path of the export file.
    /// </summary>
    public string File { get; }

    /// <summary>
    /// Builds the export.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(PluginConfiguration config, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var releases = _library.Releases();
        var exported = new List<ExportedRelease>();
        var catalogs = new Dictionary<string, IReadOnlyList<ExportedCatalog>>(StringComparer.Ordinal);
        string[] languages = [config.TitleLanguage, "ru"];
        for (var i = 0; i < releases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var release = releases[i];
            var files = _reader.Snapshot(release.Root).Select(f => Inventory.Relative(release.Root, f)).Order(StringComparer.Ordinal).ToList();
            var key = string.Join(',', release.ProviderIds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
            if (!release.IsMovie && release.ProviderIds.Count > 0 && !catalogs.ContainsKey(key))
            {
                var list = new List<ExportedCatalog>();
                foreach (var language in languages.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(await CatalogAsync("all ids", release.ProviderIds, language, cancellationToken).ConfigureAwait(false));
                }

                catalogs[key] = list;
            }

            exported.Add(new ExportedRelease(
                release.Root,
                release.LibraryFolder,
                release.IsMovie,
                release.Title,
                release.Year,
                release.ProviderIds,
                release.Videos.Select(v => new ExportedVideo(Inventory.Relative(release.Root, v.Path), v.Season, v.Episode, v.Height)).ToList(),
                files,
                release.IsMovie ? null : key,
                NfoSamples(release.Root, files)));
            progress?.Report(100.0 * (i + 1) / Math.Max(1, releases.Count));
        }

        var export = new Export(DateTime.UtcNow, typeof(ExportService).Assembly.GetName().Version?.ToString() ?? string.Empty, config, exported, catalogs);
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var temp = File + ".tmp";
        await using (var stream = System.IO.File.Create(temp))
        await using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
        {
            await JsonSerializer.SerializeAsync(gzip, export, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        System.IO.File.Move(temp, File, true);
        _logger.LogInformation("Outer Files: exported {Releases} releases and {Catalogs} episode lists to {File}", exported.Count, catalogs.Count, File);
    }

    // Two episode nfo files per release (double files first): what a refresh reads back.
    private static Dictionary<string, string> NfoSamples(string root, List<string> files)
    {
        var samples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var nfo in files
            .Where(f => f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && f.Contains('/', StringComparison.Ordinal))
            .OrderByDescending(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"E\d+-E\d+"))
            .Take(2))
        {
            try
            {
                var text = System.IO.File.ReadAllText(Path.Combine(root, nfo));
                samples[nfo] = text.Length > 6000 ? text[..3000] + "\n…\n" + text[^3000..] + $"\n[{text.Length} chars]" : text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                samples[nfo] = "unreadable: " + ex.Message;
            }
        }

        return samples;
    }

    private async Task<ExportedCatalog> CatalogAsync(string ids, IReadOnlyDictionary<string, string> seriesIds, string language, CancellationToken cancellationToken)
    {
        var episodes = new List<CatalogEpisode>();

        // Specials are numbered with gaps: stop after several misses in a row.
        for (int episode = 1, misses = 0; episode <= 300 && misses < 6; episode++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = await _metadata.EpisodeTitleAsync(seriesIds, 0, episode, language, cancellationToken).ConfigureAwait(false);
            misses = title is null ? misses + 1 : 0;
            if (title is not null)
            {
                episodes.Add(new CatalogEpisode(0, episode, title));
            }
        }

        // Regular seasons: an unnamed episode in the middle does not end a season, two in a row do.
        for (int season = 1, empty = 0; season <= 80 && empty < 2; season++)
        {
            var found = 0;
            for (int episode = 1, misses = 0; episode <= 1000 && misses < 2; episode++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = await _metadata.EpisodeTitleAsync(seriesIds, season, episode, language, cancellationToken).ConfigureAwait(false);
                misses = title is null ? misses + 1 : 0;
                if (title is not null)
                {
                    episodes.Add(new CatalogEpisode(season, episode, title));
                    found++;
                }
            }

            empty = found == 0 ? empty + 1 : 0;
        }

        return new ExportedCatalog(ids, language, _metadata.AnsweredBy(seriesIds), episodes);
    }
}

/// <summary>The export file.</summary>
/// <param name="Utc">When it was written.</param>
/// <param name="PluginVersion">Plugin version.</param>
/// <param name="Configuration">Plugin configuration.</param>
/// <param name="Releases">Every release of the libraries.</param>
/// <param name="Catalogs">Episode lists by the series' provider ids.</param>
public sealed record Export(DateTime Utc, string PluginVersion, PluginConfiguration Configuration, IReadOnlyList<ExportedRelease> Releases, IReadOnlyDictionary<string, IReadOnlyList<ExportedCatalog>> Catalogs);

/// <summary>A release.</summary>
/// <param name="Root">Folder.</param>
/// <param name="LibraryFolder">Library folder.</param>
/// <param name="IsMovie">Movie or series.</param>
/// <param name="Title">Title in Jellyfin.</param>
/// <param name="Year">Year in Jellyfin.</param>
/// <param name="ProviderIds">Provider ids in Jellyfin.</param>
/// <param name="Videos">Videos Jellyfin knows, with stored numbers.</param>
/// <param name="Files">Every file of the folder, relative.</param>
/// <param name="CatalogKey">Key into the catalogs, for series.</param>
/// <param name="NfoSamples">Content of up to two episode nfo files.</param>
public sealed record ExportedRelease(string Root, string LibraryFolder, bool IsMovie, string Title, int? Year, IReadOnlyDictionary<string, string> ProviderIds, IReadOnlyList<ExportedVideo> Videos, IReadOnlyList<string> Files, string? CatalogKey, IReadOnlyDictionary<string, string> NfoSamples);

/// <summary>A video as Jellyfin has it.</summary>
/// <param name="Relative">Path relative to the release.</param>
/// <param name="Season">Stored season.</param>
/// <param name="Episode">Stored episode.</param>
/// <param name="Height">Video height.</param>
public sealed record ExportedVideo(string Relative, int? Season, int? Episode, int? Height);

/// <summary>An episode list.</summary>
/// <param name="Ids">Which provider ids were used.</param>
/// <param name="Language">Title language.</param>
/// <param name="Provider">Provider that answered.</param>
/// <param name="Episodes">Episodes with titles.</param>
public sealed record ExportedCatalog(string Ids, string Language, string? Provider, IReadOnlyList<CatalogEpisode> Episodes);
