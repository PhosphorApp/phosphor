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
- The level gate short-circuits **before** string formatting, so tagging hot paths `Trace` also
  removes their formatting cost at default verbosity — a minor perf win, not just noise reduction.
- Verify each pass with a build. No behavioral changes expected beyond log routing.

---

## Work Log

Newest first. Note the date, what was retagged, and anything discovered.

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
4. [ ] **DOF** family (DOF-Bridge + DOF) — cohesive connect/reconnect/shutdown pass.
5. [ ] **Volume / WinVolume**, **Status**, **App** — misc high/medium volume.
6. [ ] **Plugin:* runtime logs** — per-source sweep. (Note: extracted plug-ins each have their own
       `DebugLog` shim → `Trace.WriteLine`, like Plex; retag them there, not in the host.)
7. [ ] **Visualization** (Matrix, Mandelbrot, ProjectM, PERF.*) — per-frame → Trace, keep PERF stalls Warning.
8. [ ] Remaining low-volume categories — fold into a final "misc sweep."

### Done
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
