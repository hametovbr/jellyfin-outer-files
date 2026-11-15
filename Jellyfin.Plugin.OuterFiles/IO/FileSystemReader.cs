using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.OuterFiles.Domain.Links;
using Jellyfin.Plugin.OuterFiles.Domain.Plans;

namespace Jellyfin.Plugin.OuterFiles.IO;

/// <summary>
/// Read-only access to the file system for planners: folder snapshots, plugin-owned links, text samples.
/// </summary>
public sealed class FileSystemReader
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    static FileSystemReader()
    {
        // Russian .srt files are often Windows-1251.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Lists every file below a folder (absolute paths), skipping quarantine folders of the cleanup script.
    /// </summary>
    /// <param name="root">Folder.</param>
    /// <returns>Files; empty when the folder does not exist.</returns>
    public IReadOnlyList<string> Snapshot(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", Recursive)
            .Where(p => !p.Contains("/_cleanup/", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Lists the plugin's own links in a folder: symlinks whose target lies inside a library root
    /// (docs/ARCHITECTURE.md, P7). Real files, e.g. downloaded subtitles, are never returned.
    /// </summary>
    /// <param name="folder">Internal metadata folder of an item.</param>
    /// <param name="libraryRoots">Library roots.</param>
    /// <returns>Owned links.</returns>
    public IReadOnlyList<ExistingLink> OwnedLinks(string folder, IReadOnlyCollection<string> libraryRoots)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var result = new List<ExistingLink>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var target = new FileInfo(path).LinkTarget;
            if (target is not null && libraryRoots.Any(r => PlanValidator.IsInside(target, r)))
            {
                result.Add(new ExistingLink(Path.GetFileName(path), target));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the folders between <paramref name="stopAt"/> (exclusive) and <paramref name="folder"/>
    /// (inclusive) that do not exist, outermost first.
    /// </summary>
    /// <param name="folder">Wanted folder.</param>
    /// <param name="stopAt">An existing ancestor, e.g. the internal metadata root.</param>
    /// <returns>Missing folders.</returns>
    public IReadOnlyList<string> MissingFolders(string folder, string stopAt)
    {
        var missing = new List<string>();
        var current = folder.TrimEnd('/', '\\');
        var stop = stopAt.TrimEnd('/', '\\');
        while (!string.IsNullOrEmpty(current) && !current.Equals(stop, StringComparison.Ordinal) && !Directory.Exists(current))
        {
            missing.Insert(0, current);
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return missing;
    }

    /// <summary>
    /// Reads the beginning of a text file, decoding UTF-8 (with or without BOM) or Windows-1251.
    /// </summary>
    /// <param name="path">File.</param>
    /// <param name="maxBytes">Bytes to read.</param>
    /// <returns>Text, or empty on error.</returns>
    public string ReadTextSample(string path, int maxBytes = 64 * 1024)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[maxBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var bytes = buffer.AsSpan(0, read);
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes[2..]);
            }

            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                bytes = bytes[3..];
            }

            try
            {
                // A cut in the middle of a multi-byte character is not an encoding error: drop the tail.
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return strict.GetString(bytes[..TrimIncompleteUtf8(bytes)]);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1251).GetString(bytes);
            }
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static int TrimIncompleteUtf8(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        for (var i = 1; i <= 3 && end - i >= 0; i++)
        {
            var b = bytes[end - i];
            if ((b & 0xC0) == 0x80)
            {
                continue; // continuation byte
            }

            var need = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            return need > i ? end - i : end;
        }

        return end;
    }
}
