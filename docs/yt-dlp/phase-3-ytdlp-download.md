# Phase 3 — yt-dlp Video Engine: Download Path ⬜

**Status:** Not started. **Prereq:** Phase 2 complete.
**Goal:** Implement `YtDlpVideoEngine` and make the **cache/prefetch download** path
use it when `AppSettings.VideoEngine == YtDlp`. Biggest, safest win (offline, measurable).

## Plan sketch
- Add `Phosphor/Video/YtDlpVideoEngine.cs : IVideoEngine` (promote the spike's process
  plumbing + JSON parsing from `YtDlpSpike.cs`, then **delete the spike**).
- `DownloadStreamsAsync`: prefer yt-dlp one-shot `-f "bv*+ba/b" --remux-video mkv`
  (uses bundled ffmpeg). Decide: return the already-remuxed `.mkv` (simplest) vs. raw
  files for the caller to mux. If returning a finished `.mkv`, extend the seam with an
  optional "already muxed" signal so `VideoCache`/`PrefetchCache` skip their mux step.
- Keep the caches' index/eviction/chapters logic intact.
- yt-dlp path resolution: `YtDlpVideoEngine` uses `dependencies/yt-dlp.exe` copied next
  to the app (same as spike's `ResolveYtDlpPath`).

## Validation
- Toggle `VideoEngine = YtDlp`, enable cache, play a playlist; confirm `.mkv` is
  seekable and scrub reliability improves vs. streaming.
- Measure cache/prefetch time vs. YoutubeExplode path.

## Cleanup touchpoints
- ✅ Remove `Phosphor/YtDlp/YtDlpSpike.cs` (superseded).
- Reassess `Phosphor/YtDlp/` folder (delete or house the real engine).

## Open questions
- One-shot remux (return finished `.mkv`) vs. raw-files-then-caller-mux — pick based on
  how cleanly the seam extends. Record decision in the tracker.
