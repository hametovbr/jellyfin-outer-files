# Outer Files — target architecture (v0.2+ rewrite)

Status: implemented in 0.2.0 (2026-11-16); section 12 lists what changed during implementation. Supersedes the 0.1.x design (scanner that writes `Video.SubtitleFiles`).
Every statement about Jellyfin below was checked against the `v12.4` tag of `jellyfin/jellyfin`;
file:line references point there.

## 1. What the plugin must do

1. **Link mode (default, automatic).** Make Jellyfin show audio and subtitle tracks that live in sub
   folders or sibling folders of a release, with the right language, title (studio) and forced flag.
   Never touch media files.
2. **Recognition.** Episodes must get correct season/episode numbers **without** any fix in Jellyfin
   itself. Known failures on the owner's library:
   - year in brackets in the folder name (`Show [2023][BDRip]/[Group] Show [01][…].mkv` → episode 2023);
   - names Jellyfin does not parse at all (`[Henshin]_K-ON!_07_(…)`, `Show_[61_of_62]_…`,
     `Show 08 [BDRip …]`);
   - names it parses wrong (`Show TV-2 - 10 (…)` → E2, `136. Title` → S1E36,
     `JN.16.Title` inside `(Сезоны 1-3)` → S1E3 for every file).
3. **Organize mode (manual, per folder, on demand).** Identify a release against metadata sources,
   map files to episodes/movie, and rename folder, video and every related file (tracks, `.nfo`,
   `-thumb.jpg`, fonts, extras) to one template. Watched state may be lost; that is accepted.

## 2. Facts about the host that shape the design

| Fact | Where | Consequence |
|---|---|---|
| Probing **overwrites** `Video.SubtitleFiles` / `AudioFiles` with what the core resolver finds | `MediaBrowser.Providers/MediaInfo/FFProbeVideoInfo.cs:575,595` | These fields are outputs. Writing them is useless. |
| A scan re-probes when `SubtitleFiles`/`AudioFiles` differ from the files the core finds | `MediaBrowser.Providers/MediaInfo/ProbeProvider.cs:155-167` | Writing them also causes a re-probe loop. |
| The core looks for external tracks in the video folder **and** in the item's internal metadata folder; name = video stem + optional `.flags` | `MediaInfoResolver.cs:204-260`, `BaseItem.cs:944-961` | A correctly named symlink in the internal folder is a native external track. |
| Flags after the stem: language = first recognised token from the end; `forced/foreign`, `default`, `hi/cc/sdh` are flags (substring match for forced/default); everything else becomes the title | `Emby.Naming/ExternalFiles/ExternalPathParser.cs` | We control language/title/forced through the link name alone. |
| Downloaded subtitles are stored in the same internal folder | `SubtitleManager.cs:236` | Our links must be recognisable as ours (ownership rule). |
| Internal folders are deleted with the item | `LibraryManager.cs:360-380` | Links clean themselves up when an item disappears. |
| Episode numbers come from `EpisodeResolver.Resolve(episode.Path …)` over the **full path** | `LibraryManager.cs:3305-3330` | Folder names leak into numbers. |
| "Replace all metadata" re-parses the path (`forceRefresh`) | `Entities/TV/Episode.cs:371` | Any manual number fix is lost on replace-all, unless the item is locked. |
| `NamingOptions` is a DI singleton; `EpisodeExpressions` has a public setter and is read on every parse | `ApplicationHost.cs:578`, `EpisodePathParser.cs:53`, `SeriesPathParser.cs:21` | The plugin can change episode recognition for every code path at once. |
| Plugin types implementing `ILibraryPostScanTask`, `IItemResolver`, `IMetadataProvider` are discovered automatically | `ApplicationHost.cs:732-740` | No registration hacks needed. |
| `IProviderManager.QueueRefresh` exists | `IProviderManager.cs:36` | Re-probing goes through the host's own queue. |

## 3. Architectural patterns to follow

**P1. Work with the host, not around it.** Use the extension points Jellyfin already has: sidecar naming,
the internal metadata folder, the post-scan task hook, scheduled tasks, the refresh queue, provider
search and identify. If something seems to need a write into a core field, assume it is the wrong lever.

**P2. Hexagonal layout: pure domain, thin adapters.** All decisions (name parsing, track description,
matching, link plans, rename plans, template rendering) live in a `Domain` assembly that has **no**
reference to Jellyfin or the file system. Adapters translate between Jellyfin/FS and domain types.
This is what makes the logic testable against a real library layout in milliseconds.

**P3. Functional core, imperative shell.** Domain functions take a snapshot (`Inventory`) and return a
`Plan` (immutable list of operations plus reasons). Only executors touch the world.

**P4. Plan → Review → Apply → Undo.** Every change to the file system is planned first, can be shown as
a dry run, is applied by one executor that writes a journal **before** each step (write-ahead), and can
be undone from the journal. This is the same model as `media_cleanup.py`, which already proved itself.

**P5. Reconciliation for derived state.** Link mode is declarative: compute the desired set of links,
diff it with the links that exist, create/remove the difference. Running it twice changes nothing.

**P6. The file system is the source of truth.** The plugin keeps no state that Jellyfin depends on.
Decisions in organize mode are materialised as file names; links are derived and can be rebuilt at any
time. Plugin data (journals, last report) is history, never input.

**P7. Explicit ownership.** The plugin only modifies things it can prove are its own: symlinks whose
target is inside a library root, inside an internal metadata folder; journal entries it wrote.

**P8. Single writer.** One operation gate serialises link reconciliation and organize apply/undo, so
they never race each other or themselves.

**P9. Long work runs in the background.** Scans and applies run as scheduled tasks or via the task
manager; HTTP endpoints only start work and report status.

**P10. Explainable decisions.** Every match, rejection, language guess and target name carries a reason
code. Preview/plan views show the reasons; tests assert them.

**P11. Verify host behaviour with the host's code.** Tests that depend on Jellyfin behaviour run the real
`Emby.Naming` code (NuGet `Jellyfin.Naming`) instead of re-implementing it from memory.

**P12. Composition root + constructor injection.** All wiring in `PluginServiceRegistrator`; the static
`Plugin.Instance` is used only by Jellyfin-facing glue (configuration access), never in services.

## 4. Anti-patterns to avoid (each one already happened in 0.1.x)

| Anti-pattern | How it showed up | Rule |
|---|---|---|
| Writing host-derived fields | `SubtitleFiles`/`AudioFiles` written via `UpdateItemAsync` | P1. Never write a field the host recomputes. |
| Assuming host behaviour | parser "verified" on bare file names, while the server parses full paths | P11. Test against real host code with realistic inputs. |
| Heuristics where structure exists | episode-number matching of tracks; every one of 362 sub-folder tracks in the real library matches by stem | Prefer structural identity (stem) over parsed numbers. |
| Expensive work in a hot path | ffprobe of every candidate on every run, before comparing anything | Never probe in the plugin; the host probes once, after a refresh. |
| Work inside HTTP requests | `POST /OuterFiles/Scan` runs the whole scan synchronously | P9. |
| Exploratory writes against a live library | empty `POST /Items/{id}` wiped an episode; empty `POST /Repositories` wiped sources | The plugin never calls generic item-update endpoints; every write is planned, journaled and confirmed. |
| Destructive changes without a way back | — | P4: quarantine/rename only, never delete media; journal + undo. |
| God class | `OuterFileScanner` walks, matches, probes, persists and keeps history | One responsibility per component (section 5). |
| Swallowed errors | history writer `catch (Exception) {}` | Errors are data: collected per item into the report and logged once. |
| Hidden plugin state the library depends on | (would appear with a "number override" database) | P6. |
| Static service locator | `Plugin.Instance!.Configuration` deep inside the scanner | P12. |
| Over-broad global mutation | — | One deliberate exception, fenced (section 6.4). |

## 5. Target structure

```
Jellyfin.Plugin.OuterFiles.Domain/          pure .NET, no Jellyfin, no IO
  Model/          MediaPath, VideoFile, TrackFile, RelatedFile, ReleaseFolder, Inventory,
                  EpisodeRef, TrackDescriptor, Reason
  Naming/         ReleaseNameParser       our parser for release names → EpisodeRef + reason
                  NamingRules             the episode expressions we add to Jellyfin (6.4)
                  Template, TemplateRenderer   target names (Sonarr-style tokens)
  Tracks/         FolderVocabulary        tokens for type/language/studio/signs folders
                  TrackDescriber          language/studio/forced from folder chain + suffix
                  TextLanguageGuesser     language of .srt/.ass by script (Cyrillic, Latin, CJK…)
  Matching/       StemMatcher             track ↔ video by stem within a release root
  Links/          LinkNamer, LinkPlanner  desired link set for a video
  Organize/       InventoryBuilder (pure part), EpisodeMapper, RenamePlanner, PlanValidator
  Plans/          Plan, Operation (Mkdir, Move, Symlink, Unlink, RemoveEmptyDir), Conflict

Jellyfin.Plugin.OuterFiles/                 the plugin (adapters + application services)
  Composition/    PluginServiceRegistrator, Plugin, PluginConfiguration (scalars only)
  Host/           LibraryGateway          videos, series/movie roots, internal metadata paths
                  RefreshGateway          QueueRefresh wrapper
                  MetadataGateway         remote search/identify, episode lists (IProviderManager)
                  NamingAdapter           installs NamingRules into NamingOptions + self-test
  IO/             FileSystemReader        snapshot of a release folder (one walk, cached per run)
                  PlanExecutor            applies Operations, write-ahead journal, undo
                  JournalStore            data/journals/<operationId>.jsonl
  Services/       OperationGate           single-flight lock
                  LinkReconciler          mode 1
                  OrganizeService         mode 2 sessions: analyze → identify → map → plan → apply/undo
                  ReportStore             last run report (diagnostics only)
  Tasks/          LinkPostScanTask (ILibraryPostScanTask), LinkScheduledTask, UndoTask (none: undo is API)
  Api/            OuterFilesController    thin: status, preview, organize sessions, undo
  Web/            configPage.html (settings), organizePage.html (organize UI)

tests/
  Domain.Tests                fast unit tests + golden corpus
  Plugin.Tests                contract tests with real Jellyfin.Naming, executor tests on a temp tree
```

Dependencies only point inwards: `Plugin → Domain`. `Domain` references nothing but the BCL.

## 6. Mode 1 — link reconciliation

### 6.1 Flow
```
library scan finishes
  → LinkPostScanTask (also: scheduled task, POST /OuterFiles/Links/Reconcile)
  → OperationGate.Run("links")
  → LibraryGateway: all file-based videos, grouped by release root
  → FileSystemReader: one recursive snapshot per release root
  → for each video: StemMatcher → TrackDescriber → LinkPlanner (desired links)
  → diff with existing own links in item.GetInternalMetadataPath()
  → PlanExecutor: create/remove symlinks (journaled)
  → RefreshGateway.QueueRefresh(item) for items whose link set changed
  → host re-probes → tracks appear as normal external streams
```

### 6.2 Matching rule
A track belongs to a video when, inside the same **release root**, the track file name (without
extension) equals the video stem or continues it with `.`. The release root is the series folder for
episodes and the movie folder for movies; the search covers that whole tree (one snapshot per root, not
one walk per video). A track that matches no video goes to the report as "unmatched", with nothing
guessed. Tracks already next to their video are left to the host.

### 6.3 Link names and ownership
`<video stem>[.<studio>][.forced].<lang>.<ext>` in the item's internal metadata folder.
- Language is last, because the host takes the first language token from the end.
- Studio/title is sanitised: no dots; tokens that the host reads as flags (`default`, `forced`,
  `foreign` as substrings; `hi`, `cc`, `sdh` as whole tokens) and tokens that are language codes are
  rewritten.
- Collision (two tracks producing the same name) → numeric disambiguator inside the title.
- Ownership: a link is ours if it is a symlink whose target is under a configured library root.
  Real files in that folder (downloaded subtitles) are never touched.

### 6.4 Recognition: NamingAdapter (the one deliberate global change)
At plugin start the adapter takes the `NamingOptions` singleton and installs a new `EpisodeExpressions`
array: Jellyfin's own expressions, with the anime expression replaced by a file-name-anchored version
and a few release-name expressions added (`_NN_`, `[NN_of_MM]`, `Name NN [tags]`, `Name TV-N - NN`).
Fences:
- a **self-test** runs before installing: a fixed table of real paths (Sonarr names, scene names,
  anime names from the owner's library) must parse to the expected S/E with the new array, and every
  name that parsed correctly before must parse identically; on any mismatch nothing is installed and the
  status page says why;
- the original array is kept and restored when the feature is switched off;
- the setting is on by default, can be disabled on the settings page.

Names that no generic rule can fix (`136. Title` as absolute numbering, `JN.02a`) are **not** patched;
they are reported as "needs organizing" and fixed for good by organize mode.

## 7. Mode 2 — organize sessions

### 7.1 Flow
```
UI: pick a folder flagged by the analyzer (or any folder)
  1. Analyze   Inventory of the folder: videos, tracks, sidecars, nfo/thumb, fonts, extras, junk;
               ReleaseNameParser + current Jellyfin numbers; problems with reason codes
               (unparsed, wrong numbers, duplicates, unrecognised extras, mixed releases)
  2. Identify  use the Jellyfin series/movie the folder belongs to (provider ids) or remote search
               via MetadataGateway; the user confirms
  3. Map       EpisodeMapper proposes file → episode (by parsed number, by order, by title similarity);
               the user fixes the rest in the UI; duplicates: choose a release or keep as versions
  4. Plan      RenamePlanner renders targets with the template; related files follow their video;
               sub-folder tracks move next to the video under host-readable names; extras go to
               `extras/`, specials to `Season 00`; PlanValidator checks: no collisions, no path outside
               the library root, every target episode name parses back to the intended S/E with the
               host's naming rules, the plan is reversible
  5. Apply     OperationGate → PlanExecutor (journal) → library scan of that folder
  6. Undo      replay the journal backwards (available until the journal is deleted)
```

### 7.2 Default templates (configurable)
- Series folder: `{Series Title} ({Year})`
- Season folder: `Season {season:00}` (specials: `Season 00`)
- Episode file: `{Series Title} ({Year}) - S{season:00}E{episode:00}[-E{episodeEnd:00}] - {Episode Title} {Quality}`
- Movie: `{Movie Title} ({Year})/{Movie Title} ({Year}) {Quality}`
- Track next to video: `{video stem}[.{studio}][.forced].{lang}.{ext}`
- Extras: `extras/{original name}`; fonts: `fonts/` beside the season folder

### 7.3 After organizing
An organized folder needs neither links nor naming rules: the host recognises everything by itself.
Link mode simply finds nothing to do there.

### 7.4 Episode source per release
Jellyfin stores only the ids a series was identified with, never the provider the user picked, so a lookup without
a named provider asks every enabled provider and the first answer wins. That mixed the numberings of different
sources in one list (TMDb for the seasons it knows, OMDb placeholders past them) and let a season walk run past the
last real season. The plugin therefore resolves **one provider per release**:

- TMDb by default when the series has a TMDb id; otherwise any provider.
- The choice is stored in a hidden `.outer-files.json` in the release folder (Jellyfin ignores dot folders/files),
  written by the organize page (`POST /OuterFiles/Organize/Source`) and moved with the folder when it is organized.
  Only a file the plugin itself wrote is replaced or removed; "default" removes it.
- The metadata gateway passes the provider as `SearchProviderName` (`IncludeDisabledProviders` so a picked provider
  answers even when not enabled for the library), and treats OMDb placeholders (`Episode #4.1`) as a missing episode.

`EpisodeSources` (domain) owns the choice format and resolution; `EpisodeSourceStore` (adapter) reads/writes the file.

## 8. Configuration (scalars only)
Library roots to manage (default: all TV and movie libraries), link mode on/off, naming adapter on/off,
default audio language, templates (strings), report size. No lists of custom types (XML serializer).

## 9. API (thin)
```
GET  /OuterFiles/Status                      last runs, naming adapter state, counts
GET  /OuterFiles/Links/Preview/{itemId}      desired vs existing links, with reasons
POST /OuterFiles/Links/Reconcile             queue a run
GET  /OuterFiles/Organize/Candidates         folders with problems
POST /OuterFiles/Organize/Sessions           { path } → analysis
POST /OuterFiles/Organize/Sessions/{id}/Identify|Map|Plan
POST /OuterFiles/Organize/Sessions/{id}/Apply  { planHash } (must match the reviewed plan)
POST /OuterFiles/Organize/Source            { root, provider } → store/clear the episode source choice
GET  /OuterFiles/Journal                     applied operations
POST /OuterFiles/Journal/{id}/Undo
```
All endpoints require elevation. Apply requires the hash of the plan the user reviewed.

## 10. Testing strategy
- **Domain unit tests** for every rule, with reason codes asserted.
- **Golden corpus**: the owner's library layout (paths only) as a fixture. Assertions: every track maps
  to its video; JJK/Shinchou/Yakusoku/Jitsu/K-ON get the expected language/studio/forced; nothing maps
  across releases. The repository is public, so the committed fixture is a reduced, anonymised subset;
  the full listing stays local and runs through an opt-in test.
- **Contract tests with real `Jellyfin.Naming`**: naming adapter rules (before/after table), and
  "every rendered target name parses back to its S/E".
- **Executor tests on a temporary tree**: apply → verify → undo → identical tree (like the cleanup script).
- **Live check** per milestone on one series before a release.

## 11. Milestones
1. Skeleton: solution split (Domain / Plugin / tests), composition root, gate, journal, executor, CI.
2. Link mode: matcher, describer, link planner, reconciler, post-scan task, preview API, golden corpus.
3. Naming adapter with self-test; status page.
4. Organize: analyzer, candidates list, identify via providers, mapper, planner, validator (dry run only).
5. Organize: apply/undo, organize UI page.
6. Polish: track-suffix normalisation (`.rus.19.srt`), fonts, docs, remove 0.1.x leftovers.

## 12. Implementation notes (what changed while building it)

- **Recognition is broader than one regex.** Measuring Jellyfin's real parser over all 4365 paths of the
  reference library showed that *every* path-unaware core expression can read folder names: besides the
  anime expression, `([0-9]+)-([0-9]+)` turned `(Сезоны 1-3)` into S1E3. The adapter therefore confines all
  four path-unaware expressions to the file name (lookarounds, groups unchanged) instead of replacing one,
  plus one prepended and three appended rules. The full-library comparison (opt-in test) shows exactly the
  intended 197 changes.
- **Re-numbering existing items.** Installing rules only affects new parses, so a post-scan task finds
  episodes whose stored numbers equal the broken core parse and differ from the new one, and queues a
  replace-all refresh for them. Hand-edited numbers never match the broken parse and are left alone.
- **Stale per-episode nfo.** Jellyfin reads `<episode>` from local nfo files, so moving them with the video
  would re-impose old numbers. Organize mode moves them to `<library>/.outer-files-trash/<time>/` (Jellyfin
  ignores dot folders, `IgnorePatterns`: `**/.*`); undo brings them back.
- **Stale nfo and re-numbering (0.3).** Measured live: an existing episode nfo makes Jellyfin treat the nfo
  saver as enabled (`IsSaverEnabledForItem` for a metadata edit), so a replace-all refresh skips local
  providers and takes the number from the path; but the refresh does not rewrite the nfo unless the library
  saves local metadata, and the next default-mode refresh of a changed file reads the old number again.
  The plugin therefore refreshes those episodes too and reports their nfo files; with local saving on the
  nfo is rewritten (verified). Episodes whose stored number already equals the fixed parse are skipped.
- **Fonts (0.3).** `FallbackFonts` registers `<data>/outer-files/fonts` as `EncodingOptions.FallbackFontPath`
  only when it is empty or already ours; font links are reconciled with the same planner and journal as
  track links. Jellyfin serves only .ttf/.otf/.woff/.woff2, 20 MiB in total (`SubtitleController`).
- **Undo against Jellyfin's own writes (0.3).** With local saving on, Jellyfin writes nfo files and
  thumbnails into organized folders; undo keeps such folders (`FolderNotEmptyException`, journal state
  `kept`) instead of failing, and moves whose result is already in place are skipped, so any interrupted
  run can be retried.
- **Split episodes** (`JN.02a/02b`) are named `… - pt1/pt2`; Jellyfin stacks them into one episode
  (verified: `PartCount=2`).
- **Testability seams**: `ILibrarySource`, `IRefresher`, `IMetadataSource` keep services free of Jellyfin
  entities, so link reconciliation and organize run end-to-end on real temporary folders in tests.

## 13. Verification

- Unit, contract (real `Jellyfin.Naming` 12.4) and file-system tests: apply → verify → undo → identical
  tree, for the executor, link reconciliation and organize.
- Live run on Jellyfin 12.4 built from the `v12.4` tag, library generated from the owner's layouts with real
  (tiny) media: before the plugin Jitsu had E2023 ×14, Yakusoku TV-2 E2 ×3, no outer tracks; after install
  the self-test passed, 132 links were created, Jitsu became E1–E12 with 7 external streams each (language,
  title, forced as expected), TV-2 became S2E1–3; a second scan changed nothing and re-probed nothing;
  subtitles were served through the links; organize moved Jitsu to `Season 01` with tracks next to the
  videos (no links needed), undo restored all 98 files exactly.
- 0.3, same server with jellyfin-web 12.4 built from source and driven in headless Chromium: both pages
  render and work; Jitsu organized from the page (101 moves) with 7 external streams per
  episode, undone from the page (original tree restored, the new folder kept because Jellyfin had saved
  metadata into it); a movie release organized (`Some Movie (2010)/Some Movie (2010) Bluray-1080p.rus.srt`)
  and undone byte-exact; release fonts served by `/FallbackFont/Fonts`; stale nfo checked with local
  saving off (reported, fixed until the file changes) and on (nfo rewritten to the right number).

## 14. Open points
- Which metadata providers are installed (TMDB is built in; TVDB/AniDB/Shikimori plugins change what
  identify can find for anime).
- Fonts for `.ass`: Jellyfin only supports a global fallback font folder (now optionally filled by the
  plugin); per-release fonts and libraries above 20 MiB of fonts are not possible in Jellyfin itself.
- Files the recognition rules cannot number (`136. Title`, `JN.01`) keep whatever number Jellyfin stored
  until they are organized.
