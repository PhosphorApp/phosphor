# Phase 5 — Metadata & Native Chapters ⬜

**Status:** Not started. **Prereq:** Phases 3–4 (yt-dlp JSON available).
**Goal:** Use yt-dlp's native structured `chapters[]` + duration instead of scraping
the description (`ParseYouTubeChapters`), keeping description-parse as fallback.

## Plan sketch
- Metadata is discovery-adjacent → belongs with the **search/metadata** side, but the
  yt-dlp JSON is produced by the **video** engine's `--dump-json`. Decide authority to
  avoid double-fetching (see analysis "Chapter source coupling").
- Options: (a) `ISearchEngine.GetMetadataAsync` gains a yt-dlp impl; or (b) video
  engine backfills chapters into the item when it already holds the JSON.
- Preserve `VideoCache.UpdateChapters` persistence + `NotifyCachedChaptersRestored`.

## Validation
- Videos with real chapter markers show correct chapter ticks without description text.
- Fallback still works for videos lacking native chapters.
