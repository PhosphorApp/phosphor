# Phase 4 — yt-dlp Video Engine: Live Playback ⬜

**Status:** Not started. **Prereq:** Phase 3 complete.
**Goal:** Route `BackglassWindow` live resolution through `YtDlpVideoEngine.ResolveStreamsAsync`
when `VideoEngine == YtDlp`. A/B scrub reliability + startup latency vs. YoutubeExplode.

## Plan sketch
- Implement `YtDlpVideoEngine.ResolveStreamsAsync` using `-g` (video+audio URL pair,
  or muxed/audio-only as needed). Resolve fresh per play (URLs carry `expire=`).
- No caller changes if Phase 2's seam is clean — just the engine impl + toggle.

## Validation / A-B
- With the toggle, compare: time-to-first-frame, streaming scrub reliability, and the
  live→cached switch path (`SwitchToCachedFileAndSeek`) under both engines.
- Confirm audio slave sync and muxed fallback both work.

## Risks
- Process-spawn latency on every play (100s of ms) — measure; acceptable for play.
- `infoForOverlay`/streaming-diagnostic signal must be preserved via `VideoStreams?`.
