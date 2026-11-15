# Jellyfin Outer Files

A Jellyfin plugin that finds audio and subtitle files living in **sub folders** of a video folder and
links them to the video as regular external tracks.

![Outer Files](thumb.png)

Jellyfin itself only picks up external files that sit **next to** the video and whose name **starts with
the video file name**, all inside the video's own folder. Anime releases from groups such as VCB-Studio
put extra audio tracks and subtitles into sibling folders ("RU Subs", "EN Sound",
"RU Sound [Anilibria]/надписи") or into a nested folder of the video folder. Those files never reach the
library, so episodes end up with a single Japanese FLAC track and no subtitles.

This plugin walks the folder of every video, decides which track belongs to which episode, and stores the
paths so Jellyfin exposes them as ordinary external audio/subtitle streams — with language, a "Signs"
flag for signs-only subtitles and the release studio in the track title.

**Nothing about your media is modified**: no files are created, no symlinks, no remuxing, no copying. The
plugin only records which files belong to which video, exactly like Jellyfin does for a `.srt` next to an
episode.

## Supported layout

```
Series Folder/
├── [Group] Show [01][1080p][x265_flac].mkv          ← video
├── EN Subs/[Group] Show [01][1080p][x265_flac].en_CR.ass
├── RU Subs/[Group] Show [01][1080p][x265_flac].ru_CR.ass
├── EN Sound/[Group] Show [01][1080p][x265_flac].en_CR.mka
├── EN Sound/signs/[Group] Show [01][1080p][x265_flac].en_signs_CR.ass
└── RU Sound [Anilibria]/
    ├── [Group] Show [01][1080p][x265_flac].ru_Anilibria.mka
    └── надписи/[Group] Show [01][1080p][x265_flac].ru_signs_CR.ass
```

Any depth below the video folder works. Movies are handled the same way: all track files found below a
movie folder are attached to the movie.

## How tracks are matched

1. **Same stem** — a file whose name (without extension) equals or starts with the video name is
   attached, exactly like Jellyfin does it.
2. **Episode number** — for shows, a track is attached when it carries the same episode number as the
   video (`[05]`, `- 05`, `S01E05`) and does not carry a number that belongs to another episode in the
   same folder.
3. **Movies** — when the video name has no episode number, only rule 1 applies (plus the "whole folder"
   mode described below).

Language, signs and studio are read from the file name and from every folder between the video folder
and the track:

| Source | Detected as |
|---|---|
| folder `RU Subs`, `EN Sound`, `JP Subs` | language `rus` / `eng` / `jpn` |
| folder `signs`, `надписи`, name part `forced` | forced (signs only) |
| name part `.ru_Anilibria` | language `rus`, title `Anilibria` |
| name part `.en_CR` | language `eng`, no title (`CR` = complete release) |

A season folder is used as the boundary: tracks are searched below the folder that contains the video,
so `Season 1`, `Season 2` and `SPs` never mix.

## Installation

1. Download `outer-files_<version>.zip` from the [releases](../../releases) page.
2. Copy it into the Jellyfin plugin folder and restart the server:
   - Docker (`jellyfin/jellyfin`): `<data>/plugins/outer-files_<version>/`, where `<data>` is the mounted `/config`
   - Linux: `~/.local/share/jellyfin/plugins/outer-files_<version>/`
3. Open **Dashboard → Plugins** and check that **Outer Files** is listed as `Active`.

### From a plugin repository

The repository contains `manifest.json`, so it can be added like any other third-party source:
**Dashboard → Plugins → Repositories → +**, paste

```
https://raw.githubusercontent.com/hametovbr/jellyfin-outer-files/main/manifest.json
```

then install **Outer Files** from **Dashboard → Plugins → Catalog**. The manifest points at the zip asset
of the GitHub release and carries its MD5, so Jellyfin can install and later update the plugin by itself.

## Usage

The plugin ships the scheduled task **Attach outer audio and subtitle files** (*Dashboard → Scheduled
Tasks → Library*). It walks every episode, movie and video in the library and updates the track lists.

- **Scan now** on the plugin page runs the same scan immediately.
- The page shows the last scan summary and the videos that received tracks.
- `GET /OuterFiles/Preview/{itemId}` lists what *would* be attached to a single video without changing
  anything. Useful to debug one series.
- After the scan, Jellyfin shows the new tracks on the next probe of the video (playback or a metadata
  refresh of that item). Existing streams are not duplicated: the plugin writes the same lists Jellyfin
  keeps for adjacent files, so a repeated scan is a no-op.

## Limitations

- Files are discovered, not transcribed: audio tracks are separate files (`.mka`, `.flac`, …) and
  subtitles external (`.ass`, `.srt`, `.ssa`, `.vtt`, …). Tracks *inside* the `.mkv` are Jellyfin's own
  business and unchanged.
- The plugin cannot change how Jellyfin *names* an episode or assigns season numbers. If a release folder
  name confuses the core parser (for example a folder with `[2023]` where every episode ends up with
  `IndexNumber=2023`), fix the folder name or use Jellyfin's "Edit metadata" — the plugin will attach the
  tracks once the episode numbering is correct.
- Audio tracks are external streams, so a client that does not support file-based external audio streams
  will not list them. Subtitles behave like any other external `.ass`/`.srt`.
- Only the local file protocol is supported (no remote/cloud sources).

## Building

Requires .NET SDK 10 and Python 3 with `jprm`:

```bash
dotnet test tests/Jellyfin.Plugin.OuterFiles.Tests/Jellyfin.Plugin.OuterFiles.Tests.csproj
make package VERSION=0.1.0.0     # → artifacts/outer-files_0.1.0.0.zip
```

The `Release` workflow builds the zip and attaches it to a GitHub release when a `v*` tag is pushed.

## Licence

MIT. See [LICENSE](LICENSE).