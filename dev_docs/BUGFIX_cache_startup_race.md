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
