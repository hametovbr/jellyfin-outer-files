using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Jellyfin.Plugin.OuterFiles.Domain.Organize;
using MediaBrowser.Model.Configuration;
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
    /// <param name="provider">Only ask this provider (null: every enabled provider, first answer wins).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Title or null; placeholders such as <c>Episode #4.1</c> count as null.</returns>
    Task<string?> EpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesIds, int season, int episode, string language, string? provider, CancellationToken cancellationToken);

    /// <summary>Lists the installed providers that can return episode lists.</summary>
    /// <returns>Provider names.</returns>
    IReadOnlyList<string> EpisodeProviders() => [];

    /// <summary>
    /// Names the provider that answered the episode lookups for these series ids, when known.
    /// </summary>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <returns>Provider name or null.</returns>
    string? AnsweredBy(IReadOnlyDictionary<string, string> seriesIds) => null;
}

/// <summary>
/// Asks Jellyfin's installed metadata providers (TMDB is built in; TVDB/AniDB plugins add theirs) through
/// <see cref="IProviderManager"/>, with a cache for episode titles. Episode lookups are pinned to one provider
/// per release (<see cref="EpisodeSources"/>): without that, the host asks every provider and the first answer
/// wins, which mixes numberings of different sources in one list.
/// </summary>
public sealed class MetadataGateway : IMetadataSource
{
    private readonly IProviderManager _providers;
    private readonly ConcurrentDictionary<string, string?> _titles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _answeredBy = new(StringComparer.Ordinal);

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
    public async Task<string?> EpisodeTitleAsync(IReadOnlyDictionary<string, string> seriesIds, int season, int episode, string language, string? provider, CancellationToken cancellationToken)
    {
        var key = IdsKey(seriesIds) + $"|{season}|{episode}|{language}|{provider}";
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
                SearchProviderName = provider,

                // A provider the user picked for a release answers even when it is not enabled for the library.
                IncludeDisabledProviders = provider is not null,
            },
            cancellationToken).ConfigureAwait(false);

        var hit = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Name) && !EpisodeSources.IsPlaceholder(r.Name));
        var title = hit?.Name;
        _titles[key] = title;
        if (hit?.SearchProviderName is { } answered)
        {
            _answeredBy[IdsKey(seriesIds)] = answered;
        }

        return title;
    }

    /// <inheritdoc />
    public string? AnsweredBy(IReadOnlyDictionary<string, string> seriesIds) => _answeredBy.GetValueOrDefault(IdsKey(seriesIds));

    /// <inheritdoc />
    public IReadOnlyList<string> EpisodeProviders() => _providers.GetAllMetadataPlugins()
        .Where(s => s.ItemType == nameof(Episode))
        .SelectMany(s => s.Plugins)
        .Where(p => p.Type == MetadataPluginType.MetadataFetcher)
        .Select(p => p.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string IdsKey(IReadOnlyDictionary<string, string> seriesIds)
        => string.Join(',', seriesIds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
}
