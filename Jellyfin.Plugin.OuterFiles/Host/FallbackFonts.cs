using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.OuterFiles.Domain.Fonts;
using Jellyfin.Plugin.OuterFiles.Domain.Links;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.OuterFiles.Host;

/// <summary>
/// The plugin's fallback font folder (<c>&lt;data&gt;/outer-files/fonts</c>, links to fonts inside releases)
/// and its registration as Jellyfin's fallback font folder. A folder the administrator set up is never
/// replaced.
/// </summary>
public class FallbackFonts
{
    private readonly IServerConfigurationManager? _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackFonts"/> class.
    /// </summary>
    /// <param name="paths">Server paths.</param>
    /// <param name="config">Server configuration.</param>
    public FallbackFonts(IServerApplicationPaths paths, IServerConfigurationManager config)
        : this(Path.Combine(paths.DataPath, "outer-files", "fonts"), config)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackFonts"/> class.
    /// </summary>
    /// <param name="folder">Font folder.</param>
    /// <param name="config">Server configuration, or null in tests.</param>
    protected FallbackFonts(string folder, IServerConfigurationManager? config)
    {
        Folder = folder;
        _config = config;
    }

    /// <summary>Gets the folder that holds the font links.</summary>
    public string Folder { get; }

    /// <summary>
    /// Registers or unregisters the folder with Jellyfin after the links were reconciled.
    /// </summary>
    /// <param name="enabled">Setting.</param>
    /// <param name="fonts">Fonts linked now.</param>
    /// <returns>The state for the status page.</returns>
    public virtual FontReport Apply(bool enabled, IReadOnlyList<DesiredLink> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        var bytes = fonts.Sum(f => File.Exists(f.Target) ? new FileInfo(f.Target).Length : 0);
        if (_config is null)
        {
            return new FontReport(enabled, fonts.Count, bytes, "not registered (no server)");
        }

        var options = _config.GetEncodingOptions();
        var ours = string.Equals(options.FallbackFontPath?.TrimEnd('/', '\\'), Folder, StringComparison.Ordinal);
        string state;
        if (enabled)
        {
            if (string.IsNullOrEmpty(options.FallbackFontPath) || ours)
            {
                if (!ours || !options.EnableFallbackFont)
                {
                    options.FallbackFontPath = Folder;
                    options.EnableFallbackFont = true;
                    _config.SaveConfiguration("encoding", options);
                }

                state = bytes > FontCollector.JellyfinLimitBytes
                    ? $"registered; {bytes / 1048576} MiB exceeds Jellyfin's 20 MiB limit, the largest fonts are not sent"
                    : "registered as Jellyfin's fallback font folder";
            }
            else
            {
                state = "not registered: Jellyfin already uses another fallback font folder (" + options.FallbackFontPath + ")";
            }
        }
        else
        {
            if (ours)
            {
                options.FallbackFontPath = string.Empty;
                options.EnableFallbackFont = false;
                _config.SaveConfiguration("encoding", options);
            }

            state = "off";
        }

        return new FontReport(enabled, fonts.Count, bytes, state);
    }
}

/// <summary>
/// Fallback font state.
/// </summary>
/// <param name="Enabled">Setting.</param>
/// <param name="Fonts">Fonts linked.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="State">Human readable state.</param>
public sealed record FontReport(bool Enabled, int Fonts, long Bytes, string State);
