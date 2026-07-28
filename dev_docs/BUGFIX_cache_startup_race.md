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

