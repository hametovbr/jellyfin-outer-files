using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.OuterFiles.Tests;

/// <summary>A throw-away folder tree for executor tests.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "outer-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string P(string relative) => Path.Combine(Root, relative);

    public string File(string relative, string content = "")
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Every entry below the root as "d relative" / "f relative" / "l relative -> target".</summary>
    public IReadOnlyList<string> Snapshot(params string[] exclude) =>
        Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Select(p => (p, rel: Path.GetRelativePath(Root, p)))
            .Where(x => !exclude.Any(e => x.rel.StartsWith(e, StringComparison.Ordinal)))
            .Select(x =>
            {
                var info = new FileInfo(x.p);
                if (info.LinkTarget is not null)
                {
                    return "l " + x.rel + " -> " + info.LinkTarget;
                }

                return (Directory.Exists(x.p) ? "d " : "f ") + x.rel;
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
