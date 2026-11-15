using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.OuterFiles.Configuration;

namespace Jellyfin.Plugin.OuterFiles.Tests;

// Jellyfin persists plugin configuration with XmlSerializer. 0.1.0 broke because a List<CustomType>
// could not be reflected; these tests keep the configuration serializer-friendly.
public sealed class PluginConfigurationTests
{
    [Fact]
    public void CanBeReflectedByXmlSerializer() => _ = new XmlSerializer(typeof(PluginConfiguration));

    [Fact]
    public void SurvivesRoundTrip()
    {
        var original = new PluginConfiguration
        {
            EnableLinks = false,
            EnableNamingRules = false,
            DefaultAudioLanguage = "jpn",
            EpisodeFileTemplate = "{Series Title} - S{season:00}E{episode:00}",
        };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new StringWriter();
        serializer.Serialize(buffer, original);
        using var reader = new StringReader(buffer.ToString());
        var copy = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.False(copy.EnableLinks);
        Assert.False(copy.EnableNamingRules);
        Assert.Equal("jpn", copy.DefaultAudioLanguage);
        Assert.Equal(original.EpisodeFileTemplate, copy.EpisodeFileTemplate);
    }

    [Fact]
    public void ExposesOnlyScalars()
    {
        var offending = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string) && !p.PropertyType.IsValueType && !p.PropertyType.IsEnum)
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(offending);
    }
}
