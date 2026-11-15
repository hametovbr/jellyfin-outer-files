# Matching rules in detail

The matcher lives in `Jellyfin.Plugin.OuterFiles/Matching/OuterFileMatcher.cs` and is covered by
`tests/Jellyfin.Plugin.OuterFiles.Tests` against the folder layout of a real release.

## Candidate files

All files below the folder of the video, recursively, with these extensions:

- subtitles: `.ass .ssa .srt .vtt .sub .idx .smi .sami .mks`
- audio: `.mka .flac .aac .ac3 .dts .eac3 .opus .mp3` (configurable on the plugin page)

Video files found in the same tree are ignored.

## Normalisation

Names are lower-cased, the characters `-`, `_`, `.` and dashes are turned into single spaces, repeated
separators are collapsed. `[VCB-Studio] Show [05][Ma10p_1080p]` becomes `show 05`: everything inside
brackets is dropped **except a bracket that contains only digits**, which is the episode number.

## Rule 1: same stem

`normalize(track) == normalize(video)` or `normalize(track)` starts with `normalize(video)`. This is
the behaviour Jellyfin already has for adjacent files and it stays valid for nested folders.

## Rule 2: episode number

Applies when rule 1 does not hold and the video stem carries a number:

- the track stem must contain the same number token;
- the track stem must not contain a number from the **video's folder name** (this is how a leftover
  year such as `[2023]` in a folder name is kept out of the comparison);
- when several candidate stems contain the same number, the track is only accepted if it shares a long
  common prefix (≥ 12 normalised characters) with the video stem. That keeps `ShowA 05` apart from
  `ShowB 05` in libraries that keep several releases in one folder.

Numbers are compared without leading zeros, so `[05]`, `- 5` and `S01E05` are the same episode.

## Rule 3: movies

A movie stem has no episode number, so rule 2 cannot apply. Tracks are attached only when they share the
movie stem (rule 1). A movie folder that contains several different films is therefore never mixed.

## Language

1. the **deepest folder** between the video folder and the track that names a language wins
   (`RU Subs`, `EN Sound`, `JP Subs`, `RU Sound [Anilibria]`);
2. otherwise the dotted flag part of the file name (`.ru_CR`, `.en_signs_CR`, `.jp`);
3. otherwise nothing — the stream keeps whatever the container reports.

## Signs and studio

- folder `signs`/`надписи` or name part `signs`/`forced` marks the track as forced (signs only);
- name part `.ru_Anilibria` gives language `rus` and title `Anilibria`; `.en_CR` gives language `eng`
  and no title, because `CR` means "complete release" and is not a studio.
