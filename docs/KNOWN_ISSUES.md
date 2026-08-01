# Known Issues

Tracked, intentionally-deferred issues. 

## Forward-scrubbing streaming (non-cached) YouTube videos can fail

YouTube delivers progressive DASH streams that lack a complete seek index until the full
stream has been downloaded. A forward scrub can leave VLC's decoder wedged on a
non-keyframe — the seek is detected as failed (time stops advancing) and app recovers
by restarting playback from the beginning. The user loses their place but the player ends
in a known, controllable state.

**Deterministic fix (user-side):** enable **Cache enabled** + **Cache mode: Everything** +
**Preemptively cache next queue item**. The current track downloads as it starts and the
next queued track downloads in parallel, so scrubs/seeks become instant and reliable
against local files. Pair with **Purge cache on shutdown** to avoid long-term disk use
while staying instantly seekable in-session.

**Tip:** **Settings → Video engine → yt-dlp** noticeably improves streaming scrub
reliability even without caching (fresher stream-URL handling avoids the throttling that
wedges the decoder). Caching remains the deterministic fix.

## Age-restricted YouTube videos fail to play (all engines)

Some videos require a signed-in account to confirm age ("Sign in to confirm your age…").
Both backends hit this: YoutubeExplode cannot resolve the stream, and yt-dlp errors out
unless cookies are supplied (`--cookies-from-browser` / `--cookies`). Playback fails and
Phosphor returns to a controllable state, but the track does not play.

**Possible future fix:** optional cookie support — a setting pointing yt-dlp at a browser's
cookies (or a cookies.txt file) so age-gated content can authenticate. Engine-agnostic and
unaffected by the plug-in source path.

## Live TV (Plex/Jellyfin) can exceed the first-frame timeout on slow-starting streams

**Where:** `Phosphor.Plugins.Plex/PlexLiveTvService.cs`,
`Phosphor.Plugins.Jellyfin/JellyfinClient.cs` (live open/resolve), and the host's
first-frame startup watchdog that raises "server unreachable or stream timed out".

**Context:** Live TV playback is not a ready-to-serve file — the server must **tune a
physical tuner and start a transcode/remux** (the tuner feed is MPEG-2/UDP and cannot
direct-play), so the first HLS segment only appears after ffmpeg spins up. Measured spin-up
on a modest server (Synology VM) was **~11 seconds**, which races the host's ~10s first-frame
watchdog. When the watchdog wins, playback is reported as failed even though the stream would
have started a moment later. Observed as intermittent success (~1 in 3–4) on the
underpowered VM; reliable on adequately-provisioned servers and on the hardware
(Plex + HDHomeRun) path. The plug-in's own open and teardown are clean in every case
(verified HTTP 200/204 on stop — no tuner/session leaks); only the startup *window* is at
issue.

**Contributing factors (can compound):**
- **Transcode/remux start latency** — ffmpeg process start + first-keyframe/segment
  generation; scales inversely with server CPU. This is the dominant factor on weak servers.
- **Network latency to the server** — a remote or high-latency server adds round-trips for
  PlaybackInfo/tune + the initial segment fetch on top of the transcode delay.
- **Tuner acquisition** — the backing device (e.g. HDHomeRun) may take time to lock the
  channel, especially if a tuner must first be freed.

**Status: fixed (automatic live-aware timeout).** Implemented Option 1 — an automatic,
per-stream startup budget that the host watchdog honors for slow-starting live streams,
leaving finite-media timeouts unchanged.

**Implementation:**
- `Phosphor.Plugin.Abstractions/ResolvedStream.cs` — added an optional
  `TimeSpan? StartupTimeout` hint (init prop, default `null`). Null keeps the host's standard
  finite-media timeout; a non-null value is a slow-start budget the watchdog should wait.
- `Phosphor.Plugins.Plex/PlexSource.cs` and `Phosphor.Plugins.Jellyfin/JellyfinSource.cs` —
  the live-resolve paths now set `StartupTimeout = TimeSpan.FromSeconds(30)` on their live
  `ResolvedStream` (alongside `IsLiveStream = true`). 30s comfortably clears the measured ~11s
  spin-up on a modest server.
- `Phosphor/Models/VideoItem.cs` — added `TimeSpan? StartupTimeout` so the hint can ride with
  the now-playing item.
- `Phosphor/JukeboxViewModel.cs` — propagates `ResolvedStream.StartupTimeout` onto
  `VideoItem.StartupTimeout` in both the browse-time mapping and the play-time live-resolve
  path (`ResolveAndPlayLiveAsync`).
- `Phosphor/Windows/BackglassWindow.xaml.cs` — replaced the two hardcoded `Task.Delay(10000)`
  waits (audio-only start and first video frame) with `Task.Delay(FirstFrameTimeoutMs(vm))`,
  a helper that returns the item's `StartupTimeout` when present, else the
  `DefaultFirstFrameTimeoutMs` (10s). Note: `PlayfieldWindow` has no streaming first-frame
  watchdog (it only drives local/folder ambient video), so only Backglass needed the change.

**Not (yet) done — possible follow-ups:**
- **User-configurable timeout (setting):** *[FLAG — future]* surface a "Live TV startup
  timeout (seconds)" setting (global, or per-source instance) so users on slow/remote servers
  can tweak the budget without a rebuild. The fixed 30s default covers the tested servers, so
  this is deferred; add the knob if a real-world server needs a different value.
- **Warm-up / readiness probe:** as a fallback if the fixed budget proves insufficient — have
  the plug-in poll the child HLS playlist until the first segment exists before handing off the
  URL, hiding latency from the watchdog entirely (at the cost of plug-in complexity).


