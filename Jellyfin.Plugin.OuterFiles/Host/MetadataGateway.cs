using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.OuterFiles.Host;

/// <summary>
/// A search hit from a metadata provider.
/// </summary>
/// <param name="Title">Title.</param>
/// <param name="Year">Premiere year.</param>
/// <param name="ProviderIds">Provider ids (Tmdb, Tvdb, Imdb, …).</param>
/// <param name="Provider">Provider that found it.</param>
/// <param name="Overview">Overview.</param>
/// <param name="ImageUrl">Poster URL.</param>
public sealed record MetadataHit(string Title, int? Year, IReadOnlyDictionary<string, string> ProviderIds, string? Provider, string? Overview, string? ImageUrl);

/// <summary>
/// What organize mode needs from metadata providers. Implemented by <see cref="MetadataGateway"/>.
/// </summary>
public interface IMetadataSource
{
    /// <summary>Searches series or movies.</summary>
    /// <param name="isMovie">Movie or series.</param>
    /// <param name="name">Name.</param>
    /// <param name="year">Year or null.</param>
    /// <param name="language">Metadata language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hits.</returns>
    Task<IReadOnlyList<MetadataHit>> SearchAsync(bool isMovie, string name, int? year, string language, CancellationToken cancellationToken);

    /// <summary>Gets an episode title, or null when the provider does not know the episode.</summary>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <param name="season">Season.</param>
    /// <param name="episode">Episode.</param>
    /// <param name="language">Metadata language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Title or null.</returns>
    Task<string?> EpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesIds, int season, int episode, string language, CancellationToken cancellationToken);
}

/// <summary>
/// Asks Jellyfin's installed metadata providers (TMDB is built in; TVDB/AniDB plugins add theirs) through
/// <see cref="IProviderManager"/>, with a cache for episode titles.
/// </summary>
public sealed class MetadataGateway : IMetadataSource
{
    private readonly IProviderManager _providers;
    private readonly ConcurrentDictionary<string, string?> _titles = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataGateway"/> class.
    /// </summary>
    /// <param name="providers">Provider manager.</param>
    public MetadataGateway(IProviderManager providers)
    {
        _providers = providers;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetadataHit>> SearchAsync(bool isMovie, string name, int? year, string language, CancellationToken cancellationToken)
    {
        IEnumerable<RemoteSearchResult> results = isMovie
            ? await _providers.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { Name = name, Year = year, MetadataLanguage = language } },
                cancellationToken).ConfigureAwait(false)
            : await _providers.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { Name = name, Year = year, MetadataLanguage = language } },
                cancellationToken).ConfigureAwait(false);

        return results.Take(20).Select(r => new MetadataHit(
            r.Name ?? string.Empty,
            r.ProductionYear ?? r.PremiereDate?.Year,
            new Dictionary<string, string>(r.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            r.SearchProviderName,
            r.Overview,
            r.ImageUrl)).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> EpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesIds, int season, int episode, string language, CancellationToken cancellationToken)
    {
        var key = string.Join(',', seriesIds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)) + $"|{season}|{episode}|{language}";
        if (_titles.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var results = await _providers.GetRemoteSearchResults<Episode, EpisodeInfo>(
            new RemoteSearchQuery<EpisodeInfo>
            {
                SearchInfo = new EpisodeInfo
                {
                    SeriesProviderIds = seriesIds.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase),
                    ParentIndexNumber = season,
                    IndexNumber = episode,
                    MetadataLanguage = language,
                },
            },
            cancellationToken).ConfigureAwait(false);

        var title = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Name))?.Name;
        _titles[key] = title;
        return title;
    }
}
