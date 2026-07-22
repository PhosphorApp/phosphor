# Known Issues

Tracked, intentionally-deferred issues. Each entry notes the tradeoff and where the
relevant code lives so it's easy to pick up later.

## Settings ComboBox — keyboard bring-into-view suppressed

**Where:** `Phosphor/Windows/SettingsWindow.xaml.cs` — static constructor class handlers
for `ComboBox` / `ComboBoxItem` `RequestBringIntoView`.

**Context:** Changing a `ComboBox` selection inside a `ScrollViewer` used to jump the
settings panel scroll position (usually to the top). This was fixed by registering class
handlers that mark `RequestBringIntoView` as handled for both `ComboBox` and
`ComboBoxItem`, covering every combo including dynamically-built plug-in dropdowns
(e.g. Vimeo/DailyMotion video quality).

**Tradeoff / known issue:** Suppressing `RequestBringIntoView` on the `ComboBox` type
also disables the *legitimate* behavior of auto-scrolling an off-screen combo into view
when it receives focus via keyboard (Tab). The Settings window is primarily mouse-driven
(cabinet keyboard shortcuts target playback, not settings), so this is an acceptable
trade for now.

**Possible future fix:** Make the handler selective instead of blanket-suppressing —
e.g. only mark handled when the requested rectangle corresponds to the combo/item that is
already visible (a selection-driven scroll), and let through genuine focus-driven
bring-into-view when the target is off-screen. Would restore keyboard navigation without
reintroducing the selection jump.

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

