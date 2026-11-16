# Changelog

## 0.4.6.0

- **Match by episode titles, double files**: `SxxEyy-Ezz - A + B` is matched half by half; when both halves
  are neighbouring episodes of the source (in either order) the file takes that range. A half the source
  titles differently is accepted when the neighbour resembles it. Halves that are not neighbours are not
  guessed (skip, with the reason).
- **Match by episode titles, own numbers**: a file's own `SxxEyy` is kept for an unknown title only when the
  release is numbered like the source (at least 4 of 5 matched titles sit on their own numbers). On a
  release in another order (Paw Patrol: TVDB against TMDb) the fallback put files on other episodes.
- A second copy with a near-exact title shares the episode and is resolved as a duplicate, instead of being
  reported as "title not found".
- **Plan**: `tvshow.nfo`/artwork that the target folder already has stays there; the release's own copy goes
  to the trash (was a `target-exists` conflict after an undo that kept the folder).

## 0.4.5.0

- **Plan: numbering check.** The episode title in a file name is compared with the source's title for the
  planned number and with the other planned episodes. Titles that fit another episode clearly better are
  listed (`TitleMismatches`); when they are a third or more of the titled files, the plan has a
  `numbering-mismatch` conflict. Found on Paw Patrol: Sonarr had numbered the files in TVDB order, the plan
  used TMDb, and files were renamed with the titles of other episodes
  (`S01E25 - Pups Save a Goodway` became `S01E25 - Pups Turn On the Lights`).
- **Duplicates**: a double file (`S10E25-E26`) makes single copies of its episodes (`S10E25`, `S10E26`)
  duplicates, in proposals and as a `duplicate-episode` conflict; before, only copies starting at the same
  number were compared.

## 0.4.4.0

- **Organize list**: a folder that an earlier organize run moved files out of, and that still holds videos
  (the skipped duplicate copies), is listed as "leftovers of a release organized earlier". Before, such a
  folder was listed only if it had another problem, so well-named leftovers (Paw Patrol: 8 copies in season
  folders) stayed in the library as a second series with no way to reach them from the page.

## 0.4.3.0

- **Organize: move leftovers to the trash** (checkbox on the organize page, `TrashLeftovers` in
  `POST /OuterFiles/Organize/Plan`): everything the plan would leave in the old folder (skipped duplicate
  copies with their tracks, scans, old nfo and artwork) goes to `<library>/.outer-files-trash/…` as well, and
  the emptied old folder is removed. Without it, the old folder stays in the library as a second series with
  the skipped copies (seen on K-ON!, JUJUTSU KAISEN, Paw Patrol). Undo puts everything back; the trash folder
  is ignored by Jellyfin and can be emptied by hand once the result is checked.

## 0.4.2.0

- **Match by episode titles**: a file named `SxxEyy` (season 1 and later) whose title the source does not
  know keeps its own number when the source has that episode and no title match took it. Before, it was
  skipped: on K-ON! the TVDB titles of Sonarr's files ("Summer Classes!", "No Music Room!", "Exams") differ
  from TMDb's, and three episodes of season 2 stayed behind in the old folder. Double files
  (`S01E25-E26 - A + B`) keep their numbers the same way.
- **Plan**: an episode that the target folder already holds is an `episode-exists` conflict, so a second
  run over the leftovers of a release cannot add a second copy.

## 0.4.1.0

- **Plan**: two files planned as the same episode (rows edited by hand or through the API) are a
  `duplicate-episode` conflict naming both files and their track counts, instead of a second copy named
  `… (2)`. Seen on K-ON!: Henshin `_13_` set to the special "Winter Days!" next to Henshin `sp_01`.
- **Match by episode titles**: a file without a title that carries the same number as a titled copy follows
  that copy (Henshin `sp_01` with Sonarr's `S00E01 - URA-ON! …`, which TMDb numbers S00E02; Henshin `_13_`
  with `S01E13 - Winter Days!`, TMDb S00E01), and the better of the two is kept as usual.

## 0.4.0.0

- **Match by episode titles** (organize page, `POST /OuterFiles/Organize/Titles`): for releases that number
  episodes their own way. The title is read from each file name (after `SxxEyy` or the release's own number,
  without quality tags; `(Часть 1)`/`Part 2` become parts) and matched against every episode and special of
  the chosen series, in the title language and, for Cyrillic names, in Russian. Each episode is taken once
  (parts of one episode share it); a match needs a clear lead. Files whose title the source does not know
  are proposed as skip with the reason; files without a title keep the usual proposal. Checked on the
  Jimmy Neutron release (`JN.02a/02b` are separate TMDb episodes): 76 of 84 files match exactly, the other 8
  are TV specials found in season 0.

## 0.3.1.0

Fixes from the first organize runs on a real library:

- **Duplicates**: of two copies of one episode, organize keeps the one with more attached tracks (a BD
  release with Russian dub and subtitles over a bare WEB-DL), then the higher resolution. Before, the
  bare copy could win and the tracks stayed behind with the skipped copy (K-ON!, JUJUTSU KAISEN).
- **Wrong Jellyfin match**: when the metadata entry picked in organize contradicts the one Jellyfin matched
  (K-ON! identified as "K-Pop ON!"), the old `tvshow.nfo`/`movie.nfo` and artwork go to the trash folder
  instead of moving along, so Jellyfin identifies the new folder from its name.
- **Numbers that contradict the file name**: the status lists episodes named `SxxEyy` that the library
  holds under another number (seen: Hetalia `S01E27` stored as episode 1, from a metadata provider or a
  local nfo). Reported only, a refresh would not change them.

## 0.3.0.0

- **Fonts** (setting, off by default): fonts shipped in release `fonts/` folders (.ttf, .otf, .woff, .woff2)
  are linked into one plugin folder registered as Jellyfin's fallback font folder, so styled `.ass`
  subtitles render with their fonts in the web player. Used only when that Jellyfin setting is empty or
  already points to the plugin; Jellyfin loads at most 20 MiB, larger sets are reported.
- **Episode re-numbering**: episodes with numbers the rules cannot replace (`NCOP1`, `JN.16`) are cleared
  instead of keeping a wrong number; episodes that already carry the right number are left alone. A local
  `.nfo` with the old number is detected: the library is fixed, and the status lists those nfo files, which
  bring the old number back when the video changes unless the library saves metadata into media folders
  (then Jellyfin rewrites them).
- **Organize**: adjacent subtitles and audio get the same normalized names as tracks from sub folders;
  releases whose tracks carry tool-generated names are listed; a release that ships the same fonts per
  subtitle group keeps one copy (the rest goes to the trash folder); a readable title is proposed when
  Jellyfin only knows the raw release name.
- **Undo**: folders Jellyfin wrote its own metadata into after organizing (nfo, thumbnails) are kept and
  reported instead of stopping the undo; an interrupted run can be retried, finished moves are skipped.
- Settings page shows the font state; the organize table is easier to read.

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
