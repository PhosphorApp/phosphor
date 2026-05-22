# Copilot Instructions

> For full architecture details, component map, and cross-cutting decisions, see [`AGENTS.md`](../AGENTS.md) at the repo root.

## Project Summary
Phosphor is a WPF (.NET 8) music jukebox for virtual pinball cabinets. It plays YouTube and Plex music videos across multiple screens (Playfield, Backglass, Topper, DMD) with audio-reactive animated visuals and optional DOF cabinet lighting. A companion .NET Framework 4.8 process (`DofBridge`) hosts the DirectOutput Framework and communicates via named pipe.

## Project Guidelines
- Prefers minimal IO — avoid unnecessary disk writes (e.g., only save settings on exit, not on every state change).
- Be aware that using the Mouse Without Borders utility to share keyboard/mouse across multiple PCs may interfere with programmatic cursor repositioning.
- `PlayfieldWindow` and `BackglassWindow` run on their own threads — do not marshal their work onto the main dispatcher.
- `DofBridge` / `DofBridge.x86` must stay on .NET Framework 4.8; do not add .NET 8-only APIs there.
- When adding a new blob/visual pattern, implement `IBlobPattern`, add to the `BlobPattern` enum, and register in the pattern factory.
- `AppSettings` is saved once on exit; do not save inside property setters or event handlers.

## Git Workflow
- At the start of a working session, do a `git pull` to ensure the local repo is up to date.
- "check in" means: `git add -A && git commit` (local only, do NOT push).
- "check in and push" means: `git add -A && git commit && git push`.