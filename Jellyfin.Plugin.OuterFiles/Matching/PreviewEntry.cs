namespace Jellyfin.Plugin.OuterFiles.Matching;

/// <summary>
/// One row of the preview: a track that would be attached to a video.
/// </summary>
/// <param name="Path">Track path.</param>
/// <param name="IsAudio">True for audio tracks, false for subtitles.</param>
/// <param name="Language">Three letter language code or null.</param>
/// <param name="Title">Title shown in the client or null.</param>
/// <param name="IsForced">True for signs-only subtitles.</param>
public sealed record PreviewEntry(string Path, bool IsAudio, string? Language, string? Title, bool IsForced);
