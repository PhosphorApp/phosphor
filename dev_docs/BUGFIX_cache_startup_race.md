# Bug Fix — Cache/Prefetch Startup Race (silent no-op)

> **Flagged for durability.** This fix is unrelated to the multiplayer refactor it was
> discovered during. It is committed as its own commit (`4caa82e` on branch
> `multiplayer`) so it can be cherry-picked onto `master` independently. If the
> `multiplayer` branch is ever reverted or abandoned, **this fix must be preserved** —
> port commit `4caa82e` (and this doc) to `master`.

## Symptom

Intermittent, hard to reproduce: with the video cache enabled (and/or preemptive
caching / prefetch on), YouTube items were **not cached** on play — and **nothing was
logged** to indicate why. Toggling the setting and relaunching sometimes "fixed" it;
sometimes it started working on its own. The next queue item also failed to
preemptively cache.

## Root cause — startup race on `DownloadOverride`

All three download-based cache paths depend on a `DownloadOverride` delegate:

- current-item cache (`JukeboxViewModel.PlayNow`)
- preemptive next-track cache (`KickoffPreemptiveCacheForNext`)
- prefetch next-track (`PrefetchNextTrack`)

`DownloadOverride` is wired **only** inside `BuildSourceRegistryAsync` (via
`WireCacheDownloadOverride`). At startup that build is **fire-and-forget**
(`App.xaml.cs`), while `SetupCache` runs synchronously first. So if any cache trigger
fired before the registry finished building, the cache/prefetch hit
`if (DownloadOverride == null) return;` and **silently gave up**. Whether caching worked
on a given launch depended purely on who won the race (play vs. registry build) — hence
the intermittency and the "relaunch fixed it" behavior.

## Fix

1. `BuildSourceRegistryAsync` publishes a `_sourceRegistryReady` task, completed in a
   `finally` (success or failure, so awaiters never hang).
2. All three cache triggers route through that gate before downloading:
   - `CacheAfterRegistryReadyAsync` (current-item + preemptive)
   - `PrefetchAfterRegistryReadyAsync` (prefetch)
   They remain fire-and-forget continuations, so playback still starts instantly.
3. Every previously-silent skip path now logs its reason (`DebugLog`) in
   `VideoCache.CacheVideoAsync`, `PrefetchCache.PrefetchAsync`, and the VM cache/
   preemptive gates — so any future non-caching is immediately diagnosable
   (disabled, over clip-length, non-YouTube id, already-cached, no-override,
   null-download).

## Files touched

- `Phosphor/JukeboxViewModel.cs`
- `Phosphor/Caching/VideoCache.cs`
- `Phosphor/Caching/PrefetchCache.cs`

---

# Follow-up bugs

## BUG 1 — Main video-cache enable/disable does not apply until restart

**Status: FIXED.**

Toggling the **"cache enabled"** checkbox (and cache size / max clip length) in Settings
has **no effect until the app is restarted**. The settings-apply path in
`DmdWindow.xaml.cs` (runs on Settings save, ~line 2725) re-applies prefetch, thumbnail
cache, result cache, engine, quality, preemptive, and network settings — but **never
re-runs `SetupCache(...)`** for the main `VideoCache`. So the cache keeps whatever
enabled/size/clip-length it was given at launch (`App.xaml.cs` line 67).

Observed: enabling caching then playing without restart failed to reflect the new state;
disabling caching then playing still logged `cacheEnabled=<launch value>`; a restart made
it correct.

**Fix applied:** `DmdWindow`'s settings-apply block now calls
`vm.SetupCache(_appSettings.CacheEnabled, _appSettings.CacheMaxSizeGb, _appSettings.CacheMaxClipLengthMinutes)`
alongside `SetupPrefetch`/`SetupThumbnailCache`. `JukeboxViewModel.SetupCache` was changed
to call `VideoCache.UpdateSettings` in place when a cache already exists (create only on
first call), so the loaded index/entries survive a settings save.

## BUG 2 — Long yt-dlp videos time out while streaming + caching concurrently

**Status: FIXED (mitigation: defer background cache until first frame).**

With caching **enabled** and the **yt-dlp** engine, long videos (e.g. full-length
concerts) intermittently hit `Playback failed: server unreachable or stream timed out`.
The commonality: yt-dlp engine, item not yet cached, long duration. YouTubeExplode-style
playback streams to the window immediately while the cache download runs in the
background; on large videos the concurrent background download appears to starve/stall
the streaming path, tripping the first-frame/startup watchdog
(`DefaultFirstFrameTimeoutMs`, 10s — see the log gap 23:17:51 → 23:18:01 above).

Evidence (first run, caching on):
```
[23:17:47] Playing: Rush ** FULL SHOW ** ...
[23:17:47] [VideoCache] Caching mN4xDgLN3k0 (quality=High, stereo=True)
[23:17:51] Stream resolution routed through plug-in YouTube source
[23:18:01] Playback failed: server unreachable or stream timed out   (~10s watchdog)
```
After restart with cache disabled the same item played fine
(`Not caching ... cacheEnabled=False`).

**Root cause:** the current-item cache download (a second yt-dlp/HTTP fetch) was kicked
off in `PlayNow` immediately, concurrent with stream resolution. On long videos that
second fetch starved the streaming path and tripped the 10s first-frame watchdog.

**Fix applied:** the current-item background cache download is now **deferred until
playback is confirmed**. `PlayNow` records the item in `_pendingCacheItem`; the download
is kicked off from `NotifyPlaybackStarted` (fired on first video output). The failure/
timeout path clears `_pendingCacheItem` (via `NotifyPlaybackFailed`) so a timed-out item
is not cached. This keeps the second fetch off the play path entirely, so first-frame
startup no longer contends with the download. (Preemptive next-track caching already runs
after the current track is playing, so it was unaffected.)

## BUG 2b — yt-dlp download holds the single process gate, blocking later resolves

**Status: FIXED (separate download gate).**

Discovered while fixing BUG 2. `YtDlpVideoEngine` serialized **all** yt-dlp invocations
(resolve / download / metadata / self-update) through **one** `SemaphoreSlim`
(`ProcessGate`, count 1). Crucially, `RunYtDlpAsync` holds the gate for the entire
process lifetime, and a cache/prefetch **download** (`DownloadStreamsAsync` →
`DownloadOneAsync` ×2 + `GetResolutionAsync`) can run for **minutes** on a long video.
So even after BUG 2's deferral, a subsequent action that needs yt-dlp — e.g. **skipping
to the next track** (its `ResolveStreamsAsync`) or a chapters/metadata fetch — would
queue behind the in-flight download and trip the first-frame watchdog on that later play.

The gate's original purpose is narrow: stop the self-updater from swapping `yt-dlp.exe`
while a resolve/download is mid-flight.

**Fix applied:** split the single gate into two:

- `ProcessGate` — interactive invocations (resolve / metadata / version / update). Short-lived.
- `DownloadGate` — background cache/prefetch downloads only (`DownloadOneAsync`,
  the download-path `GetResolutionAsync`). Downloads stay serialized **among themselves**
  (one at a time, to avoid doubling network/CPU) but **no longer block interactive
  resolves**.
- The **updater acquires BOTH gates** for the `--update-to` step, preserving the original
  exe-swap protection against an in-flight download. No lock-order inversion: the download
  path never takes `ProcessGate`, and interactive callers never take `DownloadGate`; only
  the (rare) updater holds both, always `DownloadGate` outside `ProcessGate`.

**Files touched (BUG 2b):**

- `Phosphor.Plugins.YouTube/Engines/YtDlpVideoEngine.cs`
- `Phosphor.Plugins.YouTube/Engines/YtDlpUpdater.cs`

### Tradeoff — the gate split slightly widens concurrent yt-dlp requests

The single gate used to fully serialize yt-dlp **resolve** and **download**, so two yt-dlp
processes never hit YouTube at once. After the split, a download (`DownloadGate`) and a
resolve (`ProcessGate`) CAN run simultaneously — e.g. skipping to a new track (resolve)
while the previous track's cache download is still running. That is a modest increase in
the concurrent-request surface YouTube's anti-abuse heuristics watch, so it can *marginally*
raise 403 likelihood.

Context: this is secondary. The dominant 403 driver is the LibVLC **stream** + yt-dlp
**download** of the same item running concurrently — and that is **unchanged** by the split
(VLC streaming is not gated by yt-dlp at all, and never was). Resolves are brief and
infrequent (one per track change) vs. the heavy, sustained download. The split was a
deliberate tradeoff for responsiveness (BUG 2b: skip-to-next no longer hangs behind a long
download). Mitigation (1) below (`--limit-rate` / `--sleep-interval` on the download path)
directly offsets this by making downloads gentler.


### Note — yt-dlp search vs. the gates (expected, benign)

Relevant when the user selects **yt-dlp** as the search engine (default is
YouTubeExplode). Search does **not** get starved by playback or downloads:

- Main search / playlist / channel enumeration (`YtDlpSearchEngine.EnumerateAsync`) uses
  `RunYtDlpStreamingAsync`, which holds `ProcessGate` only long enough to **launch** the
  process, then releases it — so search results stream freely even while other yt-dlp work
  runs.
- Cache/prefetch downloads are on the separate `DownloadGate`, so they never block search
  at all.
- `ResolvePlaylistIdAsync` (resolve a playlist *by name*) does hold `ProcessGate` for its
  (short, single-item) duration, but it is an interactive action, correctly on the
  interactive gate.

The only shared-gate interaction that remains is **play-resolve vs. search**, both on the
interactive `ProcessGate`. If a search is launched at the exact moment a stream resolve is
in flight, it waits a couple seconds for the resolve to finish. This is expected and
benign (both are short, interactive operations); it is **not** the long-download starvation
that BUG 2/2b fixed.

---

# KNOWN ISSUE — YouTube 403 throttling on concurrent stream + download

**Status: open / partially mitigated. Environmental (YouTube-side), not a Phosphor bug.**

## Symptom

With `videoEngine = YtDlp` and caching **on**, playing an uncached YouTube item can:
- time out at the ~10s first-frame watchdog (resolve/stream path throttled), and/or
- start playing but the background cache download fails with
  `HTTP Error 403: Forbidden` (`unable to download video data`), leaving nothing cached.

Observed with repeated attempts on the same item (Journey / Rush). A partial file appears
in `cache/` then is deleted (the video stream downloaded but the audio stream 403'd, so
the muxer's cleanup removes the orphan).

## Why

`videoEngine` and `searchEngine` are configured **independently**. When `videoEngine =
YtDlp`, BOTH the playback **resolve** (`yt-dlp -g`) and the cache **download**
(`yt-dlp` full media pull) go through yt-dlp. YouTube rate-limits/403s **downloads** far
more aggressively than the lightweight resolve. Streaming the item AND downloading it for
cache at nearly the same time is a red flag to YouTube's anti-abuse heuristics — the
concurrent full-media pull can get the IP throttled, which then bleeds onto the resolve
path (explaining the occasional first-frame timeout even when only *streaming*).

Engine attribution is now logged (`YouTubeSource: resolve via <engine>` /
`download via <engine>`) so this is diagnosable at a glance — previously the play path
never said which engine it used, which made this confusing to attribute.

## Mitigation options (not yet implemented — for follow-up)

1. **yt-dlp built-in rate/throttle controls on the DOWNLOAD path** (cheapest, keeps
   downloads polite):
   - `--limit-rate 500K` — cap download speed so the pull isn't a burst.
   - `--throttled-rate 100K` — re-extract if speed drops below threshold (bypasses some
     ISP/site throttling).
   - `--sleep-interval 10 --max-sleep-interval 35` — random sleeps between downloads to
     avoid tripping anti-bot bans.
   These would be added to `DownloadOneAsync`'s arg list (download-only; never on resolve).
2. **"Play from cache only" setting** — when caching is on, download first and play the
   cached file, rather than streaming and downloading concurrently. Trades startup lag for
   a much lower throttling risk (only one yt-dlp pull instead of stream + download).
3. **403 back-off** — on repeated 403s for an item, stop re-attempting the download for a
   cooldown so we don't worsen the throttle, and surface a clear "YouTube throttled" status
   instead of a generic failure.

Recommended order: (1) is low-risk and likely sufficient for most cases; (2) is the
strongest guarantee but changes UX (startup lag); (3) is good hygiene regardless.




