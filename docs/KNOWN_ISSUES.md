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

**Where:** the `Phosphor.Plugins.Plex` / `Phosphor.Plugins.Jellyfin` live-TV code (in the
`phosphor-plugins` repo) and the host's
(`Playback/JukeboxPlayer.cs`, `MediaEngine.FirstFrameTimeoutMs`)

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

**Status: mitigated.** Plex and Jellyfin live-TV streams now carry a 30 s startup budget
(`ResolvedStream.StartupTimeout`) that the host's first-frame watchdog honors, instead of the
default 10 s used for finite media. A very slow or remote server can still exceed it.

**Possible follow-ups:**
- A user-configurable "Live TV startup timeout" setting (global or per-source instance).
- A warm-up probe in the plug-in that waits for the first HLS segment before handing off the URL.
