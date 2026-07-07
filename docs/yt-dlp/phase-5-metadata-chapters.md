# Phase 5 — Metadata & Native Chapters ✅ DONE

**Status:** Complete. Commit `768e136`. **Prereq:** Phases 3–4.
**Goal:** Use yt-dlp's native structured `chapters[]` + duration instead of scraping
the description (`ParseYouTubeChapters`), keeping description-parse as fallback.

## Decision — metadata on `IVideoEngine` (not a separate seam)
The analysis floated putting metadata on `ISearchEngine`, but that seam doesn't exist
yet (Phase 6) and the yt-dlp JSON is produced by the **video** engine. So `GetMetadataAsync`
was added to **`IVideoEngine`** — both engines implement it, and the VM asks the *active
video engine*. This keeps chapters coherent with whichever engine is selected and avoids a
premature dependency on the (future) search seam. Revisit in Phase 6 only if metadata needs
to follow the search engine instead.

**Chapter authority:** native-first, description-fallback, single-sourced parse.
`ParseYouTubeChapters` stays in the VM; the engine returns native chapters (yt-dlp) *and*
the raw description, and the VM parses the description **only when native chapters are absent**.

## What was delivered
- `IVideoEngine.GetMetadataAsync(videoId) → VideoMetadata?` (+ `VideoMetadata` DTO:
  `Duration`, `Description`, `Chapters`).
- `YoutubeExplodeVideoEngine`: `Videos.GetAsync` → duration + description, **empty** native
  chapters (so the VM always description-parses = identical to prior behavior).
- `YtDlpVideoEngine`: `--dump-single-json` → duration, description, native `chapters[]`
  (`start_time`/`end_time`/`title` seconds → `ChapterMarker`). Empty when `chapters=null`.
- `JukeboxViewModel`: `GetAccurateDurationAsync` + `FetchYouTubeChaptersAsync` now go through
  `_videoEngine.GetMetadataAsync`; native-first then description fallback; logs the source.
  Cache-persist (`VideoCache.UpdateChapters`) + UI-notify unchanged. `_youtube` is now
  **search-only** in the VM (no more `Videos.GetAsync` for video metadata).

## Validation done
- Chaptered `jNQXAC9IVRw`: yt-dlp returns 3 native chapters (Intro/The cool thing/End),
  duration 19s → native path.
- Non-chaptered `dQw4w9WgXcQ`: `chapters=null`, duration 213s, description present →
  description-parse fallback.
- JSON field names match the DTO's `JsonPropertyName` mappings. Full build green.

## Not covered (deferred)
- Whether metadata should migrate to `ISearchEngine` in Phase 6 — deferred; current
  placement on the video engine is coherent and avoids double-fetch.
