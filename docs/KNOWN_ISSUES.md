# Known Issues

Tracked, intentionally-deferred issues. Each entry notes the tradeoff and where the
relevant code lives so it's easy to pick up later.

## Settings panel — focus-driven scroll jumps are corrected by a position guard

**Where:** `Phosphor/Windows/SettingsWindow.xaml.cs` — `AttachScrollGuard` /
`SuppressScrollIntoViewOnLoad`, plus `ComboBox`/`ComboBoxItem` class handlers in the
static constructor.

**Context:** Changing a control inside a `ScrollViewer` (selecting a combo item, toggling
a checkbox, clicking a plug-in button) used to jump the settings panel scroll position
(often to the top). Diagnostic logging proved the leaked scroll is a `RequestBringIntoView`
whose `OriginalSource` is the **`ScrollViewer` itself**, raised when a child takes focus.
That event originates at the scroll host and bubbles *up and away* from any child handler,
and WPF's own scroll runs in a `ScrollViewer` class handler before instance handlers — so it
cannot be cancelled from below (several attempts to do so failed intermittently).

**Fix:** A **scroll-position guard**. Each `ScrollViewer` remembers the user's intended
vertical offset, updated only on genuine *scroll gestures* (mouse wheel, scrollbar thumb
drag, PageUp/PageDown/Home/End, touch move). On any `ScrollChanged` that occurs without a
recent scroll gesture — i.e. a programmatic focus jump — the guard restores the saved
offset. This is source-agnostic: it doesn't matter what raised the scroll. The combo class
handlers remain as a cheap first layer for the common case.

**Tradeoff / known issue:** Legitimate keyboard focus-driven bring-into-view within the
panels is also neutralized (a Tab to an off-screen control won't auto-scroll it into view).
The Settings window is mouse-driven (cabinet keyboard shortcuts target playback, not
settings), so this is acceptable. There is also a theoretical one-frame "restore" bounce
when a jump is undone; it has not been observable in testing.

**Possible future refinement:** Distinguish a genuine off-screen focus target (should scroll)
from an already-visible one (should not), so keyboard navigation to off-screen controls still
works while spurious jumps are suppressed.


## Forward-scrubbing streaming (non-cached) YouTube videos can fail

YouTube delivers progressive DASH streams that lack a complete seek index until the full
stream has been downloaded. A forward scrub can leave VLC's decoder wedged on a
non-keyframe — the seek is detected as failed (Time stops advancing) and Phosphor recovers
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

## [Watching — not reliably reproducible] Live playback can time out during a same-video cache download

Observed once with **Cache mode: Everything** + **yt-dlp engine**: a track resolved
successfully but the first video frame never arrived within the 10s startup window, so
playback stopped with "server unreachable or stream timed out"; the same track played
instantly once cached. Suspected cause is bandwidth/process contention between the
concurrent yt-dlp cache download (~50 MB) and the live stream buffering.

**Diagnostic if it recurs:** reproduce with the plug-in flag OFF (clear the `cache/` folder
first); if it still times out, it is pre-existing contention rather than the plug-in path,
and the fix is likely to defer live streaming when a same-video cache download is already in
flight.

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


