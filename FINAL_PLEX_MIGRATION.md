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

### Option B — source-agnostic renames & de-coupling (steps 1–4)
The "live features carrying Plex in the name" were made source-agnostic across
four commits:

- **Playlist cache** → `SourcePlaylistCache*`. Renamed `AppSettings`
  (`SourcePlaylistCacheEnabled`/`SourcePlaylistCacheMaxAgeHours`), the VM
  (`SourcePlaylistCache` / `SetupSourcePlaylistCache`, cache prefix `plex_` →
  `source_`), the settings UI (`CbSourcePlaylistCache*`, `SourcePlaylistCacheSizeText`,
  `PurgeSourcePlaylistCache_Click`, `SetSourcePlaylistCacheSize`), `default_settings.json`,
  and all call sites. Pure rename; local cache-folder rename is acceptable on a clean slate.
- **Gapless playback** → dropped the `Plex` prefix: `AppSettings.GaplessPlayback`,
  `CbGapless`. The VM already exposed `GaplessPlayback`.
- **`IsPlexBrowsing`** → **removed** (dead code — only ever assigned `false`).
  `IsSearchScoped` reduces to `IsGenericScopedSearchAvailable`; `DmdWindow` browse
  checks reduce to `IsGenericBrowsing`.
- **Search hint** → new `ISearchHintProvider` capability. Sources author their own
  query-syntax hint (Plex/YouTube implement it); the host reads
  `ActiveSearchSourceHint` instead of a hard-coded type-id `switch`. Removed the
  now-dead `ActiveSearchSourceTypeId`.
- **Plex id-shape routing** → **removed**. Plex leaves already carry
  `SourceInstanceId` (set in `PlexMappings.ToSourceItem`; the host's carried-VideoItem
  branch never applies to the plug-in's separate `VideoItem` type), so the
  `plex:`-prefix fallbacks were unneeded. Removed `VideoItem.IsPlex`,
  `SourceRegistry.PlexInstances`, `ActivePlexSource`, and the never-set
  `_activePlexInstanceId`. `SourceForItem` / `IsItemCacheable` / gapless /
  `NowPlayingSourceText` now route purely by `SourceInstanceId`.

## ⛔ Deliberately NOT removed — LIVE features that merely carry "Plex" in the name

`KnownSourceTypeIds.Plex` legitimately identifies the Plex plug-in and stays as a
type-id constant. One host-side `== KnownSourceTypeIds.Plex` branch remains — see
Remaining work below.

## ✅ Done (cont.) — Hubs/Playlists per-library flags now source-agnostic

The inline library editor's hard-coded Plex "Hubs"/"Playlists" checkboxes (gated
on `cfg.TypeId == KnownSourceTypeIds.Plex` — the **last** host-side Plex type-id
branch) are now generic. The plug-in contract already exposed per-library
sub-options (`ConfigOption.SubOptions` / `ConfigSubOption`), and Plex already
declared `hubs`/`playlists` there; the host was simply discarding them. Changes:

- **Contract**: added optional `Description` (tooltip) to `ConfigSubOption`.
- **Host model**: `SourceLibraryMapping` replaced its `HubsEnabled`/
  `PlaylistsEnabled` bools with a source-agnostic `Dictionary<string,bool> SubFlags`.
- **Host UI** (`SettingsWindow.xaml.cs`): `FetchAvailableLibrariesAsync` captures
  the plug-in's declared sub-options; `BuildInlineLibraryEditor` renders one
  checkbox per declared sub-option (bound to `SubFlags`) instead of the hard-coded
  Plex block; a one-shot background prefetch surfaces the checkboxes for
  already-added libraries; adds seed `SubFlags` from the declared defaults.
- **Plex plug-in**: `PlexLibraryMapping` persists `SubFlags` (matching the host
  JSON shape) with `HubsEnabled`/`PlaylistsEnabled` as `[JsonIgnore]` computed
  accessors, so its internal tile logic is unchanged; the declared sub-options
  carry tooltips.

The host is now **fully free of hard-coded Plex type-id branches** — all
Plex-specific sub-toggle knowledge lives in the Plex plug-in.

## 🧭 Broader follow-up — YouTube-coupled result caches (separate effort)

> **Not part of the Plex naming cleanup.** Logged here so it isn't lost. This is a
> genuine *behavioral* coupling, not a naming issue, and is larger than the items
> above.

The Settings → Cache page has two result-page caches still labelled for YouTube:

- **`CategoryCache`** ("YouTube Categories") — caches the paginated results behind
  genre category tiles.
- **`YtPlaylistCache`** ("YouTube Playlists") — caches the results behind live
  playlists.

Unlike the Plex features above (whose behavior was already source-agnostic and
only the *names* lingered), these labels are currently **accurate**: genre tiles
and live playlists run their searches against the YouTube path specifically. In
`JukeboxViewModel.DoSearch`, `_activeResultCache` is only attached when
`sourceInstanceId == null` (the YouTube-bound tile/playlist path); an ad-hoc
search against any other source deliberately skips these caches:

```
// Only the YouTube-bound path (tiles/live playlists, sourceInstanceId == null) uses these
// caches — an ad-hoc search against another source must not attach a YouTube-shaped
// category/playlist cache.
```

So the abstraction gap is **not** the label — it's that genre tiles and live
playlists don't yet flow through the generic per-source path the way Plex tiles
do. Renaming the checkboxes to source-neutral text would *hide* a real coupling
rather than fix it; the honest sequence is to de-couple the feature first, then
the cache naming follows.

**Off-the-cuff direction (to evaluate later):** rather than the host owning
"YouTube categories," push these into the plug-in itself — i.e. a source declares
its own browsable categories/collections much like Plex declares its libraries
today. The host would then treat YouTube categories as just another source's
generic tiles, routed by `SourceInstanceId`, and the result caches become
per-source (like `SourcePlaylistCache`) instead of YouTube-shaped. Needs its own
plan and scoping effort.

## Notes

- No new shims were introduced; the only removed migration code (the one-time
  legacy-Plex prune) is acceptable to drop given the clean-slate tester base.
- If a tester still has legacy bespoke Plex entries in `categories.json`, those
  JSON properties now deserialize-and-ignore; the stale entry (lacking
  `SourceInstanceId`) simply won't render a generic tile. Re-syncing sources
  regenerates the correct generic tiles.
