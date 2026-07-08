# Phase 6 — `ISearchEngine` Seam ✅ DONE (+ Phase 6b: yt-dlp search — deferred)

**Status:** Phase 6 (seam) complete, commit `d1191fd`. Phase 6b (yt-dlp search impl +
dormant fallback) deferred. **Prereq:** Phases 2–5.
**Goal (Phase 6):** Abstract discovery behind `ISearchEngine`, wrapping YoutubeExplode —
**no behavior change** — to isolate the last `_youtube` usages and set up a future yt-dlp
search impl.

## Why split 6 / 6b
The analysis flagged search as the riskiest phase (incremental-pagination UX regression
risk), but that risk lives entirely in the *yt-dlp search implementation*, not the seam.
So Phase 6 delivers the seam (pure refactor, reversible), and Phase 6b — the risky part —
becomes its own increment we can A/B in isolation. Mirrors the Phase 2 approach.

## What was delivered (Phase 6)
- `Phosphor/Search/ISearchEngine.cs`: `SearchVideosAsync`, `GetPlaylistVideosAsync`,
  `GetChannelUploadsAsync` (all `IAsyncEnumerable<VideoItem>`), `ResolvePlaylistIdAsync`
  (encapsulates `PlaylistId.Parse` → search-by-name, with an `onFoundByName` status callback).
- `YoutubeExplodeSearchEngine`: wraps search/playlist/channel + the `IVideo`→`VideoItem`
  mapping + the handle→user and id→name fallbacks. Takes an `HttpClient` for timeout parity.
- `SearchEngineKind` enum + `SearchEngineFactory` + `AppSettings.SearchEngine` (default
  YoutubeExplode) + `default_settings.json`.
- `JukeboxViewModel`: `_searchEnumerator` is now `IAsyncEnumerator<VideoItem>`; all four
  `SearchAsync` branches, `FilterVideosAsync`, both `LoadMoreResults` mapping sites, and
  both AutoDJ methods route through `_searchEngine`. `_youtube` field + YoutubeExplode
  `using`s removed. `SetSearchEngine`/`RebuildSearchEngine` added; `SetYouTubeTimeout`
  rebuilds the search engine. Wired at `App.xaml.cs` (startup) + `DmdWindow` (settings).

## Validation done
- Full build green. Zero YoutubeExplode `Search/Playlists/Channels` calls remain outside
  `YoutubeExplodeSearchEngine`; the only `new YoutubeClient` is inside it. `_youtube` fully
  gone from the VM.
- Behavioral parity audited: unquoted-playlist filter-terms logic, App startup ordering,
  incremental pagination pipeline (engine yields `VideoItem`, downstream unchanged).

## Phase 6b — yt-dlp search impl + dormant fallback (DEFERRED, not started)
- `YtDlpSearchEngine` (`ytsearchN:`, `--flat-playlist --dump-json`, streamed stdout →
  `IAsyncEnumerable<VideoItem>`). Prototype the incremental UX; if it feels responsive,
  full yt-dlp search is viable.
- **Dormant fallback (analysis §4a):** on *exception* (not empty results) fail over to
  yt-dlp search for the session; add a "force yt-dlp search" toggle to keep it exercised.
- **Gotcha:** `App.xaml.cs` currently **suppresses** YoutubeExplode exceptions by
  stack-trace string. Fault-triggered failover needs failures observable at the engine
  layer — revisit that suppression (see cleanup tracker).

## Open question
- Is yt-dlp search worth doing at all, or is the seam enough (keep YoutubeExplode for
  search, yt-dlp for video = the hybrid)? Decide before starting 6b.
