# Phase 1 — Spike / Feasibility ✅ DONE

**Status:** Complete. Commits `baf619f` (analysis doc), `65795ce` (spike).
**Goal:** Prove yt-dlp mechanics in isolation without touching the running app.

## What was delivered
- Bundled `dependencies/yt-dlp.exe` (`2026.07.04`, 17.4 MB), tracked like `ffmpeg.exe`.
- `Phosphor.csproj` copy-to-output item so the exe lands next to `Phosphor.exe`.
- `Phosphor/YtDlp/YtDlpSpike.cs` — throwaway Option-B resolver (direct process
  invocation), parses `--dump-single-json`, resolves URLs via `-g`. **Inert.**
- Terminal validation against `jNQXAC9IVRw` (see analysis Appendix B).

## Validated facts (carried forward)
- `-f "bv*+ba/b" -g` → **two URLs** (video + audio) — maps to `Media` + `AddSlave`.
- `-g` URLs carry `expire=` → short-lived, IP-bound → **resolve fresh per play**.
- Native structured `chapters[]` present → chapters win is real (Phase 5).
- `--dump-single-json` exposes all needed fields (`id/title/uploader/duration/
  description/chapters/thumbnails/formats`).

## Disposition
- `YtDlpSpike.cs` → **REMOVE** at/after Phase 3 (replaced by real `YtDlpVideoEngine`).
- Everything else → keep.

## Outcome
Green light to Phase 2. No open blockers.
