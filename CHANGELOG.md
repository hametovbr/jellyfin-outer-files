# Changelog

## 0.2.0.0

Ground-up rewrite (design: docs/ARCHITECTURE.md). Requires Jellyfin 12.4.

- **Link mode** replaces the 0.1.x scanner. 0.1.x wrote `Video.SubtitleFiles`/`AudioFiles`, which Jellyfin
  overwrites on every probe and which made every scan re-probe the touched videos. Tracks from sub folders
  are now symlinked, with Jellyfin-readable names, into the item's internal metadata folder, which Jellyfin
  scans for external tracks itself. Matching uses Jellyfin's own name rule across the whole release folder
  (no episode-number heuristics). Language/studio/signs from folders and name suffixes; subtitle language
  from the text when no name says it; default audio language setting. Runs after every library scan.
- **Episode recognition**: path-unaware core expressions confined to the file name (`[2023]` in a folder
  name no longer becomes episode 2023, `(Сезоны 1-3)` no longer S1E3), rules for `[Grp]_Show_07_(..)`,
  `Show_[61_of_62]`, `Show 08 [..]`, `Show TV-2 - 10`. Installed only after a self-test; restored when
  disabled; wrongly numbered episodes are re-read after scans.
- **Organize mode**: candidates, analysis with proposals, metadata search and episode titles, absolute
  numbering, duplicates, split episodes as stacked parts, Sonarr-style templates, plan with parse-back
  validation, journaled apply, undo.
- Settings page and Organize page (dashboard menu).
- Removed: `GET /OuterFiles/Videos`, `POST /OuterFiles/Scan`, `GET /OuterFiles/Preview/{itemId}` (replaced,
  see README).

## 0.1.1.0

- Fix: the scheduled task failed with "There was an error reflecting type ...PluginConfiguration".
  Jellyfin stores plugin configuration as XML and `XmlSerializer` cannot reflect collections of custom
  types. All settings are scalars now and the scan history lives in `history.json` in the plugin folder.
- Fix: `GET /OuterFiles/Status` returned 500 for the same reason.
- Status reports inspected / changed / attached / failed counts.

## 0.1.0.0

- Initial release.
- Scheduled task that attaches audio and subtitle files from sub folders to episodes, movies and other
  videos, without modifying any media file.
- Matching by stem, by episode number, and by movie folder; language, forced (signs) and studio detected
  from folder and file names.
- Plugin page with the last scan summary, "Scan now" and the list of videos that received tracks.
- API: `GET /OuterFiles/Status`, `GET /OuterFiles/Videos`, `POST /OuterFiles/Scan`,
  `GET /OuterFiles/Preview/{itemId}`.
