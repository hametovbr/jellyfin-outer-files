using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OuterFiles.Configuration;
using Jellyfin.Plugin.OuterFiles.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OuterFiles;

/// <summary>
/// Finds audio and subtitle files that live in sub folders of a video folder and links them to the video.
/// </summary>
/// <remarks>
/// Jellyfin only looks at files that sit next to the video and start with the video file name. Release groups keep
/// extra tracks in sibling folders ("RU Subs", "EN Sound", "RU Sound [Anilibria]/надписи") or carry extra track
/// files inside the video folder itself. This scanner walks the whole folder of every video, matches tracks to
/// episodes by number, then writes the paths into <see cref="Video.SubtitleFiles"/> and <see cref="Video.AudioFiles"/>.
/// Jellyfin turns those files into regular external subtitle/audio streams the next time the video is probed, so
/// nothing in the media files is changed and no extra files are created.
/// </remarks>
public sealed class OuterFileScanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly OuterFileMatcher _matcher;
    private readonly ILogger<OuterFileScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OuterFileScanner"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaEncoder">Media encoder, used to probe external files.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="matcher">Matcher deciding which track belongs to which video.</param>
    /// <param name="logger">Logger.</param>
    public OuterFileScanner(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IFileSystem fileSystem,
        OuterFileMatcher matcher,
        ILogger<OuterFileScanner> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _matcher = matcher;
        _logger = logger;
    }

    /// <summary>
    /// Runs a scan over the whole library.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="progress">Progress in percent, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of the scan.</returns>
    public async Task<ScanResult> ScanAsync(PluginConfiguration config, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var videos = GetVideos();
        _logger.LogInformation("Outer files: {Count} videos to inspect", videos.Count);

        var changed = 0;
        var tracks = 0;
        var errors = 0;
        var history = new List<VideoOuterFiles>();
        var index = 0;

        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            try
            {
                var summary = await AttachAsync(video, config, cancellationToken).ConfigureAwait(false);
                if (summary is not null)
                {
                    changed++;
                    tracks += summary.TrackCount;
                    history.Add(summary);
                }
            }
#pragma warning disable CA1031 // A single broken file must not stop the scan.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                errors++;
                _logger.LogError(ex, "Outer files: failed on {Path}", video.Path);
            }

            if (progress is not null && index % 25 == 0)
            {
                progress.Report(index * 100.0 / Math.Max(1, videos.Count));
            }
        }

        config.LastScanUtc = DateTime.UtcNow;
        config.LastScanChanged = changed;
        config.LastScanTracks = tracks;
        config.Videos = history.Take(Math.Max(1, config.HistoryLimit)).ToList();
        Plugin.Instance?.SaveConfiguration();
        progress?.Report(100);

        _logger.LogInformation("Outer files: done, {Changed} videos changed, {Tracks} tracks, {Errors} errors", changed, tracks, errors);
        return new ScanResult(videos.Count, changed, tracks, errors);
    }

    /// <summary>
    /// Attaches outer files to a single video.
    /// </summary>
    /// <param name="item">Video item.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary when tracks were attached, null when nothing was found.</returns>
    public async Task<VideoOuterFiles?> AttachAsync(BaseItem item, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(config);

        if (item is not Video video
            || !video.IsFileProtocol
            || string.IsNullOrEmpty(video.Path)
            || video.VideoType is not (VideoType.VideoFile or VideoType.BluRay or VideoType.Dvd))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(video.Path);
        if (string.IsNullOrEmpty(folder) || !_fileSystem.DirectoryExists(folder))
        {
            return null;
        }

        var candidates = FindCandidates(folder, config);
        if (candidates.Count == 0)
        {
            return null;
        }

        var matched = _matcher.Match(new VideoCandidate(video.Path), candidates, path => Path.GetRelativePath(folder, path));
        if (matched.Count == 0)
        {
            return null;
        }

        var knownSubtitles = new HashSet<string>(video.SubtitleFiles ?? [], StringComparer.OrdinalIgnoreCase);
        var knownAudio = new HashSet<string>(video.AudioFiles ?? [], StringComparer.OrdinalIgnoreCase);

        var subtitleStreams = new List<MediaStream>();
        foreach (var track in matched.Where(m => !m.Track.IsAudio).OrderBy(m => m.Track.Path, StringComparer.Ordinal))
        {
            var stream = await ProbeAsync(track, config, cancellationToken).ConfigureAwait(false);
            if (stream is null || stream.Type != MediaStreamType.Subtitle)
            {
                continue;
            }

            subtitleStreams.Add(track.Adjust(stream) ?? stream);
        }

        var audioStreams = new List<MediaStream>();
        foreach (var track in matched.Where(m => m.Track.IsAudio).OrderBy(m => m.Track.Path, StringComparer.Ordinal))
        {
            var stream = await ProbeAsync(track, config, cancellationToken).ConfigureAwait(false);
            if (stream is null || stream.Type != MediaStreamType.Audio)
            {
                continue;
            }

            audioStreams.Add(track.Adjust(stream) ?? stream);
        }

        var subtitlePaths = subtitleStreams.Select(s => s.Path!).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        var audioPaths = audioStreams.Select(s => s.Path!).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();

        // Only the paths are persisted; Jellyfin turns them into external streams when it probes the video.
        var newSubtitles = subtitlePaths.Where(p => !knownSubtitles.Contains(p)).Count();
        var newAudio = audioPaths.Where(p => !knownAudio.Contains(p)).Count();
        var pathsChanged = !knownSubtitles.SetEquals(subtitlePaths) || !knownAudio.SetEquals(audioPaths);

        if (!pathsChanged)
        {
            return null;
        }

        video.SubtitleFiles = subtitlePaths.ToArray();
        video.AudioFiles = audioPaths.ToArray();
        await _libraryManager.UpdateItemAsync(video, video.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Outer files: {Path}: {Sub} subtitles (+{NewSub}), {Audio} audio (+{NewAudio})",
            video.Path,
            subtitlePaths.Count,
            newSubtitles,
            audioPaths.Count,
            newAudio);

        return new VideoOuterFiles(video.Id, video.Path, subtitlePaths.Count + audioPaths.Count, DateTime.UtcNow);
    }

    /// <summary>
    /// Lists the tracks that would be attached to a single video, without changing anything.
    /// </summary>
    /// <param name="itemId">Video item id.</param>
    /// <returns>Matches, or null when the item is not a video of this library.</returns>
    public IReadOnlyList<PreviewEntry>? Preview(Guid itemId)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId);
        if (item is not Video video || !video.IsFileProtocol || string.IsNullOrEmpty(video.Path))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(video.Path);
        if (string.IsNullOrEmpty(folder) || !_fileSystem.DirectoryExists(folder))
        {
            return [];
        }

        var config = Plugin.Instance!.Configuration;
        var candidates = FindCandidates(folder, config);
        var matched = _matcher.Match(new VideoCandidate(video.Path), candidates, path => Path.GetRelativePath(folder, path));
        return matched
            .Select(m => new PreviewEntry(m.Track.Path, m.Track.IsAudio, m.Language, m.Title, m.IsForced))
            .ToList();
    }

    private IReadOnlyList<BaseItem> GetVideos()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video],
            IsVirtualItem = false,
            Recursive = true,
        });

    private List<TrackCandidate> FindCandidates(string folder, PluginConfiguration config)
    {
        var subtitles = new HashSet<string>(OuterFileMatcher.SubtitleExtensions, StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(config.AudioExtensions, StringComparer.OrdinalIgnoreCase);
        var result = new List<TrackCandidate>();

        IEnumerable<FileSystemMetadata> files;
        try
        {
            files = _fileSystem.GetFiles(folder, true);
        }
        catch (DirectoryNotFoundException)
        {
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Outer files: cannot read {Folder}", folder);
            return result;
        }

        foreach (var file in files)
        {
            if (file.IsDirectory || string.IsNullOrEmpty(file.FullName))
            {
                continue;
            }

            var extension = Path.GetExtension(file.FullName);
            if (subtitles.Contains(extension))
            {
                result.Add(new TrackCandidate(file.FullName, false));
            }
            else if (audio.Contains(extension))
            {
                result.Add(new TrackCandidate(file.FullName, true));
            }
        }

        return result;
    }

    private async Task<MediaStream?> ProbeAsync(MatchedTrack track, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var type = track.Track.IsAudio ? DlnaProfileType.Audio : DlnaProfileType.Subtitle;
        try
        {
            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaType = type,
                    MediaSource = new MediaSourceInfo
                    {
                        Path = track.Track.Path,
                        Protocol = MediaProtocol.File,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            var stream = info?.MediaStreams?.FirstOrDefault(s => s.Type == (track.Track.IsAudio ? MediaStreamType.Audio : MediaStreamType.Subtitle));
            if (stream is null)
            {
                return null;
            }

            stream.Path = track.Track.Path;
            stream.IsExternal = true;
            stream.IsForced = track.IsForced || stream.IsForced;
            if (!string.IsNullOrEmpty(track.Language))
            {
                stream.Language = track.Language;
            }

            if (!string.IsNullOrEmpty(track.Title))
            {
                stream.Title = track.Title;
            }

            return stream;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Outer files: cannot probe {Path}", track.Track.Path);
            return null;
        }
    }
}

/// <summary>
/// Summary of one scan.
/// </summary>
/// <param name="VideosScanned">Videos inspected.</param>
/// <param name="VideosChanged">Videos whose outer file list changed.</param>
/// <param name="AttachedTracks">Tracks attached.</param>
/// <param name="Errors">Videos that failed.</param>
public sealed record ScanResult(int VideosScanned, int VideosChanged, int AttachedTracks, int Errors);

/// <summary>
/// A video that received outer files.
/// </summary>
/// <param name="ItemId">Jellyfin item id.</param>
/// <param name="Path">File path.</param>
/// <param name="TrackCount">Number of attached tracks.</param>
/// <param name="ScannedAt">When it was attached.</param>
public sealed record VideoOuterFiles(Guid ItemId, string Path, int TrackCount, DateTime ScannedAt);