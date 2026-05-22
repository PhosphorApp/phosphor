# Phosphor — VPinJukebox

A WPF-based music jukebox designed for virtual pinball cabinets. Phosphor displays music videos across a cabinet's multiple screens (playfield, backglass, topper, DMD) while providing audio-reactive animated visuals, Plex integration, and optional DOF (Direct Output Framework) lighting effects.

## Features

- **YouTube & Plex playback** — Search YouTube or browse Plex music libraries; streams are selected and played via LibVLCSharp
- **Multi-screen layout** — Independent windows for Playfield, Backglass, Topper, and DMD, each independently positioned and sized
- **Audio-reactive visuals** — WASAPI loopback capture drives animated blob patterns, Mandelbrot zoom, projectM visualizations, and ferrofluid simulations in real time
- **DOF lighting** — Triggers cabinet solenoids/LEDs via DirectOutput Framework through an isolated .NET Framework 4.8 bridge process (`DofBridge`)
- **Caching** — Optional video pre-fetch cache, thumbnail cache, playlist cache, and category cache to minimize network round-trips
- **Controller input** — DirectInput joystick/gamepad support alongside keyboard bindings for button-box navigation

## Projects

| Project | Target | Description |
|---------|--------|-------------|
| `VpinJukebox` | .NET 8, WPF | Main application |
| `DofBridge` | .NET Framework 4.8 | Out-of-process DOF host |
| `DofBridge.x86` | .NET Framework 4.8 | x86 variant for 32-bit DOF drivers |

## Requirements

- Windows 10/11
- [VLC media player](https://www.videolan.org/) (or LibVLC redistributable)
- [DirectOutput Framework](https://directoutputframework.github.io/) (optional, for DOF lighting)
- Plex Media Server (optional)
- `ffmpeg.exe` in the `dependencies\` folder (bundled)
- `projectM-4.dll` and `projectM-4-playlist.dll` in `dependencies\projectM\` (bundled)

## Getting Started

1. Clone the repo and open `VpinJukebox.slnx` in Visual Studio 2022+.
2. Build the solution (`Ctrl+Shift+B`). Both `DofBridge` projects must build to `x64\` and `x86\` subfolders alongside the main executable.
3. Run `VpinJukebox`. On first launch a `settings.json` is written next to the executable.
4. Open **Settings** to configure window positions, YouTube API key (if needed), and optional Plex server details.

## Settings

Settings are persisted as `settings.json` next to the executable. Defaults are defined in `VpinJukebox\default_settings.json`. Settings are saved on application exit (not on every change) to minimize disk writes.

## Architecture Overview

See [`AGENTS.md`](AGENTS.md) for a detailed component map intended for AI-assisted development.
