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
| 4 | yt-dlp video engine — live playback | ⬜ Not started | [phase-4-ytdlp-live.md](phase-4-ytdlp-live.md) | — |
| 5 | Metadata & native chapters | ⬜ Not started | [phase-5-metadata-chapters.md](phase-5-metadata-chapters.md) | — |
| 6 | `ISearchEngine` seam + optional yt-dlp search | ⬜ Not started | [phase-6-search-engine.md](phase-6-search-engine.md) | — |
| 7 | Cutover & cleanup | ⬜ Not started | [phase-7-cutover-cleanup.md](phase-7-cutover-cleanup.md) | — |

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

---

## 🧹 Cleanup / removal tracker

Actions to take **before or at Phase 7 (cutover)**. Check off as done.

- [x] **Remove `Phosphor/YtDlp/YtDlpSpike.cs`** — done in Phase 3 (commit `5a21e13`);
  replaced by `Phosphor/Video/YtDlpVideoEngine.cs`.
- [x] **Reassess `Phosphor/YtDlp/` folder** — deleted (empty after spike removal). The
  real engine lives under `Phosphor/Video/` alongside the seam, not `Phosphor/YtDlp/`.
- [ ] **`App.xaml.cs` YoutubeExplode exception suppression** (~L549) — revisit once the
  search engine may fail over (Phase 6); failures must be *observable* for fallback.
- [ ] **Prune YoutubeExplode package** — only if/when *both* seams no longer use it
  (Phase 7). Likely retained for search unless full yt-dlp search is adopted.
- [ ] **`StreamSelector` visibility** — currently public static; if it stays a private
  detail of `YoutubeExplodeVideoEngine`, consider making it internal (Phase 7 tidy).
- [ ] **Default engine flip** — decide whether `AppSettings.VideoEngine` default moves
  from `YoutubeExplode` to `YtDlp` at cutover (Phase 7).

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

_Last updated: Phase 3 complete (yt-dlp download path landed, spike removed, build green)._
