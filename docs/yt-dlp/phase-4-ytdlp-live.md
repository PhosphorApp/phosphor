# Phase 4 — yt-dlp Video Engine: Live Playback ✅ DONE

**Status:** Complete. Commit `e0cc5f7`. **Prereq:** Phase 3 complete.
**Goal:** Make `YtDlpVideoEngine.ResolveStreamsAsync` native (no delegation), so
`VideoEngine == YtDlp` resolves live playback URLs via yt-dlp. Enables A/B vs YoutubeExplode.

## What was delivered
- Native `ResolveStreamsAsync` in `Phosphor/Video/YtDlpVideoEngine.cs`:
  - **Audio-only:** `-f "ba[audio_channels<=2]/ba"` (or `ba`) `-g` → single URL →
    `VideoStreams(AudioOnly)`.
  - **Non-audio (one spawn):** `-f "<video+audio tiers>" -g --print "%(width)sx%(height)s"`.
    Output parsed by line: `line[0]` = "WxH"; **≥3 lines** = `SeparateVideoAudio`
    (video + audio slave); **2 lines** = `Muxed`.
  - Returns `null` on failure — `BackglassWindow` bails cleanly (same as YT-Explode null).
- Removed the `_liveFallback` YoutubeExplode delegation; engine is now fully native.
- **No caller changes** — Phase 2's seam absorbed it (`BackglassWindow` already switches
  on `VideoStreams.Kind` and uses `streamingResolution` for the overlay/diagnostic).

## Bug caught & fixed during validation
The first cut reused the audio-only selector inside the non-audio expression
(`bv*{cap}+{audioSel}/b{cap}`), which — because `audioSel` contains a `/` — injected an
unintended **bare audio-only** tier before muxed. For videos lacking a stereo track this
would resolve to audio-only instead of muxed video. Fixed to explicit video+audio tiers:
- stereo: `bv*{cap}+ba[audio_channels<=2]/bv*{cap}+ba/b{cap}`
- non-stereo: `bv*{cap}+ba/b{cap}`

## Validation done
- Terminal (jNQXAC9IVRw): separate = 3 lines (`res`, video, audio); muxed = 2; audio-only = 1.
  Both corrected stereo/non-stereo expressions verified. URLs carry `expire=`.
- Full build green.

## Deferred / not covered
- **Quantitative A/B** (time-to-first-frame, streaming scrub reliability, live→cached
  switch) is left to hands-on testing with the Settings toggle — the plumbing is in place.
- **Live scrub of progressive DASH** remains a caching outcome (unchanged); native
  resolution does not fix the seek index. Reliable scrubbing still = cache/remux path.
- **Resilience fallback:** resolution is now pure yt-dlp (no per-play YT-Explode fallback)
  to keep the A/B clean. A dormant fallback could be a later explicit decision.

## Risks (carried)
- Per-play process-spawn latency (~100s of ms) — acceptable for play; measure in A/B.
