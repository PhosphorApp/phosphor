# Phosphor — Architecture & Agent Guide

This file is authoritative context for AI agents working on this codebase. Read it before planning or editing.

---

## Solution Structure

```
phosphor/
├── Phosphor/          # Main WPF app (.NET 8)
│   ├── App.xaml.cs       # Startup, shared LibVLC init, window orchestration
│   ├── JukeboxViewModel.cs  # Central ViewModel (~3000 lines); owns all app state
│   ├── Models/           # AppSettings, VideoItem, Category, KeyBindings, enums
│   ├── Caching/          # VideoCache, PrefetchCache, ThumbnailCache, PlaylistCache
│   ├── Input/            # DofClient (named-pipe DOF bridge), DirectInputPoller
│   ├── Plex/             # PlexService (REST client, no SDK)
│   ├── Services/         # PlayHistory, PlaylistManager, SearchHistory, StreamSelector, DebugLog
│   ├── Visuals/          # AudioReactiveService, blob patterns, Mandelbrot, projectM interop
│   └── Windows/          # JukeboxWindow base, PlayfieldWindow, BackglassWindow, DmdWindow,
│                         #   TopperWindow, SettingsWindow, PresetBrowserWindow
├── DofBridge/            # .NET Framework 4.8 console app; hosts DirectOutput, receives
│                         #   commands via named pipe "PhosphorDof"
└── DofBridge.x86/        # Identical to DofBridge, compiled x86 for 32-bit DOF drivers
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
- Genre categories come from `categories.json` (bundled) and are persisted via `GenreCategoryStore`.

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
- `ThumbnailCache` — stores YouTube thumbnails as PNG files.
- `PlaylistCache` / `ResultCache` — in-memory + disk caches for YouTube playlist and search results with configurable TTL.

### Input
- `DofClient` — manages `DofBridge.exe` as a child process, connects via named pipe `"PhosphorDof"`, sends binary commands `[char type][int32 number][int32 value]`. Uses an unbounded `Channel` for FIFO ordering.
- `DirectInputPoller` — polls `DirectInput` joystick axes/buttons on a timer; maps to jukebox actions.

### DofBridge (separate process)
- Must target .NET Framework 4.8 because `DirectOutput.dll` is a COM-based .NET 2.0 assembly.
- Reads binary commands from the named pipe and calls `DirectOutput.Pinball` accordingly.
- Two builds: `x64` and `x86` — the main app picks the right one at runtime via `DofClient.ResolveBridgePath()`.

### PlexService
- Vanilla `HttpClient` REST client against the Plex Media Server API — no Plex SDK.
- Requires `serverUrl` + `token` (Plex auth token).
- Supports: library browsing, hub categories, artist/album drill-down, playlist enumeration, stream URL construction.

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
- **YouTube**: YoutubeExplode
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
| Plex | `Plex/PlexService.cs` |
| Caching | `Caching/*.cs` |
| Data models | `Models/VideoItem.cs`, `Models/Category.cs`, `Models/KeyBindings.cs` |
