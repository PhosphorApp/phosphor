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

### Phase 0 inventory results (done)

Reference counts to moving symbols outside `Search/`, `Video/`, `Plugins/YouTube/`:
`JukeboxViewModel` 36, `PluginSettingsFactory` 16, `App.xaml.cs` 14, `StreamSelector` 12,
`AppSettings` 9, `SettingsWindow` 7, `VideoCache`/`PrefetchCache` 5 each, `BackglassWindow` 4,
`SourceRegistry` 2, `DmdWindow` 1.

**Key structural finding — `Phosphor.Video` must be SPLIT, not moved wholesale:**
- **Stays in the host** (host-facing playback vocabulary, consumed by windows/VM/caches/settings):
  `VideoQualityPreference`, `VideoStreams`, `VideoStreamKind`, `VideoDownload`
  (all defined in `Video/IVideoEngine.cs`).
- **Moves to the plugin** (engine implementation): `IVideoEngine`, `VideoEngineFactory`,
  `YoutubeExplodeVideoEngine`, `YtDlpVideoEngine`, `YtDlpUpdater`, `Services/StreamSelector.cs`,
  and the `YoutubeExplode` package. Same split applies to `Phosphor.Search` (`ISearchEngine`,
  factories, engines move; any host-facing result types stay).

**Concrete couplings to sever:**
- `BackglassWindow.xaml.cs` (~821) falls back to `new YoutubeExplodeVideoEngine()` — must always
  route via `vm.ResolveStreamsViaPluginOrLegacy` (no direct engine construction in the UI layer).
- The VM already bridges the contract to host vocab (`ResolveStreamsViaPluginOrLegacy` →
  `MapResolvedStream` → `VideoStreams`; `DownloadStreamsViaPluginOrLegacy` → `VideoDownload`;
  `WireCacheDownloadOverride`). This bridge is the template — legacy `_videoEngine` fallbacks in
  these methods get removed once the engine lives only in the plugin.
- `App.xaml.cs` `MaybeAutoUpdateYtDlp` and `SettingsWindow` yt-dlp update UI reference
  `YtDlpUpdater`/engine kinds — must move behind the `IUpdatable` capability (host stays engine-agnostic).
- `PluginSettingsFactory` / `SourceRegistry` construct `YouTubeSourceProvider` directly — convert
  to discovery.


## Phase 1 — Fill contract gaps (unblock both sources)

- [x] **Chapter parsing — NO new contract capability needed (verified in Phase 2 step).**
	  The contract already carries everything: `SourceMetadata(Duration, Description, Chapters,
	  PublishedAt)` is returned by `IPlayableResolver.GetMetadataAsync` and includes both native
	  `Chapters` AND the raw `Description`; `ChapterMarker` and `SourceItem.Chapters` already exist.
	  Resolution: move the YouTube-specific description→chapters parsing (`ParseYouTubeChapters`)
	  INTO the YouTube plugin's `GetMetadataAsync` so it returns pre-parsed `SourceMetadata.Chapters`.
	  The host then drops `ParseYouTubeChapters` entirely (a Phase-4 rewire, not a contract change).
- [x] **Gapless — already covered.** `IGaplessCapable` already exists in the contract; no stub needed.
- [ ] Verify `IPluginHost` already exposes everything YouTube needs (`GetToolPath("yt-dlp")`,
	  `GetToolPath("ffmpeg")`, `HttpClient`, `InstanceCacheDirectory`, secrets). Add members
	  only if a concrete gap appears — keep the door one-way.
- [x] Add an optional `RequiredTools` declaration to the provider contract
	  (`IReadOnlyList<string> RequiredTools => []` on `IPhosphorSourceProvider`) and have
	  `DiscoveredProviders.Initialize` warn when a declared tool is missing from the host tool
	  folder (mirrors `PluginHost.GetToolPath` resolution). Validation + visibility only — no
	  acquisition/provisioning.

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

- [x] `run_build` clean across the solution (host + all plug-ins).
- [x] Programmatic deployment verification (done):
	  - Plug-in self-deploys to `Phosphor\bin\Debug\net8.0-windows\plugins\YouTube` with
		`YoutubeExplode.dll` + transitive deps (`AngleSharp.dll`, `JsonExtensions.dll`) as private
		ALC dependencies; `Phosphor.Plugin.Abstractions.dll` correctly excluded (host owns it).
	  - Host bin no longer contains `YoutubeExplode.dll` (fully removed from the host).
	  - `yt-dlp.exe` and `ffmpeg.exe` present in the host dir, so `GetToolPath` + `RequiredTools`
		validation resolve.
- [x] **Manual smoke test (owner) — PASS.** App launches, YouTube appears in the providers list,
	  search + playback work. Fix required along the way: the plug-in loader constructs providers via
	  `Activator.CreateInstance`, which needs a REAL parameterless constructor — `YouTubeSourceProvider`
	  had `(HttpClient? http = null)` (optional-arg ctors don't qualify), causing
	  `MissingMethodException`. Made the provider parameterless (commit `dc42394`). Note: a running
	  app holds the previously-loaded plug-in DLL, so after a plug-in rebuild you must fully close
	  `Phosphor.exe` before relaunch or the stale DLL is used.
- [ ] Manual (optional follow-ups): caching + prefetch end-to-end; `RequiredTools` warning when
	  `yt-dlp.exe` is absent; engine switch (YoutubeExplode <-> yt-dlp) and yt-dlp updater.
- [x] **GATE outcome: GREEN — confirmed at runtime.** No step forced reaching into host internals;
	  YoutubeExplode rides cleanly in the plug-in ALC; chapters needed NO new contract capability
	  (relocated the parser into the plug-in). Full cutover verified working on branch
	  `youtubepluginmigration`. **Phase 6 (Plex) is unblocked.**

### Phase 2/3/4 cutover notes (done)
- `Phosphor.Video`/`Phosphor.Search` engines + `StreamSelector` + `YoutubeExplode` moved into
  `Phosphor.Plugins.YouTube\Engines\`; plug-in-internal support types added (`VideoItem`,
  `ChapterMarker`, engine enums, a `DebugLog` trace shim) since the plug-in can't see the host.
- Host retained playback vocabulary in new `Phosphor\Video\VideoVocabulary.cs`
  (`VideoStreams`/`VideoStreamKind`/`VideoDownload`/`VideoMetadata`).
- Host decoupled from the plug-in via `Phosphor\Plugins\KnownSourceTypeIds.cs`
  (`KnownSourceTypeIds.YouTube` + `YouTubeSettingKeys`); `SourceRegistry`/`PluginSettingsFactory`
  route YouTube through `DiscoveredProviders`; `youtube` type id no longer reserved.
- Caches made engine-agnostic (drive solely via `DownloadOverride`/`IDownloadable`).
- VM legacy fallbacks deleted; `SetVideoEngine`/`SetSearchEngine`/`RebuildSearchEngine` are no-ops
  (engine choice is now a plug-in setting). `BackglassWindow` engine fallback removed; yt-dlp
  updater routes through `IUpdatable` in VM/App/SettingsWindow; `YoutubeExplode.PlaylistId.Parse`
  replaced with a host-side regex heuristic.
- Commits: `59a0993` (scaffold), `124aed6` (engine extraction + host agnostic), `f2cd3a0`
  (chapter parsing relocation).

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
