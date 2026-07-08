# yt-dlp Migration — Master Tracker

> **Purpose:** durable, resumable state for the multi-phase yt-dlp migration. If a
> session crashes or a new session picks this up, **start here.** This is the index;
> each phase has its own plan doc in this folder.
>
> **Branch:** `yt-dlp` (all work local — do **not** push unless explicitly asked).
> **Analysis source of truth:** [`../../YT-DLP_MIGRATION_ANALYSIS.md`](../../YT-DLP_MIGRATION_ANALYSIS.md)

---

## 📊 Phase status board

| Phase | Title | Status | Doc | Commit(s) |
|------:|-------|--------|-----|-----------|
| 1 | Spike / feasibility | ✅ Done | [phase-1-spike.md](phase-1-spike.md) | `baf619f`, `65795ce` |
| 2 | `IVideoEngine` seam (no behavior change) | ✅ Done | [phase-2-video-engine-seam.md](phase-2-video-engine-seam.md) | `56805ee` |
| 3 | yt-dlp video engine — download path | ✅ Done | [phase-3-ytdlp-download.md](phase-3-ytdlp-download.md) | `5a21e13` |
| 4 | yt-dlp video engine — live playback | ✅ Done | [phase-4-ytdlp-live.md](phase-4-ytdlp-live.md) | `e0cc5f7` |
| 5 | Metadata & native chapters | ✅ Done | [phase-5-metadata-chapters.md](phase-5-metadata-chapters.md) | `768e136` |
| 6 | `ISearchEngine` seam (wrap YoutubeExplode, no behavior change) | ✅ Done | [phase-6-search-engine.md](phase-6-search-engine.md) | `d1191fd` |
| 6b | yt-dlp search impl + dormant fallback (optional) | ✅ Done (search impl; fallback deferred) | [phase-6-search-engine.md](phase-6-search-engine.md) | `eb837b7` |
| 7 | Cutover & cleanup | ✅ Done | [phase-7-cutover-cleanup.md](phase-7-cutover-cleanup.md) | `35ca198` |
| 8 | Engine updater (yt-dlp self-update + version check) | ✅ Done | [phase-8-engine-updater.md](phase-8-engine-updater.md) | `33f4b65` |

Legend: ⬜ not started · 🚧 in progress · ✅ done · ⏸️ blocked

---

## 🧭 How to resume (new session checklist)

1. `git status` and `git log --oneline -8` on branch `yt-dlp` to see where things stand.
2. Read this tracker's status board, then open the **in-progress** phase doc.
3. Each phase doc has its own **Steps** + **Resume-from** notes — continue there.
4. After finishing a phase: update the status board, the changed-files ledger, and the
   cleanup tracker below, then commit locally.

---

## 📁 Changed-files ledger (cumulative, all phases)

Every file added/modified by the migration, with the phase and disposition.
**Disposition:** `keep` = stays in final product · `remove` = delete before/at cutover
· `revert` = restore to original at cutover.

### Phase 1 (spike) — commits `baf619f`, `65795ce`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `YT-DLP_MIGRATION_ANALYSIS.md` | added | keep | Analysis + appendices (design source of truth) |
| `dependencies/yt-dlp.exe` | added (17.4 MB) | **keep** | Bundled runtime, tracked like `ffmpeg.exe` |
| `Phosphor/Phosphor.csproj` | modified | keep | Copy-to-output item for `yt-dlp.exe` |
| `Phosphor/YtDlp/YtDlpSpike.cs` | added, then **removed in Phase 3** | done | Throwaway Option-B resolver; superseded by `YtDlpVideoEngine` |

### Phase 2 (video engine seam) — commit `56805ee`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `docs/yt-dlp/*` | added | keep | This tracker + per-phase docs |
| `Phosphor/Video/IVideoEngine.cs` | added | keep | Seam interface + DTOs (`VideoStreams`, `VideoStreamKind`, `VideoDownload`) |
| `Phosphor/Video/YoutubeExplodeVideoEngine.cs` | added | keep | Wraps current YoutubeExplode resolve + download logic |
| `Phosphor/Video/VideoEngineFactory.cs` | added | keep | `VideoEngineKind` → engine (YtDlp falls back to YT-Explode until Phase 3) |
| `Phosphor/Models/AppSettings.cs` | modified | keep | Added `VideoEngineKind` enum + `VideoEngine` setting (default YoutubeExplode) |
| `Phosphor/default_settings.json` | modified | keep | Added `"VideoEngine": 0` |
| `Phosphor/JukeboxViewModel.cs` | modified | keep | `_videoEngine` + `VideoEngine` prop + `SetVideoEngine`; seeds caches |
| `Phosphor/App.xaml.cs` | modified | keep | `SetVideoEngine` at startup (before `SetupCache`) |
| `Phosphor/Windows/DmdWindow.xaml.cs` | modified | keep | `SetVideoEngine` on settings-change |
| `Phosphor/Caching/VideoCache.cs` | modified | keep | Download via engine; added `VideoEngine` prop; dropped `_youtube` |
| `Phosphor/Caching/PrefetchCache.cs` | modified | keep | Download via engine; added `VideoEngine` prop; dropped `_youtube` |
| `Phosphor/Windows/BackglassWindow.xaml.cs` | modified | keep | Live resolve via engine; overlay by resolution string; dropped `_youtube` |

**Net effect:** all three YouTube *video* call sites route through `IVideoEngine`.
`StreamSelector` + all `Videos.Streams.*` calls are now isolated inside
`YoutubeExplodeVideoEngine`. Default engine = YoutubeExplode → **no behavior change**.
Build green.

### Phase 3 (yt-dlp download path) — commit `5a21e13`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/YtDlpVideoEngine.cs` | added | keep | Native yt-dlp download; live playback delegated to YoutubeExplode (Phase 4 replaces) |
| `Phosphor/Video/VideoEngineFactory.cs` | modified | keep | `YtDlp` → `YtDlpVideoEngine` |
| `Phosphor/YtDlp/YtDlpSpike.cs` | **deleted** | done | Spike removed; folder deleted |

**Net effect:** with `VideoEngine=YtDlp`, `VideoCache`/`PrefetchCache` download via
yt-dlp (separate video-only + audio-only streams, caches mux as before). Live
playback still uses YoutubeExplode via delegation until Phase 4. Default engine
unchanged → **no behavior change** by default. Build green.

### Phase 3.1 (Settings UI toggle) — commit `973e96f`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Windows/SettingsWindow.xaml` | modified | keep | "Engine" dropdown in General → VIDEO section (mirrors Quality combo) |
| `Phosphor/Windows/SettingsWindow.xaml.cs` | modified | keep | Load/save `VideoEngine`; `CbVideoEngine_SelectionChanged` + `UpdateEngineHint` |

**Net effect:** engine is now selectable interactively (YoutubeExplode / yt-dlp).
`DmdWindow` already calls `SetVideoEngine` on settings-apply, so the switch takes
effect without a restart. Default (index 0 = YoutubeExplode) unchanged.

### Phase 4 (yt-dlp live playback) — commit `e0cc5f7`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/YtDlpVideoEngine.cs` | modified | keep | Native `ResolveStreamsAsync` via `-g`; removed `_liveFallback` delegation |

**Net effect:** with `VideoEngine=YtDlp`, live playback now resolves URLs natively
(single yt-dlp spawn: resolution + video/audio or muxed URLs). The yt-dlp engine is
now **fully native** (download + live). Default engine unchanged → no behavior change
by default. Build green.

### Phase 5 (metadata & native chapters) — commit `768e136`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/IVideoEngine.cs` | modified | keep | Added `GetMetadataAsync` + `VideoMetadata` DTO |
| `Phosphor/Video/YoutubeExplodeVideoEngine.cs` | modified | keep | Metadata via `Videos.GetAsync`; empty native chapters (description fallback in VM) |
| `Phosphor/Video/YtDlpVideoEngine.cs` | modified | keep | Metadata via `--dump-single-json`; native structured `chapters[]` |
| `Phosphor/JukeboxViewModel.cs` | modified | keep | Chapters/duration via engine; native-first, description-parse fallback; `_youtube` now search-only |

**Net effect:** the `Videos.GetAsync` metadata call moved behind the engine seam.
With `VideoEngine=YtDlp`, chapters come from yt-dlp's native `chapters[]` (falling back
to description parsing when absent); YoutubeExplode path is identical to before (always
description parse). `ParseYouTubeChapters` stays single-sourced in the VM as the shared
fallback. Build green.

### Phase 6 (ISearchEngine seam) — commit `d1191fd`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Search/ISearchEngine.cs` | added | keep | Discovery seam (search, playlist, channel, playlist-id resolve) |
| `Phosphor/Search/YoutubeExplodeSearchEngine.cs` | added | keep | Wraps YoutubeExplode search/playlist/channel + `IVideo`→`VideoItem` mapping + fallbacks |
| `Phosphor/Search/SearchEngineFactory.cs` | added | keep | `SearchEngineKind` → engine (YtDlp falls back to YT-Explode until Phase 6b) |
| `Phosphor/Models/AppSettings.cs` | modified | keep | Added `SearchEngineKind` enum + `SearchEngine` setting (default YoutubeExplode) |
| `Phosphor/default_settings.json` | modified | keep | Added `"SearchEngine": 0` |
| `Phosphor/JukeboxViewModel.cs` | modified | keep | Search/playlist/channel/AutoDJ via engine; `_searchEnumerator`→`VideoItem`; dropped `_youtube` + YT-Explode usings |
| `Phosphor/App.xaml.cs` | modified | keep | `SetSearchEngine` at startup |
| `Phosphor/Windows/DmdWindow.xaml.cs` | modified | keep | `SetSearchEngine` on settings-change |

**Net effect:** all YouTube discovery routes through `ISearchEngine`. `_youtube` is gone
from the ViewModel; the only `YoutubeClient` + `Search/Playlists/Channels` calls now live
inside `YoutubeExplodeSearchEngine`. The engine yields `VideoItem` directly, so the
pagination/duration-filter/cache/prefetch pipeline is unchanged. Default engine =
YoutubeExplode → **no behavior change**. Build green. (yt-dlp search impl + dormant
fallback deferred to Phase 6b.)

### Phase 6b (yt-dlp search engine) — commit `eb837b7`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/YtDlpVideoEngine.cs` | modified | keep | Added `RunYtDlpStreamingAsync` (line-by-line stdout; gate held only at launch) |
| `Phosphor/Search/YtDlpSearchEngine.cs` | added | keep | `ISearchEngine` via yt-dlp `ytsearch`/playlist/channel; JSONL → `VideoItem` |
| `Phosphor/Search/SearchEngineFactory.cs` | modified | keep | `YtDlp` → `YtDlpSearchEngine` |
| `Phosphor/JukeboxViewModel.cs` | modified | keep | Engine-aware `SearchPageSize` (yt-dlp 50, YT-Explode 25) |
| `Phosphor/Windows/SettingsWindow.xaml` | modified | keep | "Search:" engine dropdown |
| `Phosphor/Windows/SettingsWindow.xaml.cs` | modified | keep | Search-engine load/save + handler + hint |

**Net effect:** search is now switchable (YoutubeExplode | yt-dlp) via Settings, mirroring
the video engine. yt-dlp search streams `--flat-playlist --dump-json` JSONL line-by-line
into the same `IAsyncEnumerable<VideoItem>` pipeline, so pagination + the app's
`min:`/`max:`/title/channel filters work unchanged (they're VM-level, engine-agnostic).
Page size doubles for yt-dlp to offset per-page process-spawn latency. Default
(YoutubeExplode) unchanged → no behavior change by default. Build green.

**Known limitations / deferred (yt-dlp search):**
- **Playlist-by-name** (`playlist:"Some Name"`) returns null → "could not find" (yt-dlp
  search returns videos, not playlist entities). Direct playlist id/URL works.
- **Dormant exception fallback** (analysis §4a) not built — the App.xaml.cs YoutubeExplode
  exception suppression must be revisited first (see cleanup tracker).
- **Server-side `--match-filters` duration** optimization deferred (client-side parity
  for now); see phase-6 doc.
- **Test point:** verify the doubled page size (50) keeps scroll fetches infrequent
  enough; tune if the ~2s per-page spawn still feels laggy.

### Phase 8 (engine updater) — commit `33f4b65`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/YtDlpVideoEngine.cs` | modified | keep | Shared `ProcessGate` + static `RunYtDlpAsync` serializing all yt-dlp invocations |
| `Phosphor/Video/YtDlpUpdater.cs` | added | keep | `GetVersionAsync` + `UpdateAsync` (`--update-to stable`) behind the gate |
| `Phosphor/Models/AppSettings.cs` | modified | keep | `YtDlpAutoUpdate` + `YtDlpLastUpdateCheck` |
| `Phosphor/default_settings.json` | modified | keep | `"YtDlpAutoUpdate": false` |
| `Phosphor/Windows/SettingsWindow.xaml` | modified | keep | Dedicated UPDATES section: "Check for yt-dlp updates" button + status + auto-update checkbox |
| `Phosphor/Windows/SettingsWindow.xaml.cs` | modified | keep | Load/save + async `BtnCheckYtDlpUpdate_Click` |
| `Phosphor/App.xaml.cs` | modified | keep | Throttled (7-day) background auto-update at startup |

**Net effect:** yt-dlp can self-update via its own `--update-to stable`, either on demand
("Check for updates" button) or opt-in at startup (throttled, background, off the UI
thread). All yt-dlp processes (resolve/download/metadata/update) serialize through a shared
`SemaphoreSlim` so an update never collides with in-flight work. **Design note:** the app
writes `settings.json` next to its exe (portable/cabinet-style, writable install dir), so
yt-dlp self-updates **in place** — no app-data-copy indirection needed. YoutubeExplode is
compiled-in and intentionally has no update action. Build green.

### Phase 7 (cutover & cleanup) — commit `35ca198`
| File | Change | Disposition | Notes |
|------|--------|-------------|-------|
| `Phosphor/Video/IVideoEngine.cs` | modified | keep | Added `IsAvailable` capability |
| `Phosphor/Video/YoutubeExplodeVideoEngine.cs` | modified | keep | `IsAvailable => true` |
| `Phosphor/Video/YtDlpVideoEngine.cs` | modified | keep | `IsAvailable => File.Exists(exe)` |
| `Phosphor/Video/VideoEngineFactory.cs` | modified | keep | Fallback to YoutubeExplode when unavailable |
| `Phosphor/Search/ISearchEngine.cs` | modified | keep | Added `IsAvailable` capability |
| `Phosphor/Search/YoutubeExplodeSearchEngine.cs` | modified | keep | `IsAvailable => true` |
| `Phosphor/Search/YtDlpSearchEngine.cs` | modified | keep | `IsAvailable => File.Exists(exe)` |
| `Phosphor/Search/SearchEngineFactory.cs` | modified | keep | Fallback to YoutubeExplode when unavailable |
| `Phosphor/Services/StreamSelector.cs` | modified | keep | `public` → `internal` (engine-private) |
| `AGENTS.md` | modified | keep | Dual-engine tech stack + engine seams in ownership map |
| `README.md` | modified | keep | Selectable engines; yt-dlp scrubbing tip in Known Issues |

**Net effect:** each engine exposes an `IsAvailable` capability (YoutubeExplode always
true; yt-dlp = exe present). Both factories fall back to the always-available YoutubeExplode
engine when a selected yt-dlp engine can't run, so playback/search never hard-fail — the
safety net for a missing/undeployed `yt-dlp.exe`. Defaults **stay YoutubeExplode** (no flip;
existing installs untouched — users opt in via Settings). `StreamSelector` is now internal.
Docs refreshed. Build green.

---



Actions to take **before or at Phase 7 (cutover)**. Check off as done.

- [x] **Remove `Phosphor/YtDlp/YtDlpSpike.cs`** — done in Phase 3 (commit `5a21e13`);
  replaced by `Phosphor/Video/YtDlpVideoEngine.cs`.
- [x] **Reassess `Phosphor/YtDlp/` folder** — deleted (empty after spike removal). The
  real engine lives under `Phosphor/Video/` alongside the seam, not `Phosphor/YtDlp/`.
- [x] **yt-dlp self-update mechanism** — done in Phase 8: `YtDlpUpdater` + Settings
  "Check for updates" button + opt-in throttled startup auto-update; in-place update
  (writable install dir), gated against concurrent yt-dlp use.
- [x] **`App.xaml.cs` YoutubeExplode exception suppression** (~L549) — **kept as-is** in
  Phase 7. The dormant search-fallback (§4a) was not built, and YoutubeExplode remains the
  default, so the suppression is still correct. Revisit only if the exception-triggered
  fallback is ever implemented.
- [x] **Prune YoutubeExplode package** — **kept** (Phase 7 decision). It's the default for
  both video and search, and the guaranteed `IsAvailable` fallback floor. No prune.
- [x] **`StreamSelector` visibility** — made **internal** in Phase 7 (only used by
  `YoutubeExplodeVideoEngine`, same assembly).
- [x] **Default engine flip** — **no flip** (Phase 7 decision). Defaults stay YoutubeExplode
  for both video and search (more testing first); existing installs untouched. Users opt in
  to yt-dlp via Settings. A startup **safety net** (`IsAvailable` + factory fallback) makes a
  future flip safe.

---

## 🔑 Key cross-phase decisions (living list)

- **Two seams, not one** — `IVideoEngine` (resolve/download) and `ISearchEngine`
  (search/metadata) are independent. Video moves first; search last/optional.
- **`StreamSelector` stays engine-private** — yt-dlp selects formats via `-f`
  expressions, so a shared "neutral format DTO" is **not** required for the seam.
  Each engine owns its own selection. (Supersedes the neutral-DTO idea sketched in
  the analysis Appendix A; revisit only if a shared selector proves useful.)
- **Download seam returns raw files** — the caches keep their own mux/index/eviction.
  yt-dlp's one-shot download+remux is layered in at Phase 3 without changing callers.
- **Default = YoutubeExplode** — every phase keeps the app behavior-identical until a
  setting is explicitly flipped.

---

_Last updated: Phase 7 complete — migration finished (all phases done)._
