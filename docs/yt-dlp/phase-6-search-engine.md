# Phase 6 — `ISearchEngine` Seam + Optional yt-dlp Search ⬜

**Status:** Not started (riskiest / last). **Prereq:** Phases 2–5.
**Goal:** Abstract discovery behind `ISearchEngine`; optionally add a yt-dlp search
impl and/or a **dormant fallback** to de-risk the search SPOF (analysis §4a).

## Scope (today's search surface, all in `JukeboxViewModel.cs`)
- `Search.GetVideosAsync` (free-text; genre browse; AutoDJ)
- `Search.GetPlaylistsAsync` + `Playlists.PlaylistId.Parse` (`playlist:` prefix)
- `Playlists.GetVideosAsync`, `Channels.GetByHandle/GetByUser/GetUploads` (`channel:`)
- `Videos.GetAsync` (metadata/chapters — may move to Phase 5)

## Plan sketch
- Define `ISearchEngine` yielding `VideoItem` via `IAsyncEnumerable` (preserve the
  incremental pagination UX). Wrap current logic as `YoutubeExplodeSearchEngine`.
- Add `SearchEngineKind` + factory + `AppSettings.SearchEngine`.
- Optional: `YtDlpSearchEngine` (`ytsearchN:`, `--flat-playlist`). Prototype streamed
  stdout → `IAsyncEnumerable`; if UX is fine, full yt-dlp becomes viable.
- **Dormant fallback:** on *exception* (not empty results) fail over to yt-dlp search
  for the session; add a "force yt-dlp search" toggle to keep the path exercised.

## Dependency / gotcha
- `App.xaml.cs` currently **suppresses** YoutubeExplode exceptions by stack-trace
  string. For fault-triggered failover, failures must be **observable** at the engine
  layer — revisit that suppression here (see cleanup tracker).
- `FilterVideosAsync` consumes `IVideo` mid-pipeline; if search yields `VideoItem`
  directly, move the filter to `VideoItem.Title` (confirm nothing else reads `IVideo`).

## Validation
- Search/genre/AutoDJ/playlist/channel all work through the seam.
- Fallback triggers on injected search fault; does NOT trigger on genuine no-results.
