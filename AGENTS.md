# Phosphor — Architecture & Agent Guide

This file is authoritative context for AI agents working on this codebase. Read it before planning or editing.

---

## Repository Layout (three repos)

Phosphor is split across **three repositories** under the `PhosphorApp` GitHub org:

| Repo | Contents | License | Visibility |
|------|----------|---------|------------|
| `PhosphorApp/phosphor` | Host app (`Phosphor/`) + `DofBridge` / `DofBridge.x86` | PolyForm Noncommercial | public |
| `PhosphorApp/phosphor-abstractions` | The plug-in CONTRACT — published to NuGet as `Phosphor.Plugin.Abstractions` | MIT | public (+ NuGet package) |
| `PhosphorApp/phosphor-plugins` | All `Phosphor.Plugins.*` source plug-ins (one solution) | MIT | public |

The contract is MIT so third-party plug-in authors can build against it freely; the **app** is the
part whose commercial use is restricted (PolyForm Noncommercial). The abstractions package on NuGet
contains only contract types — nothing proprietary. Contributions are governed by `CONTRIBUTING.md`
and `CLA.md`.

**This repo (`phosphor`) — host solution structure:**

```
phosphor/
├── Phosphor/          # Main WPF app (.NET 8) — the plug-in HOST
│   ├── App.xaml.cs       # Startup, shared LibVLC init, window orchestration
│   ├── JukeboxViewModel.cs  # Central ViewModel; owns UI/app state, dispatches to plug-in sources
│   ├── default_settings.json / default_categories.json  # shipped fallbacks (never written to)
│   ├── Models/           # AppSettings, VideoItem, Category, GenreCategoryStore, KeyBindings,
│   │                     #   PinupSettings, ChapterMarker, observable collections
│   ├── Playback/         # JukeboxPlayer, PlayerQueue (per-player queue + persistence), PlayerContext,
│   │                     #   AutoDjService, MediaEngine, IPlaybackHost, ActiveQueueForwarder, VlcDiagnostics
│   ├── Caching/          # VideoCache, PrefetchCache, ThumbnailCache, PlaylistCache, ResultCache,
│   │                     #   CacheDownloadCoordinator (engine-agnostic)
│   ├── Input/            # DofClient (named-pipe DOF bridge), DirectInputPoller (SharpDX.DirectInput)
│   ├── Plugins/          # Plug-in host: SourceRegistry, DiscoveredProviders, PluginInstanceConfig,
│   │                     #   KnownSourceTypeIds, PluginSettingsFactory, Loader/PluginLoader,
│   │                     #   Host/PluginHost + SecretProtector
│   ├── Video/            # Host-side playback vocabulary (VideoVocabulary.cs) — NOT the engines
│   ├── Services/         # PlayHistory, PlaylistManager, SearchHistory, FavoritesIndex, DebugLog,
│   │                     #   AppVersion, Pinup* (Pinup Popper integration)
│   ├── Visuals/          # AudioReactiveService, blob patterns (Patterns/), Mandelbrot, projectM interop
│   └── Windows/          # JukeboxWindow base, Playfield/Backglass/Topper/Dmd windows (+ *Proxy,
│                         #   *.Ambient.cs, *.PlaybackHost.cs partials), SettingsWindow,
│                         #   PresetBrowserWindow, ManageHiddenWindow, DarkConfirmDialog
├── DofBridge/            # .NET Framework 4.8 console app; hosts DirectOutput, receives
│                         #   commands via named pipe "PhosphorDof"
├── DofBridge.x86/        # Identical to DofBridge, compiled x86 for 32-bit DOF drivers
├── dependencies/         # Bundled tools/natives: DirectOutput, projectM, ffmpeg.exe, yt-dlp.exe
├── docs/                 # PLUGIN_AUTHORING_GUIDE.md, KNOWN_ISSUES.md (docs/_local/ is git-ignored)
├── tools/                # Standalone spike/prototype projects (not part of the shipped app)
├── licenses/             # Third-party license notices
└── .github/workflows/    # build.yml, build-debug.yml, release.yml
```

> `Phosphor/Plugins/Plex/` currently contains empty `PlexSource.cs` / `PlexSourceProvider.cs` stubs —
> leftovers from the pre-plug-in era; Plex lives in the `phosphor-plugins` repo.

The host references the contract as a **NuGet `PackageReference`** (`Phosphor.Plugin.Abstractions`)
and ships the single runtime copy of that DLL. The `Phosphor.Plugins.*` projects live in the separate
`phosphor-plugins` repo — they are NOT in this solution.

### Plug-in architecture (important)
Every media source — YouTube and Plex included — is an **external plug-in** discovered at startup
from the host's `plugins/` folder (see `Phosphor/Plugins/Loader/PluginLoader.cs`). There are no
statically-referenced "built-in" sources. A plug-in references ONLY the
`Phosphor.Plugin.Abstractions` **NuGet package** (compile-only: `ExcludeAssets=runtime` /
`PrivateAssets=all` — the host owns the single runtime copy), implements capability interfaces
(`ITextSearchCapable`, `IBrowsable`, `IPagedBrowsable`, `IPlayableResolver`, `IScopedSearchable`,
`IGaplessCapable`, `IDownloadable`, `IFavoritable`, …), and self-deploys into `plugins/<Name>/`. Each
plug-in loads in its own `AssemblyLoadContext` (isolated private deps). The host reaches shared native
tools (`yt-dlp.exe`, `ffmpeg.exe`) via `IPluginHost.GetToolPath`; a provider declares `RequiredTools`
for load-time validation.

### Contract versioning & NuGet
`PluginApi.Current` (in the abstractions repo) is the single source of truth for the contract version
(currently `0.18.0`; the host's `Phosphor.csproj` pins the same version); the package version tracks it.
The host accepts plug-ins whose major.minor is between `PluginApi.MinimumSupported` (0.12) and `Current`.
the `phosphor-abstractions` repo's `.github/workflows/publish.yml` fires on a `v*` tag, derives the
package version from the tag, and pushes to NuGet with no stored API key. A bump is: edit the contract,
tag `vX.Y.Z`, done. The host and plug-ins then move to the new `PackageReference` version.

### Building & distributing plug-ins (dev loop + releases)
- **Dev inner loop:** the `phosphor-plugins` repo's shared `Directory.Build.targets` self-deploys each
  plug-in to `$(PHOSPHOR_HOST_DIR)\plugins\<Name>` when that env var is set (point it at a built host
  output for F5), else to a repo-local `dist\plugins\<Name>`. A fresh host clone has NO plug-ins until
  you build the plugins repo once with `PHOSPHOR_HOST_DIR` set.
- **Distribution model:** a **client release IS the plug-in snapshot**. The host's
  `.github/workflows/release.yml` (on a `v*` tag) builds the host, checks out `phosphor-plugins@latest`,
  builds them into the host's `plugins/`, writes `build-info.json` (host version + plugins commit SHA +
  contract version), and publishes a GitHub Release. There is NO in-app updater.
- **Hotfix path (power-user testers):** the `phosphor-plugins` CI publishes per-plug-in folder artifacts
  (and a combined `plugins.zip`). A tester drops a replacement `plugins/<Name>/` folder in place — the
  runtime `AssemblyLoadContext` loading already supports this. The About tab's "LOADED PLUG-INS" panel
  (and a load-time log line) shows each loaded plug-in's assembly + contract version so a hand-dropped
  DLL is distinguishable from the shipped snapshot.

**Discovery convention (one plug-in per folder):** a folder `plugins/<Name>/` names its provider
assembly `Phosphor.Plugins.<Name>.dll`. When that file exists the loader loads *only* it and skips
reflecting the folder's private dependencies (a real win for multi-DLL plug-ins like YouTube, which
ships AngleSharp/YoutubeExplode alongside it). A folder that doesn't follow the naming convention
falls back to scanning every DLL, so this is a non-breaking speedup, not a hard requirement. This is
deliberately lightweight — do **not** replace it with a `plugin.json` manifest or `MetadataLoadContext`
pre-scan unless a single plug-in folder starts shipping *multiple candidate provider DLLs*; the
`AssemblyLoadContext` isolation already makes a bad/incompatible DLL non-fatal (logged and skipped).

---

## Key Components

### App.xaml.cs
- Entry point. Creates all windows on separate threads (Playfield and Backglass each get their own `Thread` + `Dispatcher`).
- Pre-initializes a shared `LibVLC` instance on a background thread; both the startup "ditty" clip and the backglass reuse it.
- Owns the startup splash screen lifetime.
- Wires global exception handlers.

### JukeboxViewModel.cs
- Single large `ObservableObject` (CommunityToolkit.Mvvm, ~5.6k lines). The authoritative source of truth for UI/app state.
- Owns: search results, now-playing state, category list, playlist management, screensaver state, settings propagation, and dispatch to plug-in sources.
- Queue storage/navigation lives in `Playback/PlayerQueue.cs`, AutoDJ fill in `Playback/AutoDjService.cs`, and stream resolution/seek/gapless in `Playback/JukeboxPlayer.cs`. The VM coordinates them via `PlayerContext` (Player1 = Backglass, Player2 = Topper).
- Keep logical groupings together; the file is large but intentionally cohesive. It is a `partial class`, so a split along an existing seam is acceptable if it clearly helps.
- Genre categories come from `categories.json` via `GenreCategoryStore` (see below).

**Where things are** (search by symbol; line numbers drift and are intentionally not listed):

> Source browse/search/playback are **source-agnostic**: the VM dispatches through the plug-in
> `SourceRegistry` and capability interfaces (there are no YouTube- or Plex-specific methods in the VM).

| Concern | Search for |
|---------|-----------|
| Genre categories / source tiles | `GenreCategoryStore.Load`, `SyncSourceTiles` |
| Browse / drill-down | `EnterBrowseNodeAsync`, `ExpandContainerToLeavesAsync` |
| Search (incl. history) | `DoSearch`, `SearchVideosViaPluginOrLegacy`, `AllSearchHistory` |
| Live-stream playback | `ResolveAndPlayLiveAsync` |
| Queue persistence / playback | `SaveQueue`, `PlayNextOn`, `Player1` / `Player2` |
| Playlists, history, favorites | `_playlists` (`PlaylistManager`), `_history` (`PlayHistory`), `FavoritesIndex` |
### Windows
- All windows inherit `JukeboxWindow` which provides borderless style, Win32 hit-test dragging, and "Expand to Monitor" toggle.
- `PlayfieldProxy` / `BackglassProxy` / `TopperProxy` marshal calls across thread boundaries to their respective `Dispatcher`s.
- `PlayfieldWindow` and `BackglassWindow` run on dedicated threads to keep video rendering off the UI thread.
- `BackglassWindow` and `TopperWindow` implement `IPlaybackHost` (`*.PlaybackHost.cs` partials) so `Playback/JukeboxPlayer` can drive either; idle/ambient visuals live in `*.Ambient.cs`.
- Window layout (position/size) is stored in `AppSettings` and restored on startup.

### AppSettings (Models/AppSettings.cs)
- Plain POCO serialized to/from `settings.json` via `System.Text.Json`.
- Loaded once at startup (`AppSettings.Load()`), saved once on exit.
- **Do not save on every state change** — this is intentional to minimize disk I/O.
- `default_settings.json` is the fallback when no `settings.json` exists.

### Visuals
- `AudioReactiveService` — WASAPI loopback capture → FFT → smoothed `Level`, `Bass`, `Treble`, beat detection. Feeds both blob patterns and projectM PCM.
- Blob patterns (`IBlobPattern`) are rendered on a WPF `Canvas` using `DrawingContext`. Each window (Playfield, Backglass, Topper, DMD) independently selects a pattern from `BlobPattern` enum.
GPU-accelerated via Vortice (Direct3D 11 compute/shaders); falls back to CPU.
- `ProjectMPattern` / `ProjectMRenderer` / `ProjectMInterop` — P/Invoke wrapper around `projectM-4.dll` (native). Renders into an OpenGL surface and blits to WPF. PCM is fed from `AudioReactiveService.ConsumeRawPcm()`.

### Caching
- `VideoCache` — downloads and stores video files locally; respects size cap (`CacheMaxSizeGb`) and clip length cap.
- `PrefetchCache` — background pre-fetch of upcoming tracks.
- `ThumbnailCache` — stores item thumbnails as PNG files.
- `PlaylistCache` / `ResultCache` — in-memory + disk caches for playlist and search results with configurable TTL.
- `CacheDownloadCoordinator` — coordinates cache downloads (driven through the plug-in `IDownloadable` capability).

### Input
- `DofClient` — manages `DofBridge.exe` as a child process, connects via named pipe `"PhosphorDof"`, sends binary commands `[char type][int32 number][int32 value]`. Uses an unbounded `Channel` for FIFO ordering.
- `DirectInputPoller` — polls `DirectInput` joystick axes/buttons on a timer; maps to jukebox actions.

### DofBridge (separate process)
- Must target .NET Framework 4.8 because `DirectOutput.dll` is a COM-based .NET 2.0 assembly.
- Reads binary commands from the named pipe and calls `DirectOutput.Pinball` accordingly.
- Two builds: `x64` and `x86` — the main app picks the right one at runtime via `DofClient.ResolveBridgePath()`.

**Named-pipe protocol** (pipe name `PhosphorDof`):

Each command is written as: `BinaryWriter.Write(char type)` + `Write(int number)` + `Write(int value)`.

| Type char | Meaning | Number | Value |
|-----------|---------|--------|-------|
| `'E'` | Table element trigger | Element number (e.g., 110, 111) | `1` = on, `0` = off |
| `'\0'` | Shutdown signal | — | — |

`DofClient.Trigger(type, number, value)` enqueues a command; `TriggerPulse(type, number)` sends value=1 then auto-sends value=0 after a brief delay. All active triggers are auto-cleared on shutdown.

### Source plug-ins (separate repo)
All sources (YouTube, Plex, Jellyfin, Emby, Vimeo, Dailymotion, SoundCloud, Twitch, SiriusXM, iHeartRadio,
HDHomeRun, IPTV, PodcastIndex, LocalFolder) live in `phosphor-plugins`. The host stays source-agnostic and
only sees them through the contract. Source-specific behavior (e.g. Plex's REST client and stereo downmix
for pinball cabs) is documented in that repo.

---

## Cross-Cutting Decisions

| Decision | Rationale |
|----------|-----------|
| Settings saved on exit only | Minimize disk writes (SSD wear on cabinet PCs) |
| DOF in a separate process | `DirectOutput.dll` requires .NET Framework 4.8; can't load in .NET 8 |
| Two DOF bridge builds (x64/x86) | Some cabinet setups run 32-bit DOF drivers |
| Playfield/Backglass on separate threads | Video decode + render is heavy; keeps UI thread responsive |
| Single shared `LibVLC` instance | LibVLC has significant init cost; sharing avoids double initialization |
| `JukeboxViewModel` kept in one file | Cohesion — large but deliberate; playback/queue/AutoDJ already live in `Playback/` |
| Mouse repositioning disabled by default | Mouse Without Borders (multi-PC KVM) interferes with programmatic cursor moves |
| `RangedObservableCollection` |

---

## Technology Stack

- **UI**: WPF (.NET 8), CommunityToolkit.Mvvm
- **Video**: LibVLCSharp
- **Audio capture**: NAudio (WASAPI loopback)
Vortice Direct3D 11 (Mandelbrot GPU), SkiaSharp, projectM 4 (P/Invoke)
- **Sources**: each media source is an external plug-in (see Plug-in architecture above). **YouTube**
  (`Phosphor.Plugins.YouTube`) has pluggable engines — **YoutubeExplode** (in-process, default) and
  **yt-dlp** (bundled `dependencies/yt-dlp.exe`, self-updating), selectable in the plug-in's settings;
  it ships YoutubeExplode as a private dependency and reaches yt-dlp/ffmpeg via `IPluginHost.GetToolPath`.
  **Plex** (`Phosphor.Plugins.Plex`) is a REST client (no SDK).
- **DOF**: DirectOutput Framework (via DofBridge process)
- **Input**: DirectInput (`SharpDX.DirectInput`)
- **Storage**: `System.Text.Json` for settings/categories; `Microsoft.Data.Sqlite` for the Pinup Popper integration

---

## Common Agent Pitfalls

- Do **not** add `AppSettings` saves inside property setters or event handlers — save on exit only.
- Do **not** move `PlayfieldWindow`/`BackglassWindow` work onto the main dispatcher — they own their own threads.
- Do **not** call `DofClient` synchronously from the UI thread — it queues via `Channel`.
- Do **not** target .NET 8 APIs in `DofBridge` or `DofBridge.x86` — they must remain on .NET Framework 4.8.
- When adding a new `IBlobPattern`, register it in the `BlobPattern` enum and the pattern factory; also consider whether it should be excluded from the random rotation (`ExcludeMandelbrotFromRandom` pattern).
- `JukeboxViewModel` is partial-class friendly (CommunityToolkit source gen) but keep logical groupings together.
- Queue, AutoDJ and stream-resolution logic belong in `Playback/`, not back in the ViewModel or window code-behind.
- Do **not** add code under `Phosphor/Plugins/Plex/` — it holds empty legacy stubs; Plex is a plug-in.

---

## File Ownership Quick Reference

| Area | Primary Files |
|------|--------------|
| Startup & wiring | `App.xaml.cs` |
| All UI state | `JukeboxViewModel.cs` |
| Queue / AutoDJ / playback engine | `Playback/PlayerQueue.cs`, `Playback/AutoDjService.cs`, `Playback/JukeboxPlayer.cs`, `Playback/MediaEngine.cs` |
| Favorites / history / playlists | `Services/FavoritesIndex.cs`, `Services/PlayHistory.cs`, `Services/PlaylistManager.cs` |
| Pinup Popper integration | `Services/Pinup*.cs`, `Models/PinupSettings.cs`, `Windows/IPinupFollower.cs` |
| Settings model | `Models/AppSettings.cs` |
| Window base | `Windows/JukeboxWindow.cs` |
| Video windows | `Windows/PlayfieldWindow.xaml.cs`, `Windows/BackglassWindow.xaml.cs` |
| Screensaver visuals | `Visuals/Patterns/*.cs`, `Visuals/AudioReactiveService.cs` |
| DOF integration | `Input/DofClient.cs`, `DofBridge/Program.cs` |
| Plug-in host (loader/registry/discovery) | `Plugins/Loader/PluginLoader.cs`, `Plugins/SourceRegistry.cs`, `Plugins/DiscoveredProviders.cs`, `Plugins/Host/PluginHost.cs` |
| Plug-in contract | **`phosphor-abstractions` repo** → NuGet `Phosphor.Plugin.Abstractions` (referenced as a package) |
| Source plug-ins (YouTube, Plex, etc.) | **`phosphor-plugins` repo** (`Phosphor.Plugins.*`) — not in this solution |
| Loaded-plugin diagnostics | `Windows/SettingsWindow.xaml.cs` → `RefreshLoadedPlugins()` (About tab) |
| Caching (engine-agnostic; driven via `IDownloadable`) | `Caching/*.cs` |
| Host playback vocabulary | `Video/VideoVocabulary.cs` |
| Data models | `Models/VideoItem.cs`, `Models/Category.cs`, `Models/KeyBindings.cs` |
| Genre categories | `Models/GenreCategoryStore.cs`, `default_categories.json` (shipped), `categories.json` (user) |

---

## GenreCategoryStore & categories.json

`categories.json` is the single stateful backing file for genre categories. `GenreCategoryStore` is a static helper that reads/writes it:

- **Load**: reads `categories.json` from the app directory into memory (cached after first read). If it doesn't exist, it seeds from the shipped `default_categories.json` (never written to).
- **Save / SaveInBackground**: writes the in-memory list back to `categories.json`.
- Plug-in browse tiles (Plex libraries, etc.) are reconciled into the category list via
  `GenreCategoryStore.SyncSourceTiles` (adds new, prunes stale, preserves user customization); saved-search tiles use `SyncSavedSearchTiles`.
- Unlike `AppSettings`, categories **are saved immediately** when the user edits them (not deferred to exit).

---

## Common Task Recipes

- **Add a setting**: Add a property to `Models/AppSettings.cs` and a matching default in `default_settings.json`. Do not add save logic — settings save on exit.
Add an entry to `default_categories.json` (the shipped defaults). The `GenreCategoryStore` handles
- **Add a blob pattern**: Implement `IBlobPattern`, add a value to the `BlobPattern` enum, register in the pattern factory. Decide whether to exclude from random rotation.
- **Add a new window**: Inherit `JukeboxWindow`, create a proxy class if cross-thread access is needed, register and create the window in `App.xaml.cs`.
- **Add a DOF trigger**: Call `DofClient.Trigger('E', number, value)` or `TriggerPulse('E', number)`. Pick an unused element number.
- **Add a source**: In the **`phosphor-plugins` repo**, create a `Phosphor.Plugins.<Name>` project
  referencing only the `Phosphor.Plugin.Abstractions` NuGet package (compile-only:
  `ExcludeAssets=runtime` / `PrivateAssets=all`), implement `IPhosphorSourceProvider` (parameterless
  ctor!) + the capability interfaces the source supports. The shared `Directory.Build.targets`
  self-deploys it automatically — clone an existing plug-in's csproj (e.g. `Phosphor.Plugins.Plex`).
  No host changes needed — it's discovered from `plugins/` at runtime.
- **Add a source-specific feature** (e.g. for Plex): work in that plug-in's project in the `phosphor-plugins` repo. If it needs a new contract capability, add it in `phosphor-abstractions`, publish a new package version, then bump the `PackageReference` here. The host stays source-agnostic.
