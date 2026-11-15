# Changelog

## 0.1.0.0

- Initial release.
- Scheduled task that attaches audio and subtitle files from sub folders to episodes, movies and other
  videos, without modifying any media file.
- Matching by stem, by episode number, and by movie folder; language, forced (signs) and studio detected
  from folder and file names.
- Plugin page with the last scan summary, "Scan now" and the list of videos that received tracks.
- API: `GET /OuterFiles/Status`, `GET /OuterFiles/Videos`, `POST /OuterFiles/Scan`,
  `GET /OuterFiles/Preview/{itemId}`.
