# Phosphor — Source Plug-in Architecture Analysis

Exploratory analysis (branch `plugin-rework`) for reworking Phosphor's music/video
**sources** (currently **YouTube** and **Plex**) into a **plug-in architecture** so
additional sources can be added, enabled, and disabled independently — potentially
by third parties dropping a DLL into a `plug-ins` folder scanned at startup.

**Analysis phase only — no production code changes yet.**

---

## 🎯 Goal

Today Phosphor has two hard-wired sources:

- **YouTube** — discovery + playback via a reverse-engineered library
  (YoutubeExplode) or `yt-dlp`. It already has clean seams: `ISearchEngine`
  (discovery) and `IVideoEngine` (resolve/download).
- **Plex** — a REST client (`PlexService`) that returns `VideoItem`s carrying a
  direct `StreamUrl`, bypassing the YouTube engine seams entirely.

The vision: make each source a **plug-in implementing a common contract**
(e.g. `IPhosphorPlugin` / `IPhosphorSource`), so:

1. New sources (Jellyfin, Emby, local folders, Spotify-via-preview, SoundCloud,
   Subsonic, Navidrome, etc.) can be added without touching the core.
2. Sources can be enabled/disabled at runtime.
3. Third parties can author a source, compile a DLL, drop it in a `plug-ins`
   folder, and have Phosphor discover it on startup.

---

## 🧩 Current source-coupling surface

Where "which source is this?" leaks into the codebase today. Any plug-in rework
must absorb or replace each of these:

| Concern | Location | Coupling today |
|--------|----------|----------------|
| Source discriminator | `VideoItem.cs` | `IsPlex` / `IsYouTube` computed from `VideoId` prefix (`"plex:"`); `IsPlexVideo`, `IsAudioOnly`, `PlexItemType`, `PlexRatingKey`, `PlexHubKey`, `PlexHubType`, `PlexAudioStream` — Plex-specific fields on the shared model |
| Discovery seam | `Search/ISearchEngine.cs`, `SearchEngineFactory.cs` | Clean interface, but **YouTube-shaped** (playlist ids, channel handles) and explicitly says "Plex is orthogonal and does not use this seam" |
| Playback seam | `Video/IVideoEngine.cs`, `VideoEngineFactory.cs` | Clean interface, but **YouTube-shaped** (`videoId`, stream resolution). Plex bypasses it via `VideoItem.StreamUrl` |
| Source dispatch | `JukeboxViewModel.cs` | Many `if (category.IsPlex…)` / `_plex.IsConfigured` branches (`SelectCategoryAsync`, hub/playlist browsing, playback) |
| Plex client | `Plex/PlexService.cs` (~910 lines) | Concrete service instantiated directly in the VM (`private readonly PlexService _plex = new();`) |
| Settings | `Models/AppSettings.cs` | Per-source flat fields: `VideoEngine`, `SearchEngine`, `PlexServerUrl`, `PlexToken`, `PlexLibraries`, `PlexStereoAudio`, `PlexGaplessPlayback` |
| Categories / navigation | `Category.cs`, `GenreCategoryStore.cs` | Plex-specific category kinds (`IsPlexHub`, `IsPlexPlaylist`, `IsPlexHubList`, …) |
| Settings UI | `Windows/SettingsWindow.xaml(.cs)` | Hard-coded Plex configuration panel |

**Key takeaway:** YouTube already has a partial abstraction (two engine seams).
Plex has **none** — it is woven directly into the ViewModel and the shared
`VideoItem`. The bulk of the work is not "define an interface," it's
**untangling Plex from `JukeboxViewModel` and `VideoItem`**.

---

## 🏗️ The two paths

### Option A — Built-ins + interface for *additional* sources ("hybrid")

Keep YouTube and Plex exactly as they are (built directly into the core), but
define an `IPhosphorSource` contract that **new/third-party** sources implement.
The core would dispatch: "is this one of the two known sources? use the hard-wired
path. Otherwise, route through the plug-in interface."

**Pros**
- Lowest effort and lowest risk — no rewrite of the two most-used, most-tested code
  paths.
- No regression risk to YouTube scrubbing/caching or Plex drill-down, which are
  both subtle and heavily optimized.
- Delivers the headline feature (third parties can add sources) quickly.

**Cons**
- **Two code paths forever.** The core keeps `if (IsPlex) … else if (plugin) …`
  branching. The interface is validated only by *new* sources, so it's easy for the
  contract to be subtly wrong/insufficient (the built-ins never exercise it).
- The plug-in API becomes a **second-class citizen** — third-party sources can't do
  things the built-ins do (e.g. custom category hubs, gapless playback, audio-stream
  selection) unless the interface is expanded to match, at which point you've done
  most of the "clean" work anyway.
- `VideoItem` stays polluted with Plex-specific fields; plug-ins either can't use
  them or must abuse them.
- "Feels hacky" — correct instinct. This is the tactical, not architectural, choice.

### Option B — Everything is a plug-in ("clean separation")

Re-architect YouTube and Plex to be the **first two implementations** of
`IPhosphorSource`, loaded through the same mechanism as third-party plug-ins
(possibly still shipped in-box / statically referenced, but behind the interface).

**Pros**
- **One code path.** The core never special-cases a source. `JukeboxViewModel`
  loses all its `IsPlex` branches.
- The contract is **dogfooded** by the two built-ins, so it's proven adequate before
  any third party relies on it.
- `VideoItem` can shed Plex-specific fields (move them behind a source-owned
  `object? SourceState` / typed payload).
- Cleanest long-term foundation; new sources are genuinely peers.

**Cons**
- **Large, risky refactor** touching the highest-value code (`JukeboxViewModel`,
  `VideoItem`, `PlexService`, settings, categories, settings UI).
- The interface must be **rich enough** to express both a "resolve-a-stream-URL"
  source (YouTube) and a "hierarchical browse + direct-URL" source (Plex) — these
  are genuinely different shapes (see Interface Design below).
- Higher up-front cost before any user-visible benefit.

**Recommendation:** **Option B, executed incrementally** — but only after the
interface is validated against *both* built-ins on paper first. The honest reason
Option A "feels hacky" is that it is: it ships the feature but leaves the core
permanently forked. However, do **not** attempt B as a big-bang rewrite. Stage it
(see Migration Strategy) so each step is shippable and reversible.

---

## 🔌 Interface design sketch

The hardest part is that YouTube and Plex have **different interaction shapes**:

| Capability | YouTube | Plex |
|-----------|---------|------|
| Free-text search | ✅ | ✅ (per library) |
| Hierarchical browse | Playlists / channels (flat-ish) | Artist → Album → Track, Hubs, Playlists |
| Item → playable | Resolve short-lived stream URL (per play) | Direct `StreamUrl` (long-lived-ish) |
| Metadata / chapters | Parsed from description or native | Native via REST |
| Auth / config | None (or cookies) | Server URL + token |
| Audio-only | ✅ | ✅ (music) |

A single fat interface would be awkward. Prefer a **core contract + capability
interfaces** (composition over one big interface):

```csharp
// A plug-in *type* (the DLL) — the factory for source instances.
public interface IPhosphorSourceProvider
{
	string TypeId { get; }          // stable type id, e.g. "youtube", "plex", "jellyfin"
	string DisplayName { get; }
	Version ApiVersion { get; }     // contract version this plug-in was built against
	bool SupportsMultipleInstances { get; }  // e.g. Plex = true (two servers), YouTube = false

	// Config schema advertised to the settings UI (see below).
	IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema();

	// Create a configured, independent instance (one per configured account/server).
	IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings);
}

// A configured *instance* of a source — the thing the core actually talks to.
// ("Plex @ home" and "Plex @ friend" are two instances of the same provider.)
public interface IPhosphorSource
{
	string InstanceId { get; }      // unique per configured instance, not per type
	string TypeId { get; }          // which provider created it
	string DisplayName { get; set; } // user-editable label, e.g. "Home Plex"
	bool IsConfigured { get; }
	bool IsEnabled { get; set; }

	Task InitializeAsync(IPluginHost host, CancellationToken ct);
	void ApplySettings(IReadOnlyDictionary<string, string?> values);
}

// Optional capabilities — a source implements only what it supports.
public interface ITextSearchCapable
{
	IAsyncEnumerable<SourceItem> SearchAsync(string query, CancellationToken ct);
}

public interface IBrowsable                 // hierarchical navigation (Plex)
{
	IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(CancellationToken ct);
	IAsyncEnumerable<SourceItem> BrowseAsync(SourceCategory node, CancellationToken ct);
}

public interface IPlayableResolver          // item -> playable stream(s)
{
	Task<ResolvedStream?> ResolveAsync(SourceItem item, PlaybackPreferences prefs, CancellationToken ct);
}

public interface IDownloadable              // for the disk cache / prefetch
{
	Task<SourceDownload?> DownloadAsync(SourceItem item, PlaybackPreferences prefs, string destDir, CancellationToken ct);
}
```

Supporting types replace the Plex-specific leakage on `VideoItem`:

```csharp
public sealed class SourceItem
{
	public required string SourceInstanceId { get; init; } // which configured instance owns this
	public required string ItemId { get; init; }     // opaque to the core
	public string Title { get; init; } = "";
	public string? ThumbnailUrl { get; init; }
	public bool IsAudioOnly { get; init; }
	public TimeSpan? Duration { get; init; }
	public object? SourceState { get; init; }        // plug-in-private payload (rating keys, hub keys, …)
}
```

**`IPluginHost`** is what the core hands to plug-ins so they never reference core
internals directly: logging (`DebugLog`), an `HttpClient` factory, cache dir paths,
a secrets/credential store, and UI-status callbacks.

### Why capability interfaces, not one interface
- YouTube implements `ITextSearchCapable + IPlayableResolver + IDownloadable`.
- Plex implements `ITextSearchCapable + IBrowsable + IPlayableResolver`
  (download optional).
- A future "local folder" source might implement only `IBrowsable + IPlayableResolver`.

This makes the contract honest about what each source can do and lets the UI
adapt (e.g. only show a search box for `ITextSearchCapable` sources).

---

## ⚙️ Settings & configuration (generic "Plug-ins" tab)

Replacing the hard-coded **Plex** settings tab with a generic **Plug-ins** tab is
both feasible and the right call. The core enumerates providers → lists each
configured instance → renders that instance's settings → offers enable/disable and
add/remove. No per-source UI code. The catch is that source config comes in **two
tiers**, and only the first fits a static form.

### Tier 1 — Declarative settings (app-rendered)
Simple static fields: server URL, token, "prefer stereo," enable/disable. The
plug-in *declares* a schema; the app renders a standard form and owns storage.

```csharp
public sealed record PluginSettingDescriptor(
	string Key,                 // e.g. "serverUrl", "token"
	string Label,               // shown in the UI
	PluginSettingType Type,     // Text, Secret, Bool, Number, Enum, …
	bool Secret = false,        // render masked + store via credential store (DPAPI)
	string? DefaultValue = null,
	string? HelpText = null);   // optional per-field validation could hang off this too
```

The provider returns `IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema()`
and the app builds the form from it. This is what makes a zero-custom-UI tab
possible.

### Tier 2 — Interactive config actions (plug-in-driven, app-rendered)
Plex's "browse the server and pick which libraries become tiles" is **not** a
static field — it requires calling the live server at config time, then letting the
user select from the results. Model these as **config actions** the plug-in exposes,
which return generically-renderable data (a selectable list). The plug-in supplies
the *data + logic*; the app supplies the *generic shell* (list, checkboxes, save).
This preserves the data-in/data-out boundary from the threading section — the app
calls in, gets data back, renders it; the plug-in never touches UI.

```csharp
public interface IConfigurable
{
	// e.g. [{ Id="browseLibraries", Label="Browse libraries…" }]
	IReadOnlyList<ConfigAction> GetConfigActions();

	// Invoked when the user clicks a config action; returns a pick-list the app renders.
	Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct);
}
// ConfigSelection = list of { Id, Label, IsSelected } + selection mode (single/multi).
// The user's picks are persisted back into this instance's settings blob.
```

### Serialization: app owns storage, plug-in owns shape
`AppSettings` is saved once on exit (repo guideline), so the app should own the
**storage** — one settings blob **per instance**, keyed by `InstanceId` — while the
plug-in owns the **shape**. Two contracts, pick per plug-in:

- **App-serialized (preferred default).** Plug-in exposes/consumes an
  `IReadOnlyDictionary<string, string?>` (or a small `JsonElement`); the app persists
  it verbatim. Simple, safe, and the plug-in never touches disk.
- **Self-serialized (escape hatch).** Plug-in returns an opaque JSON `string` the app
  stores as a blob and hands back on load. Only needed when the config is too rich for
  a flat dictionary (e.g. a nested library-mapping structure).

Secrets (Plex token, future API keys) route through the `IPluginHost` credential
store (ideally DPAPI-encrypted), **not** the plaintext settings blob.

### What the tab looks like
```
Plug-ins
├─ YouTube                       [enabled ▾]   (single-instance)
│   └─ (declarative: quality, engine, stereo…)
├─ Plex — "Home"                 [enabled ▾]   [remove]
│   ├─ (declarative: server URL, token, stereo…)
│   └─ Browse libraries…         → pick tiles  (config action)
├─ Plex — "Friend's server"      [disabled ▾]  [remove]
│   └─ …
└─ [ + Add Plex ]  [ + Add source… ]           (multi-instance / discovered plug-ins)
```
This also gives "not configured" and "failed to load" states one consistent home.

---

## ⚠️ Risks & downsides

### Architectural / design
- **`VideoItem` is a shared god-object.** It's referenced across the ViewModel,
  caches, players, windows, and history. Introducing `SourceItem` (or slimming
  `VideoItem`) is the single biggest ripple. Mitigation: keep `VideoItem` as the
  UI/player model, introduce `SourceItem` at the plug-in boundary, and map between
  them at one adapter layer.
- **Interface adequacy risk.** If the contract is designed only around YouTube +
  Plex, the third source will still need core changes. Mitigation: dogfood with both
  built-ins (Option B) and prototype at least one non-trivial third source (e.g.
  Jellyfin) on paper before freezing the API.
- **Category/navigation model** is currently Plex-shaped. A generic `SourceCategory`
  tree needs to subsume Plex hubs/playlists/libraries and YouTube playlists/channels.

### Plug-in loading (assembly scanning)
- **Security / trust.** Loading arbitrary DLLs from a `plug-ins` folder runs
  **untrusted third-party code with full process privileges** (network, disk, the
  user's Plex token, etc.). This is the biggest downside of the drop-a-DLL model.
  There is no cheap sandbox for in-process .NET plug-ins. Mitigations: document the
  trust boundary loudly, optionally require a manifest, consider signing/allow-list,
  and (later) an out-of-process plug-in host for isolation.
- **`AssemblyLoadContext` & dependency hell.** Plug-ins bringing their own copies of
  `System.Text.Json`, `Newtonsoft`, etc. can clash with the host. Use a collectible
  `AssemblyLoadContext` per plug-in with careful shared-vs-private assembly rules,
  and ship the **contract in a separate lightweight assembly** (`Phosphor.Plugin.Abstractions`)
  that plug-ins reference but do **not** bundle.
- **Version skew.** A plug-in built against an older `ApiVersion` must be rejected
  gracefully, not crash the host. Hence `ApiVersion` on the contract.
- **Failure isolation.** A plug-in that throws on init, hangs a network call, or
  leaks memory must not take down Phosphor. Wrap all plug-in calls in
  try/catch + timeouts; disable a misbehaving plug-in and surface a UI notice.
- **WPF threading (host-owned, plug-ins are blind to it).** The plug-in boundary is
  deliberately a **pure data-in / data-out call**, modelled on how the app already
  drives `yt-dlp`: the **host calls into** the plug-in (search / browse / resolve /
  download) and gets back plain data (`SourceItem`, `ResolvedStream`, file paths).
  The plug-in has **no dispatcher, no UI reference, and never calls back into the
  app**. This collapses the "document threading rules for authors" burden to a
  single rule: *"your methods may run on a background thread; you have no UI to
  touch."* The host owns **all** marshalling onto the `PlayfieldWindow` /
  `BackglassWindow` threads after each call returns. The only outbound flow a plug-in
  needs is **progress/status** (e.g. "downloading 40%"), provided as an
  `IProgress<T>` the host hands in — and the host, not the plug-in, marshals those
  reports onto the UI thread. Bonus: because the boundary is pure data, it
  serializes cleanly, so a future **out-of-process plug-in host** (real isolation
  for untrusted DLLs — see Security below) is a natural extension rather than a
  rewrite.

### Product / operational
- **Settings model change.** Flat per-source fields (`PlexServerUrl`, etc.) should
  become a per-**instance** settings blob keyed by `InstanceId`, with the app owning
  storage and the plug-in owning shape (see **Settings & configuration** above for
  the two-tier declarative/interactive design). `AppSettings` is saved once on exit
  (repo guideline) — the plug-in settings serialization must respect that (no writes
  in setters).
- **Secrets.** Plex tokens (and future API keys) live in settings today. A plug-in
  credential store should ideally be encrypted (DPAPI) rather than plaintext JSON.
- **Testing surface explodes.** Each capability × each source is a test matrix.
- **Discovery/enumeration UX.** Enable/disable, "not configured," and "plug-in
  failed to load" states all need UI.

---

## ✅ Benefits

- **Extensibility** — the headline feature: add sources without recompiling the core.
- **Clean core** — `JukeboxViewModel` stops special-casing sources; `VideoItem`
  sheds source-specific fields.
- **Community contributions** — third parties can add Jellyfin/Emby/Subsonic/etc.
- **Proven contract** — dogfooding YouTube + Plex validates the API before anyone
  depends on it.
- **Feature-flagging** — enable/disable sources per install (e.g. a cabinet with no
  Plex server just disables it).
- **Isolation of breakage** — YouTube extraction breakage stays contained in the
  YouTube plug-in; other sources keep working.

---

## 🪜 Suggested migration strategy (incremental Option B)

Each phase is independently shippable and reversible.

1. **Create `Phosphor.Plugin.Abstractions`** — ✅ **DONE.** A small, dependency-free
   `net8.0` class library holding the contract: `IPhosphorSourceProvider` /
   `IPhosphorSource`, capability interfaces (`ITextSearchCapable`, `IBrowsable`,
   `IPlayableResolver`, `IDownloadable`, `IConfigurable`), data types (`SourceItem`,
   `SourceCategory`/`BrowseResult`, `ResolvedStream`, `SourceDownload`,
   `SourceMetadata`, `PlaybackPreferences`), settings types
   (`PluginSettingDescriptor`, `ConfigAction`/`ConfigSelection`), `IPluginHost`, and
   `PluginApi.Current` for version checks. Referenced by the host; nothing implements
   it yet, so there is no behavior change.
2. **Adapter, no loader.** Wrap the *existing* YouTube engines behind an in-box
   `YouTubeSource : IPhosphorSource` (statically referenced, not scanned). Route the
   VM's YouTube calls through it. Prove the contract fits YouTube. — ✅ **DONE**
   (build-only, not yet wired into the VM; that routing is Phase 4). Lives in
   `Phosphor/Plugins/YouTube/`: `YouTubeSourceProvider` (single-instance, exposes the
   YoutubeExplode-vs-yt-dlp engine choice + quality/stereo as declarative settings),
   `YouTubeSource` (implements `ITextSearchCapable` + `IPlayableResolver` +
   `IDownloadable` by composing the existing `SearchEngineFactory` / `VideoEngineFactory`
   — engine selection stays an internal detail with the same availability fallback), and
   `YouTubeMappings` (pure `VideoItem`/`VideoStreams`/`VideoDownload`/`VideoMetadata` ↔
   abstraction adapters). Also added `IPluginHost.GetToolPath(...)` so a source can locate
   host-bundled native tools (yt-dlp/ffmpeg) without hard-coded paths.
3. **Wrap Plex** behind an in-box `PlexSource : IPhosphorSource` implementing
   `IBrowsable`. This is the hard one — it forces the `SourceCategory`/`SourceItem`
   model to be adequate and untangles `PlexService` from the VM. — ✅ **DONE**
   (build-only, not wired into the VM; that's Phase 4). Lives in
   `Phosphor/Plugins/Plex/`: `PlexSourceProvider` (**multi-instance** — supports two
   Plex servers — with server/token/stereo declarative settings + a `libraries` blob),
   `PlexSource` (implements `ITextSearchCapable` + `IBrowsable` + `IPlayableResolver` +
   `IConfigurable`), `PlexNode` (internal browse-tree descriptor carried in
   `SourceCategory.SourceState`), and `PlexMappings` (item/category/stream/metadata
   adapters). **Model validation result:** the generic `SourceCategory` /
   `BrowseResult` shape absorbed Plex's full hierarchy (library → artists → albums →
   tracks, plus Hubs and Playlists grouping nodes) **without requiring any change to
   the abstractions** — the provider/instance split, `IConfigurable` "browse
   libraries" action, and `SourceState` payloads all held up. Playback reuses the
   ready-to-play `StreamUrl` already built into each Plex `VideoItem` (mapped to
   `ResolvedStream(Http, …)`), so no transcode logic moved.
4. **Introduce a `SourceRegistry`** in the VM replacing the direct `_plex` field and
   engine factories. All dispatch goes through the registry; delete `if (IsPlex)`
   branches. — 🟡 **IN PROGRESS (scoped down).** A codebase survey found **~102**
   source-coupling sites in `JukeboxViewModel` (32 direct `_plex.` calls, 13 `IsPlex`,
   13 `_searchEngine`, 9 `_video`, plus gapless/cache paths reading `VideoItem`), so a
   single big-bang rip-out was rejected as too risky. **Phase 4a (done):** stood up the
   runtime infrastructure — `Phosphor/Plugins/Host/PluginHost.cs` (implements
   `IPluginHost` over `DebugLog`, a shared `HttpClient`, app-relative tool/cache paths,
   in-memory secrets) and `Phosphor/Plugins/SourceRegistry.cs` (builds + initializes the
   YouTube and Plex sources from `AppSettings`). Wired into `App.xaml.cs` and adopted in
   **one narrow path** — free-text search now routes through the registry's YouTube
   `ITextSearchCapable` — gated by a new `AppSettings.UsePluginSources` flag that
   **defaults off**, so default behavior is byte-identical. The registry runs *alongside*
   the legacy engines. **Phase 4b (done):** routed the two remaining free-text search call
   sites (AutoDJ genre-fill and video-fill) through the same guarded helper, so **all**
   free-text video search is now flag-adopted — search is the first fully-migrated
   capability. **Phase 4c+ (todo):** incrementally migrate the remaining paths
   (Plex browse/playback, video resolve/download, playlist/channel discovery) onto the
   registry and retire the legacy `if (IsPlex)` branches a few at a time.
5. **Per-plug-in settings bag.** Migrate flat Plex/engine fields in `AppSettings`
   into a keyed settings dictionary, with a one-time migration from old fields.
6. **Dynamic loader (opt-in).** Add the `plug-ins` folder scan using a collectible
   `AssemblyLoadContext`, `ApiVersion` checks, failure isolation, and a manifest.
   Gate behind a setting initially.
7. **Generic settings UI.** Replace the hard-coded Plex tab with a generic
   **Plug-ins** tab that renders each instance's declarative schema
   (`GetSettingsSchema()`) plus any interactive config actions (`IConfigurable`, e.g.
   Plex "browse libraries"), with add/remove for multi-instance sources. See
   **Settings & configuration** above.
8. **Reference third source (validation).** Prototype Jellyfin or a local-folder
   source to confirm no core changes are needed.

Stop after any phase and still have a working app. Phases 1–4 deliver most of the
"clean core" value even if the dynamic loader (6) is deferred.

---

## 🔎 Open questions

- **Multiple instances of one source: yes (requirement).** Some users have access to
  more than one Plex server, so the design must support two enabled instances of the
  same provider. This is why the contract splits `IPhosphorSourceProvider` (the DLL
  /type) from `IPhosphorSource` (a configured instance with its own `InstanceId`,
  user-editable `DisplayName`, and settings block). Providers advertise
  `SupportsMultipleInstances` so single-instance sources (YouTube) can opt out.
  Knock-on effects: settings keyed by `InstanceId`, categories/items tagged with
  `SourceInstanceId`, and the settings UI needs an "add another <source>" affordance.
- Should the dynamic loader ship at all, given the **untrusted-code** risk, or stay
  an in-box-only plug-in model with a documented "build from source" path?
- How much of the **caching/prefetch** pipeline (`VideoCache`, `PrefetchCache`,
  `StreamSelector`) should be plug-in-pluggable vs. core-owned? Recommend core-owned,
  with plug-ins only providing raw streams/downloads.
- **Gapless playback (`PlexGaplessPlayback`) — leaning per-source capability, undecided.**
  Default proposal: model it as an optional capability flag a source advertises
  (e.g. `IGaplessCapable` or a `bool SupportsGapless`), rather than promising the
  core can force gapless on any source. Rationale: gapless depends on the source
  producing back-to-back, pre-buffered streams (Plex music tracks do; a YouTube
  resolve-per-play does not), so it is genuinely a per-source property. Open point:
  if enough sources support it, a shared core gapless pipeline might be worth
  generalizing later — revisit once a second gapless-capable source exists.
- **Stream transport is open-ended (decided).** A resolved stream is not always an
  HTTP URL. YouTube and Plex return short-lived HTTP(S) URLs today, but a
  local-folder source resolves to a **file path**, and other transports may appear
  later. `ResolvedStream` therefore carries a `StreamTransport` discriminator
  (`Http` / `File` / `Other`) so the host knows how to hand the URI to the media
  engine and cache pipeline, plus optional per-stream `HttpHeaders` for the HTTP
  case. The contract stays agnostic to *where* the media lives.
- Minimum supported plug-in author toolchain / target framework (host is .NET 8)?

> **Footnote — visualizations as plug-ins (out of scope for now).** The same
> provider/instance + capability pattern could eventually apply to the audio-reactive
> **visualizations** (`IBlobPattern` and friends), letting third parties drop in new
> patterns. It is deliberately *not* part of this effort — sources are the priority
> and visuals already have their own `IBlobPattern` seam and pattern factory. Worth
> revisiting only after the source plug-in model is proven.

---

## 🧭 Bottom line

- The instinct is right: **Option A (built-ins + bolt-on interface) is the hacky
  one** and leaves the core permanently forked.
- **Option B (everything-is-a-plug-in) is the correct architecture**, but its value
  and risk are dominated by **decoupling Plex from `JukeboxViewModel`/`VideoItem`**,
  not by defining the interface.
- Do it **incrementally** (phases above). The dynamic DLL-scanning loader is the
  flashiest part but also the **riskiest** (untrusted code, `AssemblyLoadContext`
  dependency management) — treat it as the last, optional phase, and land the clean
  in-box plug-in model first.
