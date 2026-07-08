# Phase 8 — Engine Updater (yt-dlp self-update + version check) ✅ DONE

**Status:** Complete. Commit `33f4b65`. Shipped independently of Phases 6b/7.
**Goal:** Keep the engines fresh **between app releases**, with a Settings control to
auto-update and/or "Check now."

## Why this matters (and the key asymmetry)
The app will be released **infrequently**, but yt-dlp breaks often (YouTube changes) and
**can heal itself on the user's machine**. Without an updater, a months-old bundled
`yt-dlp.exe` may be broken on arrival — negating the resilience that was the whole point
of the yt-dlp path.

**Critical asymmetry — do not build symmetric UI that implies otherwise:**
- **yt-dlp** = standalone exe with a real self-updater (`yt-dlp --update-to stable`).
  Actionable at runtime, between releases. **This is the real feature.**
- **YoutubeExplode** = compiled-in NuGet dependency. **Cannot** be updated at runtime; a
  fix requires bumping the package and shipping a new build. The most the UI can do is
  *detect* a newer NuGet version and show an informational "update available — rebuild
  required" notice. **Do not present a "update YoutubeExplode now" action we can't honor.**

## Plan sketch (yt-dlp updater)
- Use yt-dlp's own updater — **do not** hand-roll GitHub download/replace:
  - Check/update: `yt-dlp --update-to stable` (no-ops if current). Capture `--version`
	before/after to report the delta.
- Trigger modes:
  - **Check now** button in Settings (General → VIDEO, near the Engine dropdown) with
	status text ("Updated 2026.07.04 → 2026.09.01" / "Already current" / "Failed: offline").
  - **Auto-update** (opt-in) on startup, throttled + off the UI thread.
- Reuse `YtDlpVideoEngine.ResolveYtDlpPath()` + the existing `ProcessStartInfo` plumbing.

## Design gotchas to decide
1. **File-in-use:** can't update `yt-dlp.exe` while a resolve/download process is running
   against it. Serialize update vs. use (gate), ideally update at startup before playback.
2. **Write permissions:** the bundled exe sits in the install dir (may be read-only, e.g.
   Program Files). Consider running the *working* yt-dlp from a user-writable app-data path,
   seeded from the bundled copy on first run, so self-update can write.
3. **Settings model:** `YtDlpAutoUpdate` (bool) + `YtDlpLastUpdateCheck` (timestamp, to
   throttle) + optional pinned-channel choice (stable vs nightly).

## Optional (YoutubeExplode — notice only)
- A version check against nuget.org that surfaces "newer YoutubeExplode available (rebuild
  required)" — informational, no action button. Low priority; only if it adds real value.

## Validation
- "Check now" updates a stale bundled exe and reports the version delta; no-ops when current.
- Auto-update respects the throttle and never blocks the UI thread.
- A resolve/download in flight does not corrupt an in-progress update (gate works).

## Outcome (as built)
- **Write-permission decision:** the "read-only Program Files / app-data-copy" concern
  was resolved as **not applicable** — the app already writes `settings.json` into its own
  `BaseDirectory` and runs `DofBridge.exe` from there, i.e. it's a portable/cabinet-style
  app with a **writable install dir**. yt-dlp self-updates **in place**; no indirection.
- **Concurrency gate:** `YtDlpVideoEngine.ProcessGate` (`SemaphoreSlim(1,1)`) + static
  `RunYtDlpAsync` now serialize *every* yt-dlp invocation (resolve / download / metadata /
  update). `YtDlpUpdater` uses the same static runner, so an update waits for in-flight work.
- **`YtDlpUpdater`:** `GetVersionAsync` (`--version`) + `UpdateAsync` (`--update-to stable`,
  capturing before/after version) → `YtDlpUpdateResult` (`Updated` / `AlreadyCurrent` /
  `Failed`) with `ToDisplayString()` for the UI. Never throws to the caller.
- **Settings UI:** General → VIDEO, under the Engine dropdown — "Check for yt-dlp updates"
  button + status text + "Automatically check … on startup" checkbox. Async handler,
  non-blocking.
- **Startup auto-update:** `App.MaybeAutoUpdateYtDlp()` — fires only if `YtDlpAutoUpdate`
  is on **and** a yt-dlp engine is selected **and** last check > 7 days ago; stamps
  `YtDlpLastUpdateCheck` and runs `UpdateAsync` fire-and-forget (persisted on exit).
- **Validated:** full command sequence (`--version` → `--update-to stable` → `--version`)
  produced "Already current (2026.07.04)" as expected; build green.
- **YoutubeExplode notice:** skipped (optional/low-value); the asymmetry is documented so
  no misleading "update YoutubeExplode" action was added.
