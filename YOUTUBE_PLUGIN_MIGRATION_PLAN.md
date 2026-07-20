# YouTube (and Plex) Built-in Plugin Extraction — Migration Plan

> Status: **Planning / not started.** This is a reference doc to work from when we're ready.
> Scope gate: **YouTube first as a feasibility spike.** Plex only proceeds if the YouTube
> spike lands without poking host internals. If YouTube is a no-go, Plex is a no-go.

## Goal

Move the two remaining in-box sources (YouTube, then Plex) out of the `Phosphor` host
project into standalone, dynamically-loaded plug-in projects that reference only
`Phosphor.Plugin.Abstractions` — matching the existing external plug-ins
(Vimeo, Jellyfin, Emby, SiriusXM, Dailymotion).

## Why this is feasible

The plug-in infrastructure already exists and is proven in production:

- Contract project `Phosphor.Plugin.Abstractions` with capability interfaces
  (`ITextSearchCapable`, `IPlayableResolver`, `IDownloadable`, `IUpdatable`,
  `IConnectionTestable`, `IFavoritable`, etc.) and the `IPluginHost` one-way door.
- Dynamic loader `PluginLoader` with isolated, non-collectible `AssemblyLoadContext`
  per plug-in, plus lazy private-dependency loading.
- Discovery registry `DiscoveredProviders` and self-deploy MSBuild `AfterTargets="Build"`
  targets (see `Phosphor.Plugins.Vimeo.csproj`).
- **Vimeo already shells out to the bundled `yt-dlp` via `IPluginHost.GetToolPath`** —
  the exact pattern YouTube's yt-dlp path needs.

## Known coupling / risk points (the actual work)

### YouTube
1. `YouTubeSource` composes host-internal engine trees `Phosphor.Search.*`
   (`SearchEngineFactory`, `YoutubeExplodeSearchEngine`, `YtDlpSearchEngine`,
   `ISearchEngine`) and `Phosphor.Video.*` (`VideoEngineFactory`,
   `YoutubeExplodeVideoEngine`, `YtDlpVideoEngine`, `IVideoEngine`, `YtDlpUpdater`).
2. The **YoutubeExplode NuGet dependency** currently belongs to the host. In a plug-in it
   must ride along as a private ALC dependency (loader supports this).
3. `JukeboxViewModel.ParseYouTubeChapters` parses chapters from YouTube descriptions
   **in the VM** — a non-contract, YouTube-specific seam.
4. `SourceRegistry` hard-references `YouTubeSourceProvider`/`YouTubeSource` and reserves
   the YouTube type id as a built-in.
5. `YouTubeSource` doc-comments explicitly rely on being "in-box, statically referenced"
   and using "the host's YoutubeExplode package and existing engine code directly."

### Plex (later)
1. `PlexService` is instantiated directly in the VM (`new PlexService()` per instance +
   legacy `_plex` fallback), keyed via `_plexServiceByInstance`, `ActivePlex`,
   `ActivePlexSource`.
2. Used across browse, in-library search, `GetAllTracks`, chapters, and gapless playback —
   more host-side call sites to sever than YouTube.
3. Types to relocate: `Plex/PlexService.cs`, `Plugins/Plex/PlexNode.cs`,
   `Plugins/Plex/PlexMappings.cs`, `Models/PlexEnums.cs`, `Plugins/Plex/PlexSource.cs`,
   `Plugins/Plex/PlexSourceProvider.cs`.

---

## Design decisions (settled during planning)

### Caching & prefetch — host keeps the mechanics; plugins supply raw downloads
- **Do NOT move the cache into the plugins.** The cache *mechanics* (ffmpeg mux, `index.json`,
  LRU eviction, disk-size budget) are generic and complex; duplicating them per plugin is wrong.
  The contract already expresses the intended split: `IDownloadable.DownloadAsync(item, prefs,
  destinationDir, …)` — "the source writes raw files into `destinationDir`; **the host muxes /
  indexes / evicts**."
- **Plugin owns**: *how* to fetch raw bytes (YoutubeExplode-vs-yt-dlp choice, headers, stream
  selection). **Host owns**: cache mux/index/evict/size-cap and prefetch scheduling.
- **Mux decision is not a runtime concern for cached items**: `VideoCache` always muxes the
  separate video+audio streams into a single seekable `.mkv` at *cache time* and deletes the
  intermediates. `TryGet` returns that one file — nothing to mux at play time. Mux-vs-no-mux only
  exists on the non-cached/live path.
- **Prefetch stays host-scheduled** — do NOT add a "fetch next item" contract method. "Next item"
  is host/playback state (`_queueIndex + 1`, repeat wrap-around, `Queue`); plugins are pure data
  producers with no concept of playback order. The host decides *what/when* to prefetch and calls
  the plugin's `IDownloadable` for the *how*.
- **Sever the engine defaults**: `VideoCache`/`PrefetchCache` currently default
  `VideoEngine = new YoutubeExplodeVideoEngine()` and reference `IVideoEngine`. Make them
  engine-agnostic and drive them exclusively through the `DownloadOverride` / `IDownloadable`
  path so the host holds no YouTube engine type.
- **Cache settings are hybrid**: global disk budget + prefetch on/off stay host-level (shared
  host disk resource); per-source knobs (opt-in caching, quality, max clip length) live in each
  plugin's settings schema. The existing per-instance `AllowCaching` policy is the precedent.

### Shared tools & dependency declaration (yt-dlp, ffmpeg)
- **Managed libs vs native tools differ**:
  - `YoutubeExplode.dll` (managed NuGet, YouTube-specific) → lives **only** in the YouTube plugin
    as a private ALC dependency.
  - `yt-dlp.exe` / `ffmpeg.exe` (native bundled tools, potentially shared by multiple plugins) →
    stay **host-owned** in the shared `dependencies\` folder.
- **`dependencies\` stays host-owned; keep/formalize it.** It already exists and works.
- **`IPluginHost.GetToolPath(name)` is the one lookup seam.** Plugins never carry their own copy
  of a native tool; they ask the host (Vimeo already does this for yt-dlp). One binary, many
  consumers — this directly answers the "multiple plugins using yt-dlp gets messy" concern.
- **yt-dlp C# wrapper vs the exe**: the C# orchestration (`YtDlpVideoEngine`, `YtDlpSearchEngine`,
  `YtDlpUpdater`) moves into the YouTube plugin; the **exe stays host-bundled** and is reached via
  `GetToolPath`.
- **Updater ownership stays host/centralized**: because `yt-dlp.exe` is a shared binary, keep the
  "update yt-dlp / report version" surface host-side (or centralized), NOT a per-plugin action on
  a file the plugin doesn't own.
- **Lightweight dependency declaration (validation + visibility only — NOT provisioning)**: add an
  optional `RequiredTools => ["yt-dlp", …]` declaration on the provider so the loader can (a) warn
  / disable a plugin at load time when a declared tool is missing (clear startup diagnostic instead
  of a play-time failure), and (b) make the shared-consumer graph explicit (which feeds the updater
  ownership decision). Do NOT build a general dependency-resolution/acquisition system — with only
  two static native tools that is YAGNI.

---

## Phase 0 — Pre-flight (do before touching code)

- [ ] `git pull` to sync, then work on branch **`youtubepluginmigration`** (the easy eject
	  button — if the spike goes south, abandon the branch).
- [ ] Confirm a clean build and note current YouTube + Plex behavior as a manual baseline
	  (search, resolve/play, download/cache, chapter markers, connection test, favorites).
- [ ] Inventory every host reference to `YouTubeSource`, `YouTubeSourceProvider`,
	  `Phosphor.Search.*`, and `Phosphor.Video.*` (Find All References) so nothing is missed.

## Phase 1 — Fill contract gaps (unblock both sources)

- [ ] Decide the home for **chapter parsing**. Preferred: add an `IChapterProvider`
	  (or extend an existing capability) to `Phosphor.Plugin.Abstractions` so YouTube and
	  Plex can both supply chapters through the contract instead of `ParseYouTubeChapters`
	  living in the VM.
- [ ] Audit whether **gapless playback** and **chapters** for Plex need new capability
	  interfaces; stub them now so YouTube extraction proves the shape before Plex needs it.
- [ ] Verify `IPluginHost` already exposes everything YouTube needs (`GetToolPath("yt-dlp")`,
	  `GetToolPath("ffmpeg")`, `HttpClient`, `InstanceCacheDirectory`, secrets). Add members
	  only if a concrete gap appears — keep the door one-way.
- [ ] Add an optional `RequiredTools` declaration to the provider contract (e.g.
	  `IReadOnlyList<string> RequiredTools => []`) and have `PluginLoader`/`DiscoveredProviders`
	  warn/disable a plugin when a declared tool is missing from `dependencies\`. Validation +
	  visibility only — no acquisition/provisioning.

## Phase 2 — Create the YouTube plug-in project

- [ ] Add `Phosphor.Plugins.YouTube\Phosphor.Plugins.YouTube.csproj`, cloning the Vimeo
	  project shape: `net8.0`, `EnableDynamicLoading=true`, compile-only contract reference
	  (`Private=false` / `ExcludeAssets=runtime`), and the `SelfDeployToHost` target writing
	  to `Phosphor\bin\$(Configuration)\net8.0-windows\plugins\YouTube`.
- [ ] Add the `YoutubeExplode` NuGet package **to the plug-in project** (private/runtime
	  dependency that ships in the plug-in folder).

## Phase 3 — Relocate the engine trees

- [ ] Move `Phosphor\Search\*` (`ISearchEngine`, `SearchEngineFactory`,
	  `YoutubeExplodeSearchEngine`, `YtDlpSearchEngine`) into the plug-in project.
- [ ] Move `Phosphor\Video\*` (`IVideoEngine`, `VideoEngineFactory`,
	  `YoutubeExplodeVideoEngine`, `YtDlpVideoEngine`, `YtDlpUpdater`) into the plug-in.
- [ ] Move `Phosphor\Plugins\YouTube\*` (`YouTubeSource`, `YouTubeSourceProvider`,
	  `YouTubeMappings`) into the plug-in.
- [ ] Re-namespace as needed; ensure the yt-dlp path resolves the exe via
	  `IPluginHost.GetToolPath("yt-dlp")` instead of app-relative host paths.
- [ ] Confirm the "fall back to an available engine" safety net still works entirely inside
	  the plug-in (no cross-boundary calls into the host).

## Phase 3.5 — Make caching engine-agnostic

- [ ] Remove the `VideoEngine = new YoutubeExplodeVideoEngine()` default from `VideoCache` and
	  `PrefetchCache`; drop their `IVideoEngine` / `Phosphor.Video` references.
- [ ] Route both caches exclusively through the `DownloadOverride` / `IDownloadable` path so the
	  host holds no YouTube engine type (download *how* comes from the plugin; mux/index/evict stay
	  host-side).
- [ ] Keep prefetch host-scheduled (`PrefetchNextTrack` stays in the VM); confirm no "next item"
	  logic leaks into the contract.
- [ ] Split cache settings: keep global disk budget + prefetch on/off host-level; move per-source
	  knobs (opt-in caching, quality, max clip length) into the YouTube plugin's settings schema
	  (the `AllowCaching` per-instance policy is the precedent).
- [ ] Keep `yt-dlp.exe` / `ffmpeg.exe` in the host `dependencies\` folder; ensure the plugin
	  reaches them only via `GetToolPath`. Keep the yt-dlp updater/version surface host-side.

## Phase 4 — Re-wire the host

- [ ] Remove YouTube from the `SourceRegistry` static wiring and stop reserving its type id
	  in `DiscoveredProviders.Initialize` so it loads via discovery like Vimeo.
- [ ] Route `JukeboxViewModel.ParseYouTubeChapters` consumers through the new chapter
	  capability (Phase 1); delete the VM-resident YouTube parsing once covered.
- [ ] Remove the `YoutubeExplode` package reference from `Phosphor.csproj` (now owned by the
	  plug-in). Confirm nothing else in the host used it.
- [ ] Fix all references surfaced in the Phase 0 inventory.

## Phase 5 — Validate the YouTube spike (decision gate)

- [ ] `run_build` clean across the solution.
- [ ] Manual smoke test vs. the Phase 0 baseline: search, resolve/play, download/cache,
	  chapters, connection test, favorites, engine switch (YoutubeExplode <-> yt-dlp),
	  yt-dlp updater.
- [ ] Verify caching + prefetch still work with the host holding no YouTube engine type
	  (cache hit plays the muxed `.mkv`; prefetch primes the next queue item via `IDownloadable`).
- [ ] Verify `RequiredTools` validation: temporarily remove `yt-dlp.exe` and confirm a clear
	  load-time warning/disable instead of a play-time failure.
- [ ] Confirm the plug-in loads from `plugins\YouTube` with YoutubeExplode as a private dep
	  (check the loader log lines from `PluginLoader`).
- [ ] **GATE:** If any step forced reaching into host internals or the YoutubeExplode-in-ALC
	  / chapter capability work turned ugly — **STOP.** Per project rule, Plex stops too.
	  Otherwise proceed to Phase 6.

## Phase 6 — Plex extraction (only if Phase 5 passes)

- [ ] Create `Phosphor.Plugins.Plex\Phosphor.Plugins.Plex.csproj` (Vimeo project shape,
	  self-deploy to `plugins\Plex`).
- [ ] Move `Plex/PlexService.cs`, `Plugins/Plex/PlexNode.cs`, `Plugins/Plex/PlexMappings.cs`,
	  `Models/PlexEnums.cs`, `Plugins/Plex/PlexSource.cs`, `Plugins/Plex/PlexSourceProvider.cs`
	  into the plug-in.
- [ ] Replace VM-direct `new PlexService()` usage: route `_plexServiceByInstance`,
	  `ActivePlex`, `ActivePlexSource`, `ConfigurePlexFromSettings`, browse, in-library
	  search, `GetAllTracks`, chapters, and gapless through contract capabilities.
- [ ] Remove Plex from `SourceRegistry` static wiring and its reserved type id.
- [ ] Build + manual smoke test: multi-server browse, search, playback, chapters, gapless,
	  connection test.

## Phase 7 — Cleanup

- [ ] Remove now-dead host files/usings; update `AGENTS.md` / architecture docs to note
	  YouTube and Plex are now external plug-ins.
- [ ] Update `PluginLoader` / `DiscoveredProviders` doc-comments that still call YouTube and
	  Plex "statically referenced / built-in".
- [ ] `check in` (local commit) after each green phase; `check in and push` when the spike or
	  full migration is validated.

## Notes / conventions

- No settings migration or backward-compat shims — testers-only user base; clean breaking
  renames are fine.
- `PlayfieldWindow` / `BackglassWindow` run on their own threads; plug-ins remain pure data
  producers (no UI, no thread assumptions) as the contract requires.
- Keep IO minimal; do not add per-change disk writes.
