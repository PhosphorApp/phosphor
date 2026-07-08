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

## Native filters — app-level vs. yt-dlp (evidence-based)
The `min:` / `max:` / `channel:` / `playlist:` / title-term filters are **app-level**
constructs in `JukeboxViewModel` (`ParseDurationFilters`, `PassesDurationFilter`,
`FilterVideosAsync`), layered *above* the `ISearchEngine` seam — **not** YoutubeExplode
features. They operate on the `VideoItem`s the engine yields, so they work identically for
**any** search engine, including a future `YtDlpSearchEngine`, with zero extra work.

yt-dlp *also* supports these natively, and one is a genuine improvement:

| Filter | yt-dlp native | Works in flat-playlist (search)? |
|--------|---------------|----------------------------------|
| **Duration** (`min:`/`max:`) | `--match-filters "duration>=N & duration<=M"` | ✅ `duration` **is present** in flat-playlist |
| Title contains | `--match-filters "title~='(?i)…'"` | ✅ (regex) |
| Offset paging | `--playlist-items 26-50` (+ `--lazy-playlist`) | ✅ |
| Upload date (`before:`/`after:`) | `--match-filters "upload_date>=…"` | ❌ `upload_date` is `NA` in flat-playlist (needs full resolve) |

### Optimization: server-side duration filter (opt-in enhancement)
Today the app filters duration **client-side** — it must fetch results to inspect
`VideoItem.Duration`, so aggressive filters (e.g. `song max:3m`) can hit the
`MaxDurationScanCount = 500` scan cap and starve. Because `duration` **survives
flat-playlist**, `YtDlpSearchEngine` could push `min:`/`max:` down as
`--match-filters "duration>=… & duration<=…"`, discarding non-matches **at the source** —
a full page of matching results, no scan-cap starvation. This is a real differentiator,
not just parity.

- **Baseline (free):** engine yields `VideoItem`s; the VM's existing duration/title/
  channel/playlist filters apply on top → identical behavior to today.
- **Optimization (opt-in):** extend the seam so `SearchVideosAsync` can accept the parsed
  `min:`/`max:` and emit `--match-filters` server-side. Small, additive; behind the same
  engine toggle. (`channel:`/`playlist:`/title stay engine-agnostic in the VM.)

## Open question
- Is yt-dlp search worth doing at all, or is the seam enough (keep YoutubeExplode for
  search, yt-dlp for video = the hybrid)? Decide before starting 6b.

## Phase 6b outcome (as built) ✅
Search is now switchable via a Settings "Search:" dropdown (mirrors the video engine).

- **`YtDlpVideoEngine.RunYtDlpStreamingAsync`** — shared line-by-line stdout runner. Holds
  the process gate only to launch (a running process has the exe in memory, so a concurrent
  updater on-disk replace is harmless), then streams without blocking other yt-dlp reads;
  kills the process on cancel/early-dispose.
- **`YtDlpSearchEngine : ISearchEngine`** — `SearchVideosAsync` (`ytsearch200:` streamed),
  `GetPlaylistVideosAsync` / `GetChannelUploadsAsync` (playlist / `@handle/videos` URLs),
  all via `--flat-playlist --lazy-playlist --dump-json`. Parses JSONL → `VideoItem`
  (id/title/uploader|channel/duration/best-thumbnail, `i.ytimg` fallback), skipping
  malformed lines. `ResolvePlaylistIdAsync` handles direct id/URL only.
- **Engine-aware page size** — `JukeboxViewModel.SearchPageSize` = 50 (yt-dlp) vs 25
  (YoutubeExplode), to offset the per-page process-spawn latency.
- **Settings** — "Search:" dropdown + load/save + hint + `e.Handled` (no scroll jump).
- **App-level filters unchanged** — `min:`/`max:`/title/channel/playlist all live in the VM
  above the seam, so they work identically for both engines.

### Validated
- Search JSONL maps correctly (concert results carry durations → `min:`/`max:` works);
  channel path (`@NASA/videos`) returns entries; default engine untouched; build green.

### Known limitations / deferred
- Playlist-by-**name** returns null with yt-dlp (search yields videos, not playlists);
  direct id/URL works.
- Dormant exception-fallback (§4a) not built — needs the `App.xaml.cs` suppression
  revisited so failures are observable.
- Server-side `--match-filters` duration push-down deferred (client-side parity for now).
- **Test point:** confirm page size 50 keeps scroll fetches infrequent; tune if the ~2s
  per-page spawn still feels laggy.
