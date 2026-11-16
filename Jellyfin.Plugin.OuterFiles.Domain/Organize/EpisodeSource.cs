using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Domain.Organize;

/// <summary>
/// Where a release's episode list comes from, and how that choice is resolved.
/// </summary>
/// <param name="Provider">Metadata provider name as Jellyfin knows it (<c>TheMovieDb</c>, <c>TVmaze</c>, …), or null for "any".</param>
/// <param name="Origin"><see cref="EpisodeSources.FromFile"/> or <see cref="EpisodeSources.FromDefault"/>.</param>
public sealed record EpisodeSource(string? Provider, string Origin);

/// <summary>
/// One provider per release (docs/ARCHITECTURE.md, section 7.4). Jellyfin keeps only the ids a series was identified
/// with, not the provider the user picked, and asking every provider per episode mixes numberings (TMDb for the
/// seasons it knows, OMDb placeholders after them). The choice is therefore stored in the release folder, in a
/// dot file Jellyfin ignores, and moves with the folder when it is organized (P6: the file system is the truth).
/// </summary>
public static partial class EpisodeSources
{
    /// <summary>Name of the choice file in the release folder.</summary>
    public const string FileName = ".outer-files.json";

    /// <summary>The choice was read from the release folder.</summary>
    public const string FromFile = "file";

    /// <summary>No choice stored: derived from the series ids.</summary>
    public const string FromDefault = "default";

    /// <summary>Name of Jellyfin's built-in TMDb provider.</summary>
    public const string Tmdb = "TheMovieDb";

    private const string Owner = "Outer Files";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Resolves the source of a release: the stored choice, else TMDb when the series has a TMDb id, else any provider.
    /// </summary>
    /// <param name="stored">Content of the choice file, or null when there is none.</param>
    /// <param name="seriesIds">Series provider ids.</param>
    /// <returns>The source.</returns>
    public static EpisodeSource Resolve(string? stored, IReadOnlyDictionary<string, string> seriesIds)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);
        if (Read(stored) is { } provider)
        {
            return new EpisodeSource(provider, FromFile);
        }

        var hasTmdb = seriesIds.Any(k => string.Equals(k.Key, "Tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(k.Value));
        return new EpisodeSource(hasTmdb ? Tmdb : null, FromDefault);
    }

    /// <summary>
    /// Reads the provider from a choice file; null when the text is not a choice file of this plugin.
    /// </summary>
    /// <param name="text">File content.</param>
    /// <returns>Provider or null.</returns>
    public static string? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("EpisodeSource", out var source)
                && source.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(source.GetString())
                ? source.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells whether a file is a choice file this plugin wrote (and may therefore replace or remove).
    /// </summary>
    /// <param name="text">File content.</param>
    /// <returns>True when owned.</returns>
    public static bool IsOwned(string? text)
    {
        if (Read(text) is null)
        {
            return false;
        }

        using var doc = JsonDocument.Parse(text!);
        return doc.RootElement.TryGetProperty("Plugin", out var owner) && owner.ValueKind == JsonValueKind.String && owner.GetString() == Owner;
    }

    /// <summary>
    /// Renders the choice file.
    /// </summary>
    /// <param name="provider">Provider name.</param>
    /// <returns>JSON text.</returns>
    public static string Write(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["Plugin"] = Owner,
                ["EpisodeSource"] = provider.Trim(),
                ["Note"] = "Metadata provider the episode list of this release is taken from. Written by the Outer Files plugin for Jellyfin; delete to use the default (TMDb).",
            },
            WriteOptions) + "\n";
    }

    /// <summary>
    /// Tells whether an episode title is a provider's placeholder for an episode it does not know
    /// (OMDb answers <c>Episode #4.1</c> for any number past the last season).
    /// </summary>
    /// <param name="title">Title.</param>
    /// <returns>True for a placeholder.</returns>
    public static bool IsPlaceholder(string? title) => title is not null && PlaceholderRegex().IsMatch(title.Trim());

    [GeneratedRegex(@"^Episode #\d+\.\d+$")]
    private static partial Regex PlaceholderRegex();
}
