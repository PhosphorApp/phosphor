# Phosphor — Architecture & Agent Guide

This file is authoritative context for AI agents working on this codebase. Read it before planning or editing.

---

## Solution Structure

```
phosphor/
├── Phosphor/          # Main WPF app (.NET 8) — the plug-in HOST
│   ├── App.xaml.cs       # Startup, shared LibVLC init, window orchestration
│   ├── JukeboxViewModel.cs  # Central ViewModel; owns all app state, dispatches to plug-in sources
│   ├── Models/           # AppSettings, VideoItem, Category, KeyBindings, enums
│   ├── Caching/          # VideoCache, PrefetchCache, ThumbnailCache, PlaylistCache (engine-agnostic)
│   ├── Input/            # DofClient (named-pipe DOF bridge), DirectInputPoller
│   ├── Plugins/          # Plug-in host: SourceRegistry, DiscoveredProviders, PluginLoader,
│   │                     #   PluginHost, KnownSourceTypeIds, PluginSettingsFactory
│   ├── Video/            # Host-side playback vocabulary (VideoVocabulary.cs) — NOT the engines
│   ├── Services/         # PlayHistory, PlaylistManager, SearchHistory, DebugLog
│   ├── Visuals/          # AudioReactiveService, blob patterns, Mandelbrot, projectM interop
│   └── Windows/          # JukeboxWindow base, PlayfieldWindow, BackglassWindow, DmdWindow,
│                         #   TopperWindow, SettingsWindow, PresetBrowserWindow
├── Phosphor.Plugin.Abstractions/   # The plug-in CONTRACT (IPhosphorSource, capabilities,
│                         #   SourceItem/SourceCategory/ResolvedStream, IPluginHost)
├── Phosphor.Plugins.YouTube/       # YouTube source plug-in (YoutubeExplode + yt-dlp engines)
├── Phosphor.Plugins.Plex/          # Plex source plug-in (REST client, no SDK)
├── Phosphor.Plugins.Jellyfin/ .Emby/ .Vimeo/ .Dailymotion/ .SoundCloud/
│   .SiriusXM/ .IHeartRadio/ .LocalFolder/   # Additional source plug-ins
├── DofBridge/            # .NET Framework 4.8 console app; hosts DirectOutput, receives
│                         #   commands via named pipe "PhosphorDof"
└── DofBridge.x86/        # Identical to DofBridge, compiled x86 for 32-bit DOF drivers
```

### Plug-in architecture (important)
Every media source — YouTube and Plex included — is an **external plug-in** discovered at startup
from the host's `plugins/` folder (see `Phosphor/Plugins/Loader/PluginLoader.cs`). There are no
statically-referenced "built-in" sources. A plug-in references ONLY
`Phosphor.Plugin.Abstractions` (compile-only; the host owns the single runtime copy), implements
capability interfaces (`ITextSearchCapable`, `IBrowsable`, `IPagedBrowsable`, `IPlayableResolver`,
`IScopedSearchable`, `IGaplessCapable`, `IDownloadable`, `IFavoritable`, …), and self-deploys into
`Phosphor/bin/.../plugins/<Name>/`. Each plug-in loads in its own `AssemblyLoadContext` (isolated
private deps). The host reaches shared native tools (`yt-dlp.exe`, `ffmpeg.exe`) via
`IPluginHost.GetToolPath`; a provider declares `RequiredTools` for load-time validation.
```


---

## Key Components

### App.xaml.cs
- Entry point. Creates all windows on separate threads (Playfield and Backglass each get their own `Thread` + `Dispatcher`).
- Pre-initializes a shared `LibVLC` instance on a background thread; both the startup "ditti" clip and the backglass reuse it.
- Owns the startup splash screen lifetime.
- Wires global exception handlers.

### JukeboxViewModel.cs
- Single large `ObservableObject` (CommunityToolkit.Mvvm). The authoritative source of truth for all UI state.
- Owns: search results, now-playing state, category list, playlist management, Plex integration, screensaver state, settings propagation.
- **Do not split into multiple files** without good reason — the entire file is intentionally cohesive.
- Genre categories come from `categories.json` via `GenreCategoryStore` (see below).

**Section Map** (landmarks are approximate — line numbers drift; search by symbol/comment):

> Source browse/search/playback are now **source-agnostic**: the VM dispatches through the plug-in
> `SourceRegistry` and capability interfaces (there are no YouTube- or Plex-specific browse methods
> in the VM anymore). Look for `EnterBrowseNodeAsync`, `ExpandContainerToLeavesAsync`, `DoSearch`,
> and the `...ViaPluginOrLegacy` helpers.

| Line | Section |
|------|---------|
| ~23 | Genre categories (loaded from `categories.json`) |
| ~77 | Categories (playlists + genres, rebuilt dynamically) |
| ~80 | Observable state properties |
| ~311 | Search history |
| ~384 | Video / thumbnail / category caches |
| ~429 | Quality, audio channel, network buffering settings |
| ~490 | Duration filter & pagination |
| ~532 | Category management (add / remove / reorder) |
| ~2005 | "Find More Like This" |
| ~2020 | Queue persistence (saved to disk) |
| ~2049 | Queue & Playback (play, stop, skip, previous) |
| ~2439 | Playlist management (create, delete, add/remove items) |
| ~2625 | AutoDJ (automatic queue fill from genres/videos) |
| ~2843 | Play history |

### Windows
- All windows inherit `JukeboxWindow` which provides borderless style, Win32 hit-test dragging, and "Expand to Monitor" toggle.
- `PlayfieldProxy` / `BackglassProxy` marshal calls across thread boundaries to their respective `Dispatcher`s.
- `PlayfieldWindow` and `BackglassWindow` run on dedicated threads to keep video rendering off the UI thread.
- Window layout (position/size) is stored in `AppSettings` and restored on startup.

### AppSettings (Models/AppSettings.cs)
- Plain POCO serialized to/from `settings.json` via `System.Text.Json`.
- Loaded once at startup (`AppSettings.Load()`), saved once on exit.
- **Do not save on every state change** — this is intentional to minimize disk I/O.
- `default_settings.json` is the fallback when no `settings.json` exists.

### Visuals
- `AudioReactiveService` — WASAPI loopback capture → FFT → smoothed `Level`, `Bass`, `Treble`, beat detection. Feeds both blob patterns and projectM PCM.
- Blob patterns (`IBlobPattern`) are rendered on a WPF `Canvas` using `DrawingContext`. Each window (Playfield, Backglass, Topper, DMD) independently selects a pattern from `BlobPattern` enum.
- `MandelbrotGpuRenderer` — GPU-accelerated via `SharpDX`/`Direct2D`; falls back to CPU. Controlled by `AppSettings.MandelbrotUseGpu`.
- `ProjectMPattern` / `ProjectMRenderer` / `ProjectMInterop` — P/Invoke wrapper around `projectM-4.dll` (native). Renders into an OpenGL surface and blits to WPF. PCM is fed from `AudioReactiveService.ConsumeRawPcm()`.

### Caching
- `VideoCache` — downloads and stores video files locally; respects size cap (`CacheMaxSizeGb`) and clip length cap.
- `PrefetchCache` — background pre-fetch of upcoming tracks.
- `ThumbnailCache` — stores item thumbnails as PNG files.
- `PlaylistCache` / `ResultCache` — in-memory + disk caches for playlist and search results with configurable TTL.

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

### Plex source (`Phosphor.Plugins.Plex`)
- Vanilla `HttpClient` REST client (`PlexService.cs`) against the Plex Media Server API — no Plex SDK.
- Requires `serverUrl` + `token` (Plex auth token); multi-instance (two servers) supported via the provider.
- Supports: library browsing, hub categories, artist/album drill-down, playlist enumeration, stream URL
  construction, in-library scoped search, and gapless (stable pre-built audio URL). Stereo/2-channel
  downmix is enforced for pinball cabs (surround channels drive exciters, not music).

---

## Cross-Cutting Decisions

| Decision | Rationale |
|----------|-----------|
| Settings saved on exit only | Minimize disk writes (SSD wear on cabinet PCs) |
| DOF in a separate process | `DirectOutput.dll` requires .NET Framework 4.8; can't load in .NET 8 |
| Two DOF bridge builds (x64/x86) | Some cabinet setups run 32-bit DOF drivers |
| Playfield/Backglass on separate threads | Video decode + render is heavy; keeps UI thread responsive |
| Single shared `LibVLC` instance | LibVLC has significant init cost; sharing avoids double initialization |
| No Plex SDK | Reduces dependencies; the REST API is stable and sufficient |
| `JukeboxViewModel` kept in one file | Cohesion — large but deliberate; splitting adds navigation overhead |
| Mouse repositioning disabled by default | Mouse Without Borders (multi-PC KVM) interferes with programmatic cursor moves |
| `BulkObservableCollection` / `RangedObservableCollection` | Batch UI updates to avoid per-item `CollectionChanged` storms |

---

## Technology Stack

- **UI**: WPF (.NET 8), CommunityToolkit.Mvvm
- **Video**: LibVLCSharp
- **Audio capture**: NAudio (WASAPI loopback)
- **Visuals**: Custom WPF `DrawingContext`, SharpDX/Direct2D (Mandelbrot GPU), projectM 4 (P/Invoke)
- **Sources**: each media source is an external plug-in (see Plug-in architecture above). **YouTube**
  (`Phosphor.Plugins.YouTube`) has pluggable engines — **YoutubeExplode** (in-process, default) and
  **yt-dlp** (bundled `dependencies/yt-dlp.exe`, self-updating), selectable in the plug-in's settings;
  it ships YoutubeExplode as a private dependency and reaches yt-dlp/ffmpeg via `IPluginHost.GetToolPath`.
  **Plex** (`Phosphor.Plugins.Plex`) is a REST client (no SDK).
- **DOF**: DirectOutput Framework (via DofBridge process)
- **Input**: DirectInput (via SharpDX or interop)
- **Serialization**: System.Text.Json

---

## Common Agent Pitfalls

- Do **not** add `AppSettings` saves inside property setters or event handlers — save on exit only.
- Do **not** move `PlayfieldWindow`/`BackglassWindow` work onto the main dispatcher — they own their own threads.
- Do **not** call `DofClient` synchronously from the UI thread — it queues via `Channel`.
- Do **not** target .NET 8 APIs in `DofBridge` or `DofBridge.x86` — they must remain on .NET Framework 4.8.
- When adding a new `IBlobPattern`, register it in the `BlobPattern` enum and the pattern factory; also consider whether it should be excluded from the random rotation (`ExcludeMandelbrotFromRandom` pattern).
- `JukeboxViewModel` is partial-class friendly (CommunityToolkit source gen) but keep logical groupings together.

---

## File Ownership Quick Reference

| Area | Primary Files |
|------|--------------|
| Startup & wiring | `App.xaml.cs` |
| All UI state | `JukeboxViewModel.cs` |
| Settings model | `Models/AppSettings.cs` |
| Window base | `Windows/JukeboxWindow.cs` |
| Video windows | `Windows/PlayfieldWindow.xaml.cs`, `Windows/BackglassWindow.xaml.cs` |
| Screensaver visuals | `Visuals/Patterns/*.cs`, `Visuals/AudioReactiveService.cs` |
| DOF integration | `Input/DofClient.cs`, `DofBridge/Program.cs` |
| Plug-in host (loader/registry/discovery) | `Plugins/Loader/PluginLoader.cs`, `Plugins/SourceRegistry.cs`, `Plugins/DiscoveredProviders.cs`, `Plugins/Host/PluginHost.cs` |
| Plug-in contract | `Phosphor.Plugin.Abstractions/*` |
| YouTube source (engines + YoutubeExplode/yt-dlp) | `Phosphor.Plugins.YouTube/*` |
| Plex source (REST client) | `Phosphor.Plugins.Plex/*` |
| Caching (engine-agnostic; driven via `IDownloadable`) | `Caching/*.cs` |
| Host playback vocabulary | `Video/VideoVocabulary.cs` |
| Data models | `Models/VideoItem.cs`, `Models/Category.cs`, `Models/KeyBindings.cs` |
| Genre categories | `Models/GenreCategoryStore.cs`, `categories.json` |

---

## GenreCategoryStore & categories.json

`categories.json` is the single stateful backing file for genre categories. `GenreCategoryStore` is a static helper that reads/writes it:

- **Load**: reads `categories.json` from the app directory into memory (cached after first read).
- **Save / SaveInBackground**: writes the in-memory list back to `categories.json`.
- On first run, the app ships a default `categories.json`; there is no separate seed file.
- Plug-in browse tiles (Plex libraries, etc.) are reconciled into the category list via
  `GenreCategoryStore.SyncSourceTiles` (adds new, prunes stale, preserves user customization).
- Unlike `AppSettings`, categories **are saved immediately** when the user edits them (not deferred to exit).

---

## Common Task Recipes

- **Add a setting**: Add a property to `Models/AppSettings.cs` and a matching default in `default_settings.json`. Do not add save logic — settings save on exit.
- **Add a genre category**: Add an entry to `categories.json`. The `GenreCategoryStore` handles serialization; no code change needed for static categories.
- **Add a blob pattern**: Implement `IBlobPattern`, add a value to the `BlobPattern` enum, register in the pattern factory. Decide whether to exclude from random rotation.
- **Add a new window**: Inherit `JukeboxWindow`, create a proxy class if cross-thread access is needed, register and create the window in `App.xaml.cs`.
- **Add a DOF trigger**: Call `DofClient.Trigger('E', number, value)` or `TriggerPulse('E', number)`. Pick an unused element number.
- **Add a source**: Create a `Phosphor.Plugins.<Name>` project referencing only
  `Phosphor.Plugin.Abstractions` (compile-only), implement `IPhosphorSourceProvider` (parameterless
  ctor!) + the capability interfaces the source supports, and add a self-deploy target (clone an
  existing plug-in's csproj, e.g. `Phosphor.Plugins.Plex`). No host changes needed — it's discovered.
- **Add a Plex feature**: Work in `Phosphor.Plugins.Plex/PlexService.cs` (REST call) and map it to the
  contract in `PlexSource.cs`/`PlexMappings.cs`. The host stays source-agnostic.
