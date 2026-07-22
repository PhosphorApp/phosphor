# Phase 7 — Cutover & Cleanup ✅ DONE

**Status:** Complete. Commit `35ca198`. Final phase — migration finished.
**Goal:** Decide defaults, add a safety net, remove dead code, refresh docs.

## Decisions (as made)
- **Default engines:** **no flip** — `VideoEngine` and `SearchEngine` stay `YoutubeExplode`
  (0/0) for now (more testing first). Users opt in to yt-dlp via Settings.
- **Existing installs:** **not overridden** — settings load from `settings.json`; defaults
  only apply to fresh installs, so saved choices are respected.
- **YoutubeExplode package:** **kept** — it's the default for both seams and the guaranteed
  `IsAvailable` fallback floor.
- **Safety net:** added an `IsAvailable` capability on each engine + factory fallback.

## What was delivered
- **`IsAvailable` capability** on `IVideoEngine` and `ISearchEngine` (a reusable engine-
  readiness hook; matches the existing `MandelbrotGpuRenderer`/`ProjectMRenderer` pattern).
  YoutubeExplode engines → `true` (in-process); yt-dlp engines → `File.Exists(exe)`.
- **Factory fallback** — `VideoEngineFactory` / `SearchEngineFactory` build the requested
  engine, and if `!IsAvailable`, log and return the YoutubeExplode engine. So selecting
  yt-dlp with a missing `yt-dlp.exe` silently falls back instead of hard-failing.
- **`StreamSelector` → internal** (only used by `YoutubeExplodeVideoEngine`).
- **Docs:** `AGENTS.md` tech stack now describes the dual selectable engines + seams, and
  the File Ownership map lists `Video/`, `Search/`, and the updater; `README.md` mentions
  selectable engines and adds a yt-dlp scrubbing tip to Known Issues.
- **Kept `App.xaml.cs` YoutubeExplode exception suppression** — still correct (YoutubeExplode
  is default; the exception-triggered dormant fallback was never built).

## Validated
- Build green. 4 engine `IsAvailable` impls correct; both factories fall back with logging;
  defaults unchanged (0/0); `StreamSelector` internal.

## Deferred / not done (by decision)
- Default flip to yt-dlp — later, after more testing (the `IsAvailable` safety net makes it
  safe when chosen).
- Dormant exception-triggered search fallback (§4a) + server-side `--match-filters` duration
  push-down — still open enhancements, not required for cutover.

## Migration complete
Phases 1–8 done. yt-dlp is a fully native, self-updating, selectable video **and** search
engine behind clean seams, defaulting to YoutubeExplode with an automatic fallback safety
net. Nothing pushed — all local on branch `yt-dlp`.
