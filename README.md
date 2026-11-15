# Jellyfin Outer Files

A Jellyfin 12.4+ plugin for libraries filled straight from torrents.

![Outer Files](thumb.png)

| Mode | When | What it does |
|---|---|---|
| **Link** | automatically, after every library scan | Audio and subtitle files stored in sub folders of a release show up as regular external tracks, with language, studio as track title and a signs (forced) flag. Media folders are not touched. |
| **Episode recognition** | always on (can be switched off) | Release names Jellyfin misreads get correct season/episode numbers. |
| **Organize** | on demand, per release | Pick a release, check how files map to episodes, review the plan, apply. Folder, videos and every related file are renamed to one Sonarr-style scheme. Every change is journaled and can be undone. |

## Link mode

Jellyfin only finds external tracks next to the video, named like the video. Releases keep them elsewhere:

```
Show [2023][BDRip]/
├── [VCB-Studio] Show [01][Ma10p_1080p][x265_flac].mkv
├── EN Subs/[VCB-Studio] Show [01][Ma10p_1080p][x265_flac].en_CR.ass
├── RU Sound [Anilibria]/[VCB-Studio] Show [01][Ma10p_1080p][x265_flac].ru_Anilibria.mka
├── RU Sound [Anilibria]/надписи/[VCB-Studio] Show [01][Ma10p_1080p][x265_flac].ru_signs_CR.ass
├── RUS Sound/ForceMedia/…mka          ├── Sounds/Wakanim/…mka          ├── Rus Sub by A-D Team/…ass
```

The plugin matches a track to a video **by name, the way Jellyfin does for adjacent files**, but across the
whole series or movie folder: the track name equals the video name or continues it with a dot. Language,
studio and signs come from the folder chain and the name suffix (`RU Sound [Anilibria]` → rus/Anilibria,
`Rus Sub by A-D Team` → rus/A-D Team, `надписи`/`signs` → forced, `.en_signs_CR` → eng/CR/forced).
Subtitles with no language in any name are recognised from their text; audio without one gets the default
language from the settings.

For every video the plugin keeps symlinks with Jellyfin-readable names
(`<video>.Anilibria.rus.mka`, `<video>.CR.forced.eng.ass`) in the item's **internal metadata folder**
(`<config>/metadata/library/xx/<id>/`), a folder Jellyfin itself scans for external tracks. Jellyfin probes
them like any adjacent file. The links are reconciled after each scan: new tracks get links, removed tracks
lose them, nothing else is touched. Media folders stay exactly as they are.

## Episode recognition

Jellyfin parses episode numbers from the full path, so folder names leak into numbers. The plugin confines
Jellyfin's path-unaware expressions to the file name and adds rules for common release names:

| Name | Jellyfin alone | With the plugin |
|---|---|---|
| `Show [2023][BDRip]/[VCB-Studio] Show [07][…].mkv` | E2023 | E7 |
| `Show (Сезоны 1-3)/JN.16.Title.avi` | S1E3 | unnumbered (fix with organize) |
| `[Henshin]_K-ON!_07_(BD_x264_1920x1080_FLAC).mkv` | — | E7 |
| `Hunter_X_Hunter_[61_of_62]_[ru_jp].mkv` | — | E61 |
| `Show 08 [BDRip 1920x1080 x264 FLAC].mkv` | — | E8 |
| `Show TV-2 - 10 (WEB-DL 1920x1080).mkv` | E2 | S2E10 |

The rules are installed into Jellyfin's naming options at start-up **only after a self-test on the
server's own parser**; switching the setting off restores Jellyfin's rules immediately. Measured on a real
library of 4365 videos: only the intended 197 files change. After each scan, episodes still carrying a
number that is known to be wrong are refreshed so Jellyfin re-reads them.

## Organize mode

Dashboard → **Outer Files: Organize**:

1. **Releases that need attention** lists folders off the templates, with unnumbered or duplicate episodes,
   videos outside season folders or tracks in sub folders.
2. **Organize** opens the analysis: one row per video with the proposed action (episode, special, extra,
   movie, skip), season, episode and the reason. Search the metadata providers to pick the right series;
   episode titles are fetched with its ids. Absolute numbering (`136. Title`) can be converted with the
   season sizes from the metadata source. Duplicate releases of the same episode are proposed as *skip*.
   Split episodes (`JN.02a`, `JN.02b`) become parts Jellyfin stacks into one episode.
3. **Build plan** shows every move. Nothing is overwritten. Every new episode name is checked to read back
   as the intended season/episode with the server's parser.
4. **Apply** runs the plan through a journal written before each step and queues a library scan.
5. **Journal → Undo** puts everything back.

What moves with a video: adjacent subtitles/audio and `-thumb.jpg`; tracks from sub folders (next to the
video, under Jellyfin-readable names, so the organized folder needs no links); specials to `Season 00`;
NCOP/NCED/menus to `extras/`; fonts to `fonts/`; `tvshow.nfo` and artwork with the series folder.
Per-episode `.nfo` files carry the old numbers, so they go to a hidden `.outer-files-trash` folder in the
library instead (Jellyfin ignores dot folders and writes fresh ones if its nfo saver is on). Files the plan
does not understand stay where they are.

Default templates (settings page):

```
{Series Title} ({Year})/Season {season:00}/{Series Title} ({Year}) - S{season:00}E{episode:00}{-Eend:00} - {Episode Title} {Quality}
{Movie Title} ({Year})/{Movie Title} ({Year}) {Quality}
```

## Install

Dashboard → Plugins → Repositories → add
`https://raw.githubusercontent.com/hametovbr/jellyfin-outer-files/main/manifest.json`, then install
**Outer Files** from the catalog and restart Jellyfin. Built and tested against Jellyfin 12.4.

In Docker the plugin creates symlinks inside the container (`/config/metadata/…` → `/mnt/…`). They work for
Jellyfin; on the host they look dangling, which is expected. Back up `/config` with a tool that copies
symlinks as symlinks (`rsync -a` does), otherwise the backup would contain the media they point to.

## API

All endpoints require an administrator.

| Endpoint | |
|---|---|
| `GET /OuterFiles/Status` | running operation, recognition rules state, last link run |
| `GET /OuterFiles/Links/Preview/{itemId}` | desired vs existing links of one video, with reasons |
| `POST /OuterFiles/Links/Reconcile` | queue a link run |
| `GET /OuterFiles/Journal` | applied operations |
| `GET /OuterFiles/Organize/Candidates` | releases off the templates |
| `POST /OuterFiles/Organize/Analyze` `{Root}` | analysis and proposals |
| `GET /OuterFiles/Organize/Search?name=&isMovie=&year=` | metadata search |
| `POST /OuterFiles/Organize/Absolute` `{SessionId, ProviderIds}` | re-propose absolute numbers by season sizes |
| `POST /OuterFiles/Organize/Plan` | build and validate the plan |
| `POST /OuterFiles/Organize/Apply` `{SessionId, Hash}` | apply the reviewed plan |
| `POST /OuterFiles/Organize/Undo/{journalId}` | undo |

## Development

Requires the .NET 10 SDK. `make test` runs the unit, contract and file-system tests; `make package` builds
the plugin zip with jprm. The design, the Jellyfin behaviour it relies on (with file and line references)
and the rules it follows are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

Opt-in tests against a whole library listing:
`OUTER_FILES_CORPUS=listing.tsv OUTER_FILES_CORPUS_DEPTH=4 dotnet test` (track matching) and
`OUTER_FILES_VIDEOS=videos.txt dotnet test` (prints every episode number the recognition rules change).

## License

MIT
