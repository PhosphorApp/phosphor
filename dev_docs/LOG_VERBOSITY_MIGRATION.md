# Phosphor — Log Verbosity Level Migration

A living document for the **incremental migration of Phosphor's logging to leveled verbosity**.
Work this in small, low-risk passes over time — one subsystem/category per session is fine. This
doc is the shared starting point: it carries the purpose, the working prompt, the rules, a running
log of what's been done, and a candidate backlog.

> Related: logging primitives live in [`Phosphor/Services/DebugLog.cs`](../Phosphor/Services/DebugLog.cs).
> The setting is `AppSettings.DebugLogLevel`; the UI is the **Verbosity** dropdown in the Debug section
> of `SettingsWindow`.

---

## Purpose

Historically, diagnostic logging was added ad-hoc during investigations and then ripped back out
afterward (the add → verify → strip → re-add churn). We introduced a `LogLevel` system so diagnostics
can stay **permanently in the code** and simply go quiet at the default level, surfacing on demand
when a tester/dev raises verbosity.

The goal of this migration is to **retag existing `DebugLog.Log(...)` call sites with an appropriate
level** so that:

- Normal runs (default `Debug`) are readable and not flooded by per-frame/per-item spam.
- Genuine problems still show at the default level.
- Deep diagnostics are one setting-change away, never a rebuild away.

This is **not** a rewrite. Existing calls keep working unchanged; we retag opportunistically.

---

## Instructions for AI Agents

To work this migration, a user may simply say: *"review the LOG_VERBOSITY_MIGRATION doc and follow
the AI instructions."* Then do the following:

> We're incrementally migrating Phosphor's logging to leveled verbosity (see
> `dev_docs/LOG_VERBOSITY_MIGRATION.md`). Pick the next candidate from the backlog in that doc (or one
> the user names). For each `DebugLog.Log(...)` call in that subsystem, assign the right `LogLevel` using
> the Level Rubric in the doc, keeping edits minimal and behavior otherwise unchanged. Prefer the
> `Log(LogLevel, category, message)` overload. Don't touch unrelated code. Build to verify, then update
> the "Work Log" and "Candidates" sections in the doc (move the finished item to Done, note anything
> discovered). Keep it to one or two subsystems this pass — small and safe.

When prioritizing, weigh **runtime volume** (how many lines a category actually emits, see the
Candidates table) more heavily than raw call-site count — a category with two call sites in a hot loop
can dominate the log.

---

## Level Rubric

Levels are defined in `DebugLog.cs` as `LogLevel { Trace, Debug, Info, Warning, Error }`.
Default `MinimumLevel` is `Debug`.

**Every log line is now stamped with its tag** right after the timestamp, so logs are filterable
after the fact (e.g. `Select-String '\[Warning\]'`):

```
[22:25:29.158] [Info] [RebuildCategories] Build list (25 items): 0ms      ← migrated, explicit level
[22:25:29.201] [GENERIC] [SomeCategory] ...                               ← NOT yet migrated
```

- `[Trace]` / `[Debug]` / `[Info]` / `[Warning]` / `[Error]` — an **explicit** level was passed.
- `[GENERIC]` — a **legacy/untagged** call (`Log(string)` / `Log(category, message)`). Still filtered
  at Debug severity, but the distinct label makes it trivial to find what's left to migrate:
  `Select-String '\[GENERIC\]'` on a log (or grep the source for `DebugLog.Log("` without a level).
  **The goal of the migration is to drive `[GENERIC]` toward zero.**

| Level | Use for | Examples |
|-------|---------|----------|
| **Trace** | Very chatty, per-frame / per-item / per-tick diagnostics. Silent by default. | Thumbnail loads per row, cache hit/miss per item, prefetch bookkeeping, render-tick detail, seek/scrub deltas. |
| **Debug** | Developer-facing detail that's useful but not spammy. The historical default. | Source resolution decisions, engine selection, one-per-action cache stores, plugin load steps. |
| **Info** | Notable milestones a curious user would want. | App start/version, settings applied, source registered, DOF connected, cache invalidated/purged. |
| **Warning** | Recoverable problems / fallbacks. Shows at default level. | Decode fallback, load miss/retry, reconnect attempts, config value missing → default used. |
| **Error** | Failures and exceptions. | `LogException` (already Error), unrecoverable operation failures. |

Rules of thumb:
- **If it fires more than ~once per user action, it's probably `Trace`.**
- **If losing it would hide a real bug, it's at least `Warning`.**
- Keep the existing `[Category]` tag; only add the level. Prefer `Log(LogLevel, "Category", msg)`.
- Don't invent new categories during a retag pass — that's a separate cleanup.
- When in doubt between two levels, pick the quieter one for chatter and the louder one for problems.

---

## Conventions

- Existing `DebugLog.Log("Cat", msg)` and `DebugLog.Log(msg)` default to **Debug** — leaving a call
  untouched is safe, it just stays at Debug.
- `LogException(...)` is already **Error**; no need to touch those.
- Verify each pass with a build. No behavioral changes expected beyond log routing.

### Guard expensive log-argument computation

The level/enabled gate lives **inside** `DebugLog.Log`, so anything you pass as an argument is
computed **before** the gate can reject it (C# evaluates arguments first). For the vast majority of
calls this is irrelevant — a `$"..."` interpolation of a couple of fields is cheap and fine to leave
unguarded even at `Trace`.

But when a log call's arguments do **non-trivial work**, guard the whole call so that work is skipped
when it wouldn't be logged. Examples of "non-trivial": `string.Join` over a collection, LINQ, an
expensive `ToString()`, a classifier/formatter, allocation-heavy string building.

```csharp
// Cheap args → leave unguarded (the gate handles it):
DebugLog.Log(LogLevel.Trace, "Cat", $"item {id} took {ms}ms");

// Expensive args → guard so the work is skipped when logging is off / below level:
if (DebugLog.Enabled)                                   // logging off is the common prod state
    DebugLog.Log(ClassifyLevel(msg), "Status", msg);    // classifier + call skipped entirely

// Guard on level too when the payload itself is expensive and only wanted at a low level:
if (DebugLog.Enabled && LogLevel.Trace >= DebugLog.MinimumLevel)
    DebugLog.Log(LogLevel.Trace, "Cat", string.Join(", ", bigList.Select(x => x.Dump())));
```

Real example: the `Status` classifier (`ClassifyStatusLevel` in `JukeboxViewModel`) is gated with
`if (DebugLog.Enabled)` — not for CPU cost (it runs at user-action frequency), but because computing a
level only to discard it when logging is off is the wrong pattern. Rule of thumb: **cheap args = trust
the gate; expensive args = guard the call.**

### Prefer a real `[Category]` over an inline message prefix

Some legacy calls baked the source name into the message text, e.g.
`DebugLog.Log($"{GetType().Name}: monitor … running at {hz} Hz")` → `[GENERIC] BackglassWindow: monitor …`.
That's ambiguous (is `BackglassWindow` the category or free text?) and not filterable. When retagging,
lift the prefix into the category argument: `DebugLog.Log(level, GetType().Name, "monitor … 144 Hz")`
→ `[Info] [BackglassWindow] monitor … 144 Hz`. `GetType().Name` is a fine dynamic category for
base-class logging shared by several window subclasses (it names the concrete window that logged).

---

## Work Log

Newest first. Note the date, what was retagged, and anything discovered.

- **2025 — YouTube Path-B shim (migration effectively complete).** Made the YouTube plug-in's own
  `DebugLog` shim (`Phosphor.Plugins.YouTube/Engines/DebugLog.cs`) level-aware — added the mirrored
  `LogLevel` enum, a `MinimumLevel` gate (default Debug), and `Log(LogLevel, message)` /
  `Log(LogLevel, category, message)` overloads that stamp the real tag instead of `[GENERIC]`, exactly
  mirroring `PlexDebugLog.cs`. **No contract change** — the contract already has `IPluginHost.Log(LogLevel,
  string)` for Path-A, but these engines are Path-B (static shim → `Trace.WriteLine`, no `_host` ref), so
  the consistent minimal move is the in-shim retag Plex set the precedent for. Retagged all 17 sites:
  **YtDlpVideoEngine** (8) metadata/download/resolve failures → Warning, the two `yt-dlp` command echoes
  → Trace; **YtDlpUpdater** (4) version-check/update failures → Warning, update-result milestone → Info;
  **StreamSelector** (3) per-stream dumps → Trace; **SearchEngineFactory** / **VideoEngineFactory** (1
  each) fallback → Warning. Dropped the unmigrated caller count 19 → **2**, and those two are
  intentionally level-less (the `PluginHost` routing method forwarding `MapLevel(level)`, and the
  `JukeboxViewModel` Status classifier forwarding `ClassifyStatusLevel(value)`). **The `[GENERIC]`
  retag is effectively done.** Caveat (same as Plex): these lines go to `Trace.WriteLine`, not the host
  log file — the retag is for debugger-output readability + consistency. See the Path-B follow-up below.
- **2025 — Misc host sweep (host backlog cleared).**
  across 13 files. **Warning:** all the persistence/IO failure sites — `FavoritesIndex`
  (Load/Save/LoadOrder/SaveOrder), `GenreCategoryStore` (load + both save paths), `PinupSettings`
  (Load/Save), `AppSettings` (Save/SaveAsync), `SecretProtector` (Protect/Unprotect), `PinupDatabase`
  (playlist query), `Pinup` load-failed — plus the "No {playfield|backglass|topper} video for"
  ambient-Pinup misses. **Trace:** the `Playfield` / `Topper` `RandomPerSong` blob transitions (match
  Backglass). **Debug:** `Network` timeout-set, `PreemptiveCache` job-start, `Pinup` load-skipped.
  **Info:** `Pinup` load-complete milestone, `PluginHost.ReportStatus` (plugin status). **Left as-is
  (correctly):** `JukeboxViewModel` `ClassifyStatusLevel(value)` (the Status classifier — already
  leveled) and `PluginHost.MapLevel` line (the routing method that forwards a plug-in's level).
  Dropped the unmigrated caller count ~44 → **19**, all of which are now the **Path-B YouTube shim**
  (`YtDlpVideoEngine` 8, `YtDlpUpdater` 4, `StreamSelector` 3, `SearchEngineFactory`/`VideoEngineFactory`
  1 each) — the last remaining track. **The host backlog is effectively cleared.**
- **2025 — Visualization pass.**
  (4): target-switch + `LogGpu` helper + boundary-point discovered → **Debug**; discovery-failed
  fallback → **Warning** (lifted the inline `Mandelbrot:` prefix into the category). **MatrixBlobPattern**
  (3): per-color-cycle / PulseDominantColor detail → **Trace**. **ProjectMPattern** / **ProjectMRenderer**
  / **MandelbrotGpuRenderer** `Log`/`LogGpu` helpers → **Debug**. **GameOfLifePattern** (1): the frame-
  timing window summary → **Trace** (lifted `[GoL]` prefix into category). **EmojiRenderer**
  (`Emoji/EmojiRenderer.cs`, 3): font-missing / typeface-load / render failures → **Warning**.
  **Discovery:** a second stray empty `Visuals/EmojiRenderer.cs` (the real one is `Emoji/`) — removed.
  Dropped the unmigrated caller count ~59 → ~44.
- **2025 — PresetBrowser + SettingsWindow + PrefetchCache pass.**
  the `BtnUnfavoriteFolder_Click` entry → **Debug**, its indented per-step detail (early returns,
  folderPath, file count, per-file move) → **Trace**. **SettingsWindow** (6 sites): all failure sites
  (`Pinup` playlist-load / BuildGameList, `Settings` Apply/Save/Close/SettingsApplied handler
  failures) → **Warning**. **PrefetchCache** (`Caching/PrefetchCache.cs`, 6 sites): per-item
  consumed/ready → **Trace**; mux/ffmpeg/failed sites → **Warning**. **Discovery:** there are two
  `PrefetchCache.cs` files (`Caching/` and `Services/`) — the active one is `Caching/`; `Services/`
  has no log calls. Dropped the unmigrated caller count ~77 → ~59.
- **2025 — GaplessAudioPlayer + DmdWindow pass.**
  12 sites, all `GaplessPCM`): per-track lifecycle milestones (Play, primed-next, decoder
  switch/start) → **Debug**; per-callback / per-buffer internals (leading/trailing silence trim,
  `cb#` peak dumps, flush/drain/EndReached queue bookkeeping) → **Trace**. **DmdWindow**
  (`DmdWindow.xaml.cs`, 9 sites): `ScrubBar` drag-start/complete + IsSeeking toggles → **Trace**;
  `Settings` apply-handler failures, `[DOF]` bridge start/startup-trigger failures, and
  `EmojiKeywords` load failure → **Warning**. Dropped the unmigrated caller count ~98 → ~77.
- **2025 — BackglassWindow pass
  as one pass. **Trace:** the whole `Seek` diagnostic cluster (PCM gapless seek, requested/skipped,
  fast-check/verify samples), the `RandomPerSong` blob `Backglass` transition, and the `#if DEBUG`
  `PERF.LogoMorph` timing dumps. **Debug:** `Gapless` primed-next + swap-to-primed, `GaplessPCM`
  playing-via-queue, `Seek` cache-ready switch. **Info:** `GaplessPCM` playback-finished (all tracks
  drained). **Warning:** `Seek` not-seekable + seek-failed restarts, and `PERF.BackglassStall` render-
  gap markers (kept as Warning per the backlog — don't hide stalls). The volume-slider handler was
  already Trace (see Follow-up). Dropped the unmigrated caller count 114 → ~98.
- **2025 — VideoCache + window monitor notifications.**
  hit/miss/store/evict/chapters → Trace; store/evicting → Debug; purge + index-verification-complete →
  Info; ffmpeg/mux/download failures → Warning. **Window notifications:** the monitor-Hz + refresh-rate
  logs in `JukeboxWindow` (base class) used `GetType().Name` as an inline message prefix, producing
  ambiguous `[GENERIC] BackglassWindow: monitor …` lines. Converted to
  `Log(level, GetType().Name, msg)` so the concrete window becomes a proper **`[Category]`** →
  `[Info] [BackglassWindow] monitor … 144 Hz` (refresh-rate detection failure → Warning). Dropped the
  unmigrated caller count 131 → 113.
- **2025 — DOF pass (`DofClient.cs`, 27 sites).** Largest single-file cluster, done as one cohesive
  pass. **Trace:** per-trigger sends (`Trigger`, `Auto-off`, `Reconnect auto-off`) and bridge stdout
  passthrough (`[DOF-Bridge]`). **Debug:** args, using-bridge detail. **Info:** connect/reconnect/
  reconnected, connection-lost, reconnect attempt, shutdown, cleanup complete, consumer exited, bridge
  normal exit. **Warning:** handler/reset/trigger/shutdown/cleanup failures, exe-not-found, exited-
  immediately, failed-to-start, enqueue-fail, bridge stderr (`[DOF-Bridge-ERR]`), kill-on-timeout.
  **Error:** reconnect gave up after max attempts. Dropped the unmigrated caller count 158 → ~131.
- **2025 — Gate expensive log-argument work + convention.** Gated the `Status` classifier with
  `if (DebugLog.Enabled)` so it isn't computed when logging is off (the common prod state). Added a
  "Guard expensive log-argument computation" convention: the level/enabled gate is inside `DebugLog.Log`,
  so arguments are evaluated first — cheap interpolation is fine unguarded, but non-trivial arg work
  (string.Join/LINQ/classifier/expensive ToString) should be guarded. Also corrected an earlier
  convention bullet that implied the gate runs before caller-side formatting.
- **2025 — ApplySettings + Status + PluginLoader pass.** **ApplySettings:** the per-step perf-timing
  helpers (`LogStep`/`_LogStep` in `DmdWindow`/`SettingsWindow`) → **Trace**. **PluginLoader:** no-folder
  + loaded-provider → **Info**; incompatible-version reject → **Warning** (the ignore/dup/missing-tool
  sites were already Warning). **Status:** resolved the deferred follow-up with a **classifier at the
  single `StatusText` setter** — failure wording → Warning, else Info — so the many scattered
  `StatusText = …` writers didn't each need touching.
- **2025 — First clutter-reduction pass (host categories).** Retagged the clear-cut high-volume host
  categories using a production `[GENERIC]` scan as the backlog. **Trace:** Volume (per-slider-tick),
  Chapters (per-item tick/restore/position), MediaEnded per-dispatcher step, RebuildCategories (perf
  timings), PlaylistPrefetch, ThumbnailCache Hit/Stored/Pruned/Skipped, WinVolume level reads, DMD blob
  transition, ResultCache per-page hit/miss/store/expire. **Debug:** Play started, Ditti diagnostics,
  SourceRegistry routing, YtDlpUpdater skips, DMD SetAppContext, Chapters count. **Info:** App startup
  lifecycle, SourceRegistry "Built N", ThumbnailCache/ResultCache purge/invalidate, MediaEnded live-drop,
  DirectInput enumeration, YtDlpUpdater update result. **Warning:** LibVLC pre-init fail, WinVolume query
  fail, Ditti failures, YtDlpUpdater failure, PluginLoader shadow/duplicate/missing-tool, SourceRegistry
  unknown provider, ResultCache read/store/purge errors. **Deferred:** `Status` (per-message split) and
  the Volume debounce — see Follow-up Work Items.
- **2025 — `[GENERIC]` scan script.** Added a "what's NOT yet migrated" PowerShell script (and
  variants) that filters `[GENERIC]` lines from recent logs, groups by the following `[Category]`, and
  sorts by volume — the retag backlog, generated from real logs. Plus a source-side scan to catch
  call sites that didn't fire in a session.
- **2025 — Level tags in log output + `[GENERIC]` marker.** Every log line now carries its tag after
  the timestamp (`[Info] [Category] msg`). Legacy untagged calls emit `[GENERIC]` (not `[Debug]`) so
  migration progress is greppable — `[GENERIC]` = not yet migrated; goal is to drive it to zero.
  Centralized formatting in `DebugLog` (single `Format`/`Enqueue` path); applied the same tagging to
  the Plex and YouTube plug-in shims for consistency.
- **2025 — `IPluginHost` leveled logging (plumbing) + SiriusXM.** Added `LogLevel` to the plug-in
  contract (`Phosphor.Plugin.Abstractions/LogLevel.cs`) and a `Log(LogLevel, string)` overload on
  `IPluginHost` (default interface method forwards to `Log(string)` so existing hosts/call sites are
  unchanged). `PluginHost` maps the contract level onto the host `DebugLog` level, so **Path-A plug-in
  logs now participate in verbosity filtering** and still land in the host log tagged `[Plugin:{id}]`.
  Demonstrated the pattern by retagging SiriusXM: added a leveled `Log` helper overload and moved its
  9 failure/warning sites (favorites/hidden/lineup read-write failures, channel-not-found, resolve
  failure, auth failure) to **Warning**.
- **2025 — Plex retag + volume-data correction.** Retagged `Phosphor.Plugins.Plex/PlexService.cs`:
  the per-track `DiagLog` helper (audio-stream selection dump, chapter probes, compilation-album
  search — all 15 sites funnel through it) now logs at **Trace**; the 3 genuine failure sites →
  **Warning**; the connect milestone (`Configured:`) → **Info**. Added level-aware overloads +
  `MinimumLevel` to the plug-in's own logger shim (`PlexDebugLog.cs`) — the Plex plug-in has its own
  `DebugLog` (forwards to `Trace.WriteLine`), separate from the host's, because it loads across the
  plug-in boundary. **Discovery:** the huge Plex share in the volume sample (~75%) was **historical**
  — it came from a previous dev cycle when Plex was still in-box using the *host* logger. Post-plugin-
  extraction, current Plex logs go to `Trace.WriteLine`, not the host log file, so that share is
  misleading. Volume table caveated accordingly (see below).
- **2025 — Foundation + `CachedImage`.** Added `LogLevel` enum, `MinimumLevel` gate, level-aware
  overloads (back-compat preserved), `AppSettings.DebugLogLevel`, and the live-applied/save-on-set
  Verbosity dropdown in Settings. Retagged the `CachedImage` thumbnail diagnostics: per-frame lines
  (`OnChanged`, `Sync/Async disk hit`, `Fallback to UI UriSource`, `Skipped stale assign`) → `Trace`;
  problems (`Disk decode FAILED`, `UriSource load failed`, `LoadAsync error`) → `Warning`. First
  proof of the pattern. Commit `4f783f2`.

---

## Candidates (Backlog)

### Migration progress (unmigrated caller count)

The concrete "how much is left" metric is the number of `DebugLog.Log(...)` call sites still using the
level-less (`[GENERIC]`) overloads. Count it with:

```powershell
# Unmigrated host+plugin callers: DebugLog.Log(...) WITHOUT a leading LogLevel arg.
# Excludes the logger's own method definitions and bin/obj.
$hits = Get-ChildItem -Path . -Recurse -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    Select-String -Pattern 'DebugLog\.Log\(' |
    Where-Object {
        $_.Line -notmatch 'DebugLog\.Log\(\s*LogLevel\.' -and
        $_.Line -notmatch 'void\s+Log\(' -and $_.Line -notmatch 'public static void Log'
    }
"UNMIGRATED CALLERS: $($hits.Count)"
$hits | Group-Object Filename | Sort-Object Count -Descending | Select-Object Count, Name
```

Snapshots (newest first):
- **2** after the YouTube Path-B shim — both remaining are intentionally level-less (the `PluginHost`
  routing method and the `JukeboxViewModel` Status classifier, each forwarding a computed level).
  **`[GENERIC]` retag effectively complete.**
- **19** after the misc host sweep — all remaining are the Path-B YouTube shim (`YtDlpVideoEngine` 8,
  `YtDlpUpdater` 4, `StreamSelector` 3, `SearchEngineFactory`/`VideoEngineFactory` 1 each) plus the
  `JukeboxViewModel` Status-classifier line and the `PluginHost` routing method (both intentionally
  level-less). **Host backlog cleared.**
- **~44** after Visualization (Mandelbrot 4, Matrix 3, ProjectM 2, GoL 1, MandelbrotGPU 1, Emoji 3).
- **~59** after PresetBrowser (6 → 0) + SettingsWindow (6 → 0) + PrefetchCache (6 → 0).
- **~77** after GaplessAudioPlayer (12 → 0) + DmdWindow (9 → 0).
- **~98** after BackglassWindow (BackglassWindow.xaml.cs 16 → 0).
- **113** after VideoCache + window monitor notifications (VideoCache 16 → 0, JukeboxWindow 2 → 0).
- **~131** after the DOF pass (DofClient.cs 27 → 0).
- **158** after ApplySettings/Status/PluginLoader (baseline for the DOF pass).
- ~230+ at the start (pre-migration, approx from the initial call-site inventory).

Caveats on the count: a chunk is **plugin-side (Path B)** — `YtDlpVideoEngine.cs`, `YtDlpUpdater.cs`
log through the YouTube plug-in's own `Trace.WriteLine` shim (separate retag track). And `PluginHost.cs`
(2) are the *routing* methods themselves, which forward whatever level a plug-in passes — they don't
need call-site levels. So the effective host backlog is somewhat below the raw number.

Prioritized by **runtime log volume** — the share of actual log lines a category emits — sampled from
the 10 most recent debug logs (~230K lines total). This is far more actionable than call-site count:
some categories have only a couple of `DebugLog.Log(...)` sites but sit in a hot loop and dominate the
file. Retagging the top few reclaims most of the log's readability.

> ⚠️ **The sample is skewed by stale/historical logging.** Verify a category's logging still exists in
> the *current* code (and still reaches the host log file) before prioritizing around it. The **Plex**
> ~75% share was entirely historical — from when Plex was in-box using the host logger. The extracted
> plug-in now logs via its own `Trace.WriteLine` shim, so those lines don't even reach the host log
> anymore. Treat the numbers below as a starting hypothesis, not ground truth.


> **Re-sample periodically.** As categories get retagged (and thus quieted at default level), re-run
> the tally to re-prioritize. See "How to re-sample" below. A **fresh sample is due** — the current one
> is dominated by the historical Plex data.

### Top offenders by runtime volume (from sample — NEEDS RE-SAMPLE)

| Category | Share | Notes / suggested direction |
|----------|------:|-----------------------------|
| ~~**Plex**~~ | ~~~75%~~ | ✅ Done, but the share was **historical/misleading** (see warning above). Retagged anyway for consistency. |
| **ApplySettings** | ~4.4% | Fires on every settings apply/broadcast → mostly Trace/Debug; "applied" summary → Info. |
| **RebuildCategories** | ~2.8% | Category rebuild churn → Trace; completion → Debug/Info. |
| **PERF.BackglassStall** | ~2.5% | Stall markers are diagnostic signal → keep as **Warning** (don't hide), but confirm they're not firing spuriously. |
| **Status** | ~1.8% | User-facing status echoes → Debug (or Trace if purely mirroring UI). |
| **DOF-Bridge / DOF** | ~1.4% + ~1.2% | Bridge chatter → Trace; connect/reconnect/shutdown → Info; reconnect attempts → Warning; gave-up → Error. Do as one cohesive pass. |
| **App** | ~1.4% | Startup/version/settings → Info; routine steps → Debug. |
| **ThumbnailCache** | ~1.3% | Hit/Stored/Pruned per item → Trace; Purge → Info. |
| **Volume / WinVolume** | ~1.3% + ~0.3% | Per-change volume spam → Trace. |
| **PluginLoader** | ~0.9% | Load steps → Debug; loaded/failed → Info/Warning. |
| **SourceRegistry** | ~0.8% | Routing decisions → Debug; source registered → Info. |
| **Matrix** | ~0.7% | Per-frame visualization → Trace. |
| **Plugin:emby / :siriusxm / :jellyfin / :iheartradio** | ~0.7%↓ | Per-plugin runtime; per-item → Trace, milestones → Debug/Info, failures → Warning. |
| **EXCEPTION** | ~0.4% | Already routed to Error via `LogException`; no action. |
| **Ditti / DMD / DirectInput / Chapters** | ~0.3% each | Mostly Debug; failures Warning. |
| **YtDlpUpdater** | ~0.2% | Update milestones → Info; per-probe detail → Debug; failures → Warning/Error. |
| **GaplessPCM / VideoCache / ResultCache / CachedImage** | <0.2% each | Already low at runtime; ResultCache invalidate/schema → Info; CachedImage done. |

### Suggested pass order (biggest readability win first)
1. [x] **Plex** — done (share was historical; retagged for consistency).
2. [ ] **Re-sample the logs** — get a realistic baseline now that the historical Plex data is understood.
       Do this before picking the next target.
3. [ ] **ApplySettings** + **RebuildCategories** — settings/category churn (verify still current).
4. [ ] **Plugin:* runtime logs** — per-source sweep. (Note: extracted plug-ins each have their own
       `DebugLog` shim → `Trace.WriteLine`, like Plex; retag them there, not in the host.) ✅ Plex + YouTube done
5. [ ] **Visualization** (Matrix, Mandelbrot, ProjectM, PERF.*) — per-frame → Trace, keep PERF stalls Warning. ✅ done
6. [ ] **Misc high-count files** — BackglassWindow (16), GaplessAudioPlayer (12), PrefetchCache (6),
       PresetBrowser (6), FavoritesIndex (4), etc. Fold into a "misc sweep." ✅ done — host backlog cleared.

### Done
- [x] **YouTube Path-B shim** (`Engines/DebugLog.cs` made level-aware + 17 call sites) —
      YtDlpVideoEngine/YtDlpUpdater failures → Warning, update milestone → Info, yt-dlp echoes +
      StreamSelector dumps → Trace, factory fallbacks → Warning. Mirrors the Plex shim; no contract
      change. **`[GENERIC]` retag effectively complete** (only 2 intentionally level-less sites left).
- [x] **Misc host sweep** —
      SecretProtector, PinupDatabase, PinupPlaylistLoader, Playfield/Topper/Backglass ambient,
      JukeboxViewModel Network/PreemptiveCache, PluginHost.ReportStatus. Persistence/IO failures →
      Warning; blob transitions → Trace; milestones → Debug/Info. **Host backlog cleared.**
- [x] **Visualization** (Mandelbrot/Matrix/ProjectM/GoL/MandelbrotGPU/Emoji) —
      → Trace; GPU/preset/target helpers → Debug; discovery + Emoji render failures → Warning.
- [x] **PresetBrowser + SettingsWindow + PrefetchCache** (6 sites each) —
      Debug/Trace; SettingsWindow failures → Warning; PrefetchCache per-item → Trace, mux/ffmpeg
      failures → Warning.
- [x] **GaplessAudioPlayer** (12 sites) + **DmdWindow** (9 sites) —
      Debug, per-buffer/callback internals → Trace; DmdWindow ScrubBar → Trace, Settings/DOF/Emoji
      failures → Warning.
- [x] **BackglassWindow** (`BackglassWindow.xaml.cs`, 16 sites) —
      timing → Trace; gapless prime/swap + cache-switch → Debug; tracks-drained → Info; seek-failed
      restarts + PERF.BackglassStall → Warning.
- [x] **VideoCache** (16 sites) + **window monitor notifications** (`JukeboxWindow`, 2 sites) —
      per-item → Trace, milestones → Info, failures → Warning; window Hz logs given a proper
      `[WindowName]` category via `GetType().Name`.
- [x] **DOF** (`DofClient.cs`, 27 sites) — per-trigger/passthrough → Trace; milestones → Info;
      failures → Warning; reconnect-gave-up → Error. Largest single-file cluster cleared.
- [x] **ApplySettings / Status / PluginLoader** — perf-timing → Trace; loader milestones → Info,
      incompatible → Warning; Status classified at the setter (Warning on failure wording, else Info).
- [x] **Host clutter pass** — Volume, Chapters, MediaEnded, RebuildCategories, PlaylistPrefetch,
      ThumbnailCache, WinVolume, App, Ditti, YtDlpUpdater, SourceRegistry, PluginLoader, DMD,
      DirectInput, ResultCache, Play retagged (see Work Log). `Status` deferred.
- [x] **Plumbing: `IPluginHost` leveled logging** — `Log(LogLevel, string)` added to the contract;
      Path-A plug-in logs can now be leveled at their call sites.
- [x] **SiriusXM** — 9 failure sites → Warning via the new leveled `_host.Log`. First Path-A retag.
- [x] **Plex** — `PlexService.cs` `DiagLog` → Trace; failures → Warning; connect → Info. Plug-in shim
      (`PlexDebugLog.cs`) made level-aware. Share was historical (pre-extraction), not current load.
- [x] **CachedImage** — see Work Log (2025 foundation pass). Confirmed low runtime volume (~0.1%).

---

## How to re-sample runtime volume

Run this in PowerShell (adjust the logs path if needed) to re-tally category share across the 10 most
recent logs and re-prioritize the backlog:

```powershell
$logs = Get-ChildItem -Path .\Phosphor\bin\Debug\net8.0-windows\logs -Filter *.log |
	Sort-Object LastWriteTime -Descending | Select-Object -First 10
$total = 0; $counts = @{}
foreach ($f in $logs) {
	foreach ($line in Get-Content $f.FullName) {
		$total++
		if ($line -match '^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s+\[([^\]]+)\]') {
			$c = $Matches[1]; $counts[$c] = ($counts[$c] + 1)
		}
	}
}
"TOTAL LINES: $total"
$counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 30 |
	ForEach-Object { "{0,7:N0}  {1,6:P1}  {2}" -f $_.Value, ($_.Value/$total), $_.Name }
```

Note: the sample reflects whatever verbosity/features were exercised in those sessions (e.g. heavy Plex
use inflates Plex's share). Take shares as directional, not absolute.

---

## How to find what's NOT yet migrated (`[GENERIC]` scan)

Every unmigrated call logs as `[GENERIC] [Category] …`. This scans the most recent logs for those,
groups by the **category that follows `[GENERIC]`**, and sorts so the highest-volume unmigrated
sources surface first — i.e. the highest-value retag targets. Drive this list toward empty.

```powershell
# The category token right after [GENERIC], e.g. "[GENERIC] [RebuildCategories] ..." -> RebuildCategories
$logs = Get-ChildItem -Path .\Phosphor\bin\Debug\net8.0-windows\logs -Filter *.log |
	Sort-Object LastWriteTime -Descending | Select-Object -First 10
$counts = @{}; $total = 0
foreach ($f in $logs) {
	foreach ($line in Get-Content $f.FullName) {
		# [ts] [GENERIC] [Category] message   (Category optional)
		if ($line -match '^\[[\d:.]+\]\s+\[GENERIC\]\s+(?:\[([^\]]+)\]\s*)?(.*)$') {
			$total++
			$cat = if ($Matches[1]) { $Matches[1] } else { '(no category)' }
			$counts[$cat] = ($counts[$cat] + 1)
		}
	}
}
"UNMIGRATED [GENERIC] LINES: $total across $($counts.Count) categories"
$counts.GetEnumerator() | Sort-Object Value -Descending |
	ForEach-Object { "{0,7:N0}  {1}" -f $_.Value, $_.Name }
```

Variants:
- **Just the distinct categories, alphabetical** (a checklist of what's left):
  ```powershell
  Select-String -Path .\Phosphor\bin\Debug\net8.0-windows\logs\*.log -Pattern '\[GENERIC\]\s+\[([^\]]+)\]' -AllMatches |
	  ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } |
	  Sort-Object -Unique
  ```
- **Raw unmigrated lines from the newest log** (to read actual messages before retagging):
  ```powershell
  $newest = Get-ChildItem .\Phosphor\bin\Debug\net8.0-windows\logs\*.log |
	  Sort-Object LastWriteTime -Descending | Select-Object -First 1
  Select-String -Path $newest.FullName -Pattern '\[GENERIC\]' | ForEach-Object { $_.Line }
  ```

> Complement with a **source-side** scan (catches call sites that didn't happen to fire in a session):
> `Select-String -Path .\**\*.cs -Pattern 'DebugLog\.Log\("' ` finds `Log(category, message)` calls
> with no level; `DebugLog\.Log\("[^"]*"\)` finds bare `Log(message)` calls. Exclude `\obj\` / `\bin\`.

---

## Follow-up Work Items

Non-blocking items surfaced during retagging — not pure level changes, so parked here for a deliberate
pass rather than done inline.

- [x] **`Status` category — split by nature (per-message levels).** ✅ Resolved via a **classifier at
      the single `StatusText` setter choke point** (`JukeboxViewModel`): failure-ish wording
      (`fail`, `error`, `unreachable`, `timed out`, `unavailable`, `can't`, `not found`, …) → **Warning**;
      everything else → **Info**. Avoided reclassifying the many scattered `StatusText = …` writers.
      Conservative by design — false negatives just log at Info. Example split:
      - `[Info] [Status] Playing: David Bowie - Modern Love …`
      - `[Warning] [Status] Playback failed: server unreachable or stream timed out`
- [ ] **Volume-slider log flood → debounce.** `BackglassWindow.xaml.cs:410` (`VolumeChanged` handler)
      logs on every slider tick; dragging floods the log. Retagged to **Trace** (so it's silent at
      default), but the underlying **lack of debounce** on `VolumeChanged` is worth addressing on its own
      (also saves redundant `SetVolume`/VLC calls). Debounce the volume apply + log, or log only on
      drag-end.
- [ ] **Path-B plug-in logs don't reach the host log file.** The Plex and YouTube shims forward to
      `Trace.WriteLine` (debugger / trace listeners only), so their now-leveled logs never land in
      `Phosphor_Debug_*.log` in a normal run, and their `MinimumLevel` isn't synced to the host
      verbosity setting. Optional future work: route these through `IPluginHost.Log(LogLevel, string)`
      (contract already supports it) so Path-B participates in host-file logging + verbosity filtering.
      Requires plumbing a host reference into the currently-static engines/factories — a real refactor,
      not a retag, hence parked here.

---

## Notes & Discoveries

Use this section to record anything found during retagging that's worth remembering (miscategorized
logs, dead log lines, categories that should be renamed/merged, hot paths that shouldn't log at all,
etc.). These aren't part of the level migration but are cheap to capture while we're in the code.

- **Two plug-in logging paths — know which one you're touching.** There is **no per-plug-in log file**;
  everything ultimately targets the host's single `Phosphor_Debug_yyyyMMdd.log` (in
  `Phosphor/bin/<config>/net8.0-windows/logs/`) — *if* it reaches the host at all.
  - **Path A — via `IPluginHost.Log` (reaches the host file).** Most plug-ins (Emby, Jellyfin,
    SiriusXM, iHeartRadio, …) log through `_host.Log(...)`. `PluginHost.Log` routes to the host
    `DebugLog` as `DebugLog.Log($"Plugin:{instanceId}", msg)` — hence the `[Plugin:emby]` /
    `[Plugin:siriusxm]` tags in the log file. **`IPluginHost` now exposes `Log(LogLevel, string)`**
    (contract enum `Phosphor.Plugin.Abstractions.LogLevel`; `PluginHost` maps it onto the host
    logger), so these retags happen **at the call site inside the plug-in** by passing a level — no
    plug-in shim needed. Untagged `Log(string)` calls still default to Debug-equivalent.
  - **Path B — via an internal `DebugLog` shim → `Trace.WriteLine` (does NOT reach any file).** Only
    **two** projects do this: `Phosphor.Plugins.Plex/PlexDebugLog.cs` and
    `Phosphor.Plugins.YouTube/Engines/DebugLog.cs`. These exist because relocated in-box code kept
    calling a bare `DebugLog.Log(...)`. Their output goes to the **VS Output window / trace listeners**
    only — it vanishes in a normal run. Retag level support in *that shim*. (Correction to an earlier
    note: it is NOT true that every extracted plug-in has such a shim — most use Path A.)
- **Consequence for Plex specifically:** the Plex `DiagLog` lines we retagged go to `Trace.WriteLine`,
  so they never hit the host log file in a normal run anyway. The retag is still correct for consistency
  and for debugger-output readability, but don't expect Plex lines in `Phosphor_Debug_*.log` from the
  current build.
- **Volume samples can be dominated by historical logging.** The Plex ~75% share came from a prior dev
  cycle when Plex was in-box on the host logger; it's meaningless for current prioritization. Always
  confirm a category's logging still exists in current source (and reaches the host log) before
  chasing its share.
