# Final Plex Migration — Purging Plex Naming from the Host

> **Purpose.** After the Plex source was extracted into an out-of-tree plug-in
> (commit `ac0170e`, "Extract Plex to plugin…"), a number of Plex-specific names
> still linger in the **host** project. This doc tracks the incremental cleanup so
> it can be picked up later with full context.
>
> **Context flag:** the dev/tester base runs on a clean slate. Per project
> convention (`AGENTS.md` / copilot-instructions), **no migration or
> backward-compatibility shims** are needed — stale on-disk data (e.g. legacy
> `categories.json` entries) is acceptable to break; testers recreate as needed.

## Background

Plex now surfaces entirely through the **generic source plug-in path**:

- Categories/tiles are created as **generic source tiles** in
  `GenreCategoryStore.SyncSourceTiles` — keyed by `(SourceInstanceId,
  SourceCategoryId)` with `IsGenericSource => SourceInstanceId != null`.
- Nothing in the host writes the old bespoke Plex tile fields anymore, so the
  entry-level `IsPlex` (`PlexLibraryKey != null`) was **always false** on live
  data — pure dead weight.

## ✅ Done

### DmdWindow.xaml — dead `PlexItemType` result bindings (commit `7cbfefc`)
`PlexItemType` (and `PlexRatingKey`/`PlexHubKey`/`PlexHubType`/`PlexAudioStream`)
were purged from the host `VideoItem` in `ac0170e`, so these result-row
`DataTrigger`s never fired. Removed:
- Thumbnail `Image` `Hub`-visibility trigger.
- Play button `Artist`/`Album`/`Hub`/`Playlist` "browse" content triggers.
The live `IsGenericContainer` path already handles container image-hiding and the
Open/⏎ affordance. (The colorful-icon templates added just before this — commit
`fbaf356` — also use only the live `IsGenericContainer`/`ContainerIcon` path.)

### Dead category-tile Plex fields (Option A)
Removed the write-only, round-tripped-only Plex fields from the category layer:
- **`GenreCategoryStore.GenreCategoryEntry`**: `PlexLibraryKey`,
  `PlexLibraryType`, `PlexInstanceId`, `PlexHubsEnabled`, `PlexPlaylistsEnabled`,
  and `IsPlex`.
- **`GenreCategoryStore.SyncSourceTiles`**: removed the one-time legacy-Plex
  prune line (`entries.RemoveAll(e => e.IsPlex && !e.IsGenericSource)`) — safe on
  a clean slate.
- **`SettingsWindow.CategoryVisibilityItem`**: `IsPlex` + the five `Plex*`
  fields, plus the two round-trip blocks (load from / save to `GenreCategoryEntry`).
- **`SettingsWindow.CategoryRemove_Click`**: dropped the `item.IsPlex` guard.
- **`SettingsWindow.xaml`**: removed the three `IsPlex` `DataTrigger`s in the
  category editor (add/name/searchterm rows), including the "Plex library" tooltip.
- **`JukeboxViewModel`** (`RebuildCategories` region): removed the dead
  `if (entry.IsPlex && !entry.IsGenericSource) continue;` skip branch.

Build verified clean after each change.

## ⛔ Deliberately NOT removed — LIVE features that merely carry "Plex" in the name

These are working features, not dead code. Renaming them to source-agnostic
names is a **larger, behavior-touching refactor** (Option B) and should be its
own scoped effort. Listed here so we don't mistake them for dead code later.

| Member | Where | Why it's live |
| --- | --- | --- |
| `KnownSourceTypeIds.Plex` | `Phosphor.Plugins`, `SourceRegistry`, `SettingsWindow` config renderer | Used to detect Plex instances and render the Hubs/Playlists flags (Plex-only concept). Real plug-in infra. |
| `PlexPlaylistCacheEnabled` / `PlexPlaylistCacheMaxAgeHours` | `AppSettings`, `SettingsWindow` (`CbPlexPlaylistCacheEnabled`, `CbPlexPlaylistCacheMaxAge`, `PlexPlaylistCacheSizeText`, `PurgePlexPlaylistCache_Click`, `SetPlexPlaylistCacheSize`) | Wired to `vm.SetupPlexPlaylistCache(...)` + live purge/size readout. Working cache feature. |
| `PlexGaplessPlayback` / `CbPlexGapless` | `AppSettings`, `SettingsWindow`, `DmdWindow` | Maps to `vm.GaplessPlayback`. Working playback feature. |
| `VideoItem.IsPlex` (`VideoId.StartsWith("plex:")`) | `VideoItem` | Source discriminator still used by the host. |
| `IsPlexBrowsing` | `JukeboxViewModel`, `DmdWindow` | Live browse-state flag. |

## 🪜 Remaining work (Option B — future)

Goal: make the host fully Plex-name-free by renaming the live features above to
source-agnostic concepts. Sketch:

1. **Playlist cache** → generic per-source playlist cache naming
   (e.g. `SourcePlaylistCache*`), migrating `AppSettings` fields + the settings
   UI (`Cb*`, purge handler, size readout) + `JukeboxViewModel.SetupPlexPlaylistCache`.
2. **Gapless playback** → `GaplessPlayback` end-to-end (drop the `Plex` prefix on
   the `AppSettings` field + `CbPlexGapless`); the VM already exposes
   `GaplessPlayback`.
3. **`KnownSourceTypeIds.Plex`** → keep as a type-id constant (it legitimately
   identifies the Plex plug-in), but audit host-side `== KnownSourceTypeIds.Plex`
   branches (e.g. the Hubs/Playlists flag renderer) to see whether they can be
   driven by a **capability** the plug-in advertises rather than a hard-coded
   type-id comparison. This is the only genuinely architectural item.
4. **`VideoItem.IsPlex` / `IsPlexBrowsing`** → evaluate whether the host still
   needs a Plex-specific discriminator or whether a generic source/browse flag
   suffices.

Each of the above is independent and can land as its own commit. Items 1–2 are
mechanical renames (low risk); item 3 is the real design decision.

## Notes

- No new shims were introduced; the only removed migration code (the one-time
  legacy-Plex prune) is acceptable to drop given the clean-slate tester base.
- If a tester still has legacy bespoke Plex entries in `categories.json`, those
  JSON properties now deserialize-and-ignore; the stale entry (lacking
  `SourceInstanceId`) simply won't render a generic tile. Re-syncing sources
  regenerates the correct generic tiles.
