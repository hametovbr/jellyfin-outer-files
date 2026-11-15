using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Xunit;

namespace Jellyfin.Plugin.OuterFiles.Tests;

/// <summary>
/// Jellyfin stores plugin configuration with <see cref="XmlSerializer"/>. A configuration that cannot be
/// reflected makes the plugin throw "There was an error reflecting type ...PluginConfiguration" and every
/// scheduled task fails before doing any work. These tests pin that contract.
/// </summary>
public sealed class PluginConfigurationTests
{
    [Fact]
    public void CanBeReflectedByXmlSerializer()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        Assert.NotNull(serializer);
    }

    [Fact]
    public void SurvivesRoundTrip()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var original = new PluginConfiguration
        {
            AudioExtensions = ".mka,.flac",
            HistoryLimit = 42,
            LastScanChanged = 3,
            LastScanTracks = 17
        };

        using var stream = new MemoryStream();
        serializer.Serialize(stream, original);
        stream.Position = 0;
        var restored = (PluginConfiguration)serializer.Deserialize(stream)!;

        Assert.Equal(original.AudioExtensions, restored.AudioExtensions);
        Assert.Equal(original.HistoryLimit, restored.HistoryLimit);
        Assert.Equal(original.LastScanChanged, restored.LastScanChanged);
        Assert.Equal(original.LastScanTracks, restored.LastScanTracks);
    }

    [Fact]
    public void ExposesNoGenericCollections()
    {
        // Generic collections of custom types are exactly what XmlSerializer fails to reflect.
        var offenders = typeof(PluginConfiguration)
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                && (p.PropertyType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>)
                    || p.PropertyType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ParsesAudioExtensions()
    {
        Assert.Equal([".mka", ".flac"], new PluginConfiguration { AudioExtensions = ".mka, .flac" }.GetAudioExtensions());
        Assert.Equal([".mka", ".flac"], new PluginConfiguration { AudioExtensions = string.Empty }.GetAudioExtensions());
    }
}