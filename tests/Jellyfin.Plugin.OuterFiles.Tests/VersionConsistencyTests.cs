using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OuterFiles.Tests;

// build.yaml (read by jprm and the release workflow) and Directory.Build.props (assemblies) must agree.
public sealed class VersionConsistencyTests
{
    [Fact]
    public void BuildYamlMatchesAssemblyVersion()
    {
        var root = FindRepositoryRoot();
        var yaml = File.ReadAllText(Path.Combine(root, "build.yaml"));
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var yamlVersion = Regex.Match(yaml, "^version:\\s*\"?([0-9.]+)", RegexOptions.Multiline).Groups[1].Value;
        var propsVersion = Regex.Match(props, "<Version>([0-9.]+)</Version>").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(yamlVersion));
        Assert.Equal(propsVersion, yamlVersion);
        Assert.Equal(propsVersion, typeof(Plugin).Assembly.GetName().Version!.ToString());
    }

    internal static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("build.yaml not found above the test folder");
    }
}
