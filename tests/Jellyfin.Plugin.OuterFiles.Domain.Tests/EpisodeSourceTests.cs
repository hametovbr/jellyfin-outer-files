using Jellyfin.Plugin.OuterFiles.Domain.Organize;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class EpisodeSourceTests
{
    [Fact]
    public void DefaultsToTmbdWhenTheSeriesHasATmdbId()
    {
        var source = EpisodeSources.Resolve(null, new Dictionary<string, string> { ["Tmdb"] = "57532", ["Tvdb"] = "272472" });
        Assert.Equal(EpisodeSources.Tmdb, source.Provider);
        Assert.Equal(EpisodeSources.FromDefault, source.Origin);
    }

    [Fact]
    public void DefaultsToAnyWhenThereIsNoTmdbId()
    {
        var source = EpisodeSources.Resolve(null, new Dictionary<string, string> { ["Tvdb"] = "272472" });
        Assert.Null(source.Provider);
        Assert.Equal(EpisodeSources.FromDefault, source.Origin);
    }

    [Fact]
    public void AStoredChoiceWinsOverTheDefault()
    {
        var text = EpisodeSources.Write("TVmaze");
        var source = EpisodeSources.Resolve(text, new Dictionary<string, string> { ["Tmdb"] = "57532" });
        Assert.Equal("TVmaze", source.Provider);
        Assert.Equal(EpisodeSources.FromFile, source.Origin);
    }

    [Fact]
    public void ForeignFilesAreNotReadAsAChoice()
    {
        Assert.Null(EpisodeSources.Read("{\"something\": \"else\"}"));
        Assert.Null(EpisodeSources.Read("not json"));
        Assert.Null(EpisodeSources.Read(null));
        Assert.Null(EpisodeSources.Read(""));
    }

    [Fact]
    public void OnlyOwnedFilesAreReplaceable()
    {
        Assert.True(EpisodeSources.IsOwned(EpisodeSources.Write("TheMovieDb")));
        Assert.False(EpisodeSources.IsOwned("{\"EpisodeSource\": \"TVmaze\"}"));
        Assert.False(EpisodeSources.IsOwned("garbage"));
    }

    [Fact]
    public void OmdbPlaceholdersAreRecognised()
    {
        Assert.True(EpisodeSources.IsPlaceholder("Episode #4.1"));
        Assert.True(EpisodeSources.IsPlaceholder(" Episode #10.12 "));
        Assert.False(EpisodeSources.IsPlaceholder("Episode 4"));
        Assert.False(EpisodeSources.IsPlaceholder("Pilot"));
        Assert.False(EpisodeSources.IsPlaceholder(null));
    }
}
