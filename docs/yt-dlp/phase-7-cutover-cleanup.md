# Phase 7 — Cutover & Cleanup ⬜

**Status:** Not started (final). **Prereq:** Phases 2–6 as adopted.
**Goal:** Decide defaults, remove throwaway/dead code, and refresh docs.

## Decisions
- **Default engines:** does `AppSettings.VideoEngine` default flip to `YtDlp`? Does a
  `SearchEngine` default change? (Hybrid = yt-dlp video + YoutubeExplode search.)
- **Package prune:** drop `YoutubeExplode` only if *no* seam uses it (likely retained
  for search unless full yt-dlp search adopted).

## Cleanup checklist (sync with tracker README)
- [ ] Delete `Phosphor/YtDlp/YtDlpSpike.cs` if not already removed in Phase 3.
- [ ] Delete/repurpose `Phosphor/YtDlp/` folder.
- [ ] Revisit `App.xaml.cs` YoutubeExplode exception suppression (needed for failover?).
- [ ] `StreamSelector` visibility → internal if engine-private.
- [ ] Update `AGENTS.md` (Technology Stack: YouTube = YoutubeExplode → note yt-dlp
  engine), `README.md` Known Issues (scrubbing), and `Phosphor.csproj` if deps change.
- [ ] Update `default_settings.json` with any new engine settings.

## Validation
- Full build + smoke test both engine configurations.
- Confirm the tracker's changed-files ledger matches the final diff vs. `origin/master`.
