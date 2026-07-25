# Phosphor Source Plug-in Authoring Guide

This guide explains how to build a **source plug-in** for Phosphor — a component that adds a new
music/video source (a media server, a local library, a streaming service, …) that shows up in the
app alongside the built-in **YouTube** and **Plex** sources.

The complete, working reference is **`Phosphor.Plugins.LocalFolder`** in this repository. It plays
media files from user-configured folders and implements most of the optional capabilities. Read it
alongside this guide — every pattern here is demonstrated there.

---

## 1. The mental model

- **Provider vs. instance.** A plug-in DLL exports one or more **providers**
  (`IPhosphorSourceProvider`) — the *type* and factory. The host creates **instances**
  (`IPhosphorSource`) from a provider. "Plex @ home" and "Plex @ work" are two instances of the same
  provider. Single-source plug-ins (like YouTube) advertise `SupportsMultipleInstances = false`.
- **A source is a pure data producer.** It answers the host's calls and returns plain data. It must
  **never** touch the UI, assume a specific thread, or reference host internals. Its *only* dependency
  is the `Phosphor.Plugin.Abstractions` contract assembly.
- **Capabilities are opt-in interfaces.** `IPhosphorSource` itself is tiny. Everything a source can
  *do* — search, browse, resolve playback, download, rescan, etc. — is expressed by *additionally*
  implementing the optional capability interfaces. The host inspects which ones you implement and
  lights up exactly those features (buttons, tiles, search, …). Implement only what your source
  supports.

---

## 2. Project setup

Create a **.NET 8 class library** that references **only** the contract, compile-only:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<TargetFramework>net8.0</TargetFramework>   <!-- NOT net8.0-windows: no UI, no WPF -->
	<Nullable>enable</Nullable>
	<ImplicitUsings>enable</ImplicitUsings>
	<EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
	<!-- Reference the contract COMPILE-ONLY. The host ships the single shared runtime copy, so
		 you must NOT bundle your own — otherwise contract types won't unify across the load
		 boundary and casts to IPhosphorSourceProvider will fail. -->
	<PackageReference Include="Phosphor.Plugin.Abstractions" Version="0.14.0">
	  <ExcludeAssets>runtime</ExcludeAssets>
	</PackageReference>
	<!-- (In-tree, you can use a ProjectReference with <Private>false</Private>/<ExcludeAssets>runtime</ExcludeAssets>
		 instead — see Phosphor.Plugins.LocalFolder.csproj.) -->
  </ItemGroup>
</Project>
```

**Rules that matter:**
- Target **`net8.0`** (plain, no `-windows`). Plug-ins produce data; they never use WPF/WinForms.
- The contract is **compile-only** (`ExcludeAssets=runtime`). The host owns the one shared copy.
- Do **not** reference the `Phosphor` host project. If you need something from it, it belongs in the
  contract — file an issue.

### Deployment

The host scans a `plugins/` folder next to `Phosphor.exe`. Drop your build output at:

```
Phosphor/…/plugins/<YourPluginName>/<YourPluginName>.dll   (+ any private dependencies)
```

Each plug-in gets its own subfolder and its own collectible `AssemblyLoadContext`, so a bad DLL is
isolated (logged and skipped) and never crashes startup. (See `PluginLoader` in the host for the
loading mechanics.)

---

## 3. The provider — `IPhosphorSourceProvider`

The provider is discovered by reflection, so it needs a **public parameterless constructor**.

```csharp
public sealed class LocalFolderSourceProvider : IPhosphorSourceProvider
{
	public const string LocalFolderTypeId = "localfolder";     // stable, unique, never localized
	public const string KeyFolders   = "folders";
	public const string KeyRecursive = "recursive";

	public string  TypeId      => LocalFolderTypeId;
	public string  DisplayName => "Local Folders";
	public string? Description => "Plays audio and video files from folders on this machine…";

	public Version ApiVersion => PluginApi.Current;   // the contract version you built against
	public bool    SupportsMultipleInstances => true; // user can add several instances

	// Tier-1 declarative settings the host renders as a form (see §5).
	public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
	[
		new(KeyFolders, "Folders", PluginSettingType.FolderPath,
			HelpText: "Folders to scan for media.") { AllowMultiple = true },
		new(KeyRecursive, "Include subfolders", PluginSettingType.Bool, DefaultValue: "true"),
	];

	// Factory: the host owns the instanceId and the persisted settings dictionary.
	public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
		=> new LocalFolderSource(instanceId, settings);
}
```

**Version gating.** The host loads your plug-in only if `IsCompatible(ApiVersion)` holds — currently
`PluginApi.MinimumSupported <= your major.minor <= PluginApi.Current`. Setting
`ApiVersion => PluginApi.Current` keeps you in step with whatever contract you compiled against.

**Optional provider metadata:**

- **`AccountRequirement? Account`** — declare that your source needs an external account or
  subscription. The host renders an advisory badge and a "Requires … Sign up: `<url>`" line in the
  Plug-ins tab so users know upfront that setup involves signing up (and whether it costs money). Set
  `IsPaid: true` for paid services. This is *distinct* from a `Secret` field: `Secret` only says "a
  credential is stored," while `Account` says "you must go create one." SiriusXM declares
  `new("an active SiriusXM streaming subscription", "https://www.siriusxm.com/plans", IsPaid: true)`.
- **`IReadOnlyList<string> RequiredTools`** — logical names of host-bundled native tools you shell out
  to at runtime (e.g. `"yt-dlp"`, `"ffmpeg"`), resolved via `IPluginHost.GetToolPath`. Declaring them
  is for load-time validation only: the host warns when a declared tool is missing (a clear startup
  diagnostic instead of a play-time failure). The host does not acquire tools from this list.

---

## 4. The instance — `IPhosphorSource`

The required surface is small:

```csharp
public string InstanceId { get; }                 // host-assigned, stable, unique
public string TypeId { get; }                      // your provider's TypeId
public string DisplayName { get; set; }            // user-editable label (names the tile)
public bool   IsConfigured { get; }                // are settings sufficient to operate?
public bool   IsEnabled { get; set; }              // user toggle; disabled = ignored by host

Task InitializeAsync(IPluginHost host, CancellationToken ct = default);  // one-time async init
void ApplySettings(IReadOnlyDictionary<string, string?> values);         // called on settings edit
```

**Patterns to copy from the reference:**
- Constructor calls a private `ApplySettingsInternal(settings)`; `ApplySettings` calls the same. Keep
  parsing in one place.
- `InitializeAsync` receives the `IPluginHost` — stash it. It's your only channel back to the host
  (logging, a shared `HttpClient`, a private cache directory, secret storage, status reports). See §7.
- `IsConfigured` should reflect *real* readiness (e.g. "at least one folder exists"), because the host
  uses it to decide whether to build the source and show its UI.
- When settings change, **invalidate any cached state** so the next call reflects the new config
  (the reference clears its catalog).

**Lifecycle / teardown.** If your source holds anything disposable — a connection, a
`FileSystemWatcher`, a timer — implement `IDisposable` or `IAsyncDisposable`. The host disposes
sources when it rebuilds the registry (on every settings save) and at shutdown. This prevents leaks
across the frequent rebuilds. Startup is `InitializeAsync` (async, host-injected); there is no
separate "startup" interface.

---

## 5. Declarative settings (Tier-1) — `PluginSettingDescriptor`

Your `GetSettingsSchema()` returns field descriptors; the host renders a standard form and persists
values into a flat `Dictionary<string, string?>` keyed by your `Key`s. You own the meaning and
parsing of those strings.

| `PluginSettingType` | Rendered as | Notes |
|---|---|---|
| `Text`      | text box | |
| `Secret`    | password box (masked) | for tokens/keys/passwords; stored via the host credential store, not the plain blob (optional at-rest DPAPI encryption, off by default) |
| `Bool`      | checkbox | parse with `bool.TryParse` |
| `Number`    | text box | parse yourself |
| `Enum`      | combo box | set `EnumValues` (names match your parse) |
| `FolderPath`| text box + **Browse…** folder picker | |

Plus one orthogonal flag:

- **`AllowMultiple = true`** → the host renders an **add/remove list editor** (a folder picker per row
  for `FolderPath`) and stores the rows as **newline-joined text in the single key**. Your code just
  splits on newlines. This composes with any type: one folder vs. N folders, one URL vs. N URLs, etc.
  Storage stays flat — there is no array type.

Example (from the reference): `Folders` is `FolderPath { AllowMultiple = true }`, parsed as:

```csharp
_folders = (Get(values, KeyFolders) ?? "")
	.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
	.ToList();
```

**Secrets:** mark tokens/passwords as `Secret`. The host stores these through its credential store,
not in the plain settings blob you get in `ApplySettings`. At-rest **DPAPI encryption is a
user-configurable, opt-in host option** — it's **off by default** (secrets are stored as plain text so
the settings file stays portable), and a user may turn it on to encrypt them (bound to the current
Windows user/machine). Either way your code is unaffected: read and write secrets through
`IPluginHost.GetSecret`/`SetSecret` (see §7) rather than treating them like ordinary values.

**Interactive config (Tier-2)** — if setup needs a *live* call (e.g. "browse the server for libraries
to add"), implement `IConfigurable` instead of/in addition to the schema. That's an advanced path;
Plex uses it. Most sources only need Tier-1.

---

## 6. Capabilities — implement what your source supports

Add these interfaces to your instance class as needed. The host shows a **"Supports:"** line listing
what you implement, and enables the matching features.

| Capability | Purpose | Host feature it unlocks |
|---|---|---|
| `ITextSearchCapable` | `SearchAsync(query)` → items | Search box routes to you |
| `IBrowsable` | root categories + `BrowseAsync(node)` | **Home-screen tiles** + drill-down |
| `IPagedBrowsable` | `BrowsePageAsync(node, offset, count)` | Lazy "load more" for huge lists |
| `IScopedSearchable` | `SearchInCategoryAsync(node, query)` | Search **within** the open browse node (fan-out allowed) |
| `IPlayableResolver` | `ResolveAsync(item)` → stream; `GetMetadataAsync` | **Playback** |
| `IDownloadable` | `DownloadAsync(item, …, destDir)` | Disk caching / prefetch |
| `IGaplessCapable` | `GetGaplessStreamUrl(item)` | Gapless audio transitions |
| `IUpdatable` | `SupportsUpdate`, `GetVersionAsync`, `UpdateAsync` | "Update engine" button |
| `IConnectionTestable` | `TestConnectionAsync()` | "Test connection" button (✓/✗ + latency) |
| `IRefreshable` | `CanRefresh`, `RefreshAsync(progress)` | "Rescan library" button (+ progress bar) |
| `IPlaylistChannelDiscovery` | resolve/enumerate playlists & channels | YouTube-style playlist/channel browse |
| `IConfigurable` | interactive setup actions | Tier-2 config actions (e.g. Plex libraries) |
| `IFavoritable` | `IsFavorite`/`SetFavorite`/`GetFavoriteIds` | Per-row **star** toggle (only shown for your source) + a "Favorites" view you surface in browse |
| `IHideable` | `GetHideableItems`/`GetHiddenIds`/bulk `SetHidden` | "Manage hidden channels…" button → themed dual-list + group tree; you persist the hidden set and filter your own browse |
| `IExperimental` | *(marker on the **provider**)* | "⚠ EXPERIMENTAL" badge in the Plug-ins settings tab (advisory only) |
| `IPlaybackReportable` | `ReportPlaybackFailure(id, kind)` → `bool` | Host tells you a play attempt failed; you decide if the item is now unplayable |

### Data-flow types you'll use

- **`SourceItem`** — a playable/browsable row. Set `SourceInstanceId`, `ItemId`, `Title`, and flags
  like `IsAudioOnly`/`IsContainer`. Crucially, stash whatever *you* need to resolve/browse it later in
  the opaque **`SourceState`** — the host stores it and hands it back to you untouched, so you never
  re-derive ids. (The reference stashes the file path.)
- **`SourceCategory`** — a browse-tree node (a tile). Same `SourceState` trick for the node's identity.
  Set the optional **`Icon`** (a glyph/emoji, e.g. `"🎵"`) to theme the tile; the host falls back to a
  default folder glyph when it's null/empty.
- **`BrowseResult`** — `{ Categories, Items }`. Return sub-categories, leaf items, or both.
- **`ResolvedStream`** — how to play an item: a `StreamTransport` (`Http`, `File`, `Other`), a
  `StreamLayout` (`Muxed`, `SeparateVideoAudio`, `AudioOnly`), and the URI(s). Local files return
  `StreamTransport.File` with the path — the host plays it directly.
- **Live/infinite streams** — set **`IsLiveStream`** on the `SourceItem` (and/or the `ResolvedStream`)
  for continuous radio-style streams with no fixed duration. The host then shows elapsed time as
  `M:SS / *`, hides the scrub bar (a "● LIVE" badge instead), disables seek, and never auto-advances
  the queue. (SiriusXM channels use this.)
- **Live thumbnail badge** — set **`SourceItem.ShowLiveBadge = true`** to have the host draw a small red
  "live now" dot on the item's thumbnail. This is a *display hint only* and deliberately separate from
  `IsLiveStream`: use it to call out a single currently-broadcasting feed shown among finite items
  (e.g. a Twitch channel's live stream sitting atop its VODs). Sources whose items are **all** live
  (e.g. SiriusXM) should leave it `false` so they don't badge every row. A source may gate it behind
  its own setting.
- **Surface-but-unplayable items** — set **`SourceItem.IsPlayable = false`** (default `true`) to show a
  row you know can't be played (e.g. a DRM-locked track). The host renders it with action buttons
  removed and a 🚫 indicator instead of hiding it. Pair with **`IPlaybackReportable`**: when a play
  attempt fails, the host calls `ReportPlaybackFailure(id, kind)` — persist only
  `PlaybackFailureKind.Unresolvable` (definitive) failures as unplayable, ignore `Transient` ones
  (network/timeout), and return `true` when the item is now known-unplayable so the host flips the live
  row. (SoundCloud uses this for "lazy discovery" of DRM tracks.)

### Tiles: you decide the shape

The host builds **one tile per root category** you return from `GetRootCategoriesAsync`. This is a
deliberate boundary — *you* choose your browse shape:

- Return **one** root → a single tile for the whole instance (the reference does this: its folders are
  an implementation detail, like a Plex library's folders). Users who want separate tiles add another
  instance.
- Return **many** roots → a tile each (Plex returns one per library).

Selecting a tile calls your `BrowseAsync` with that category; return items (and/or sub-categories) to
drill in. For playback, set each item's stream so the host can play it — for `File`-transport sources
the reference resolves the path in `BrowseAsync`; networked sources resolve lazily in `ResolveAsync`.

---

## 7. The host services — `IPluginHost`

`InitializeAsync` hands you an `IPluginHost`. It's your **only** callback channel; the host owns all
threading behind it.

```csharp
void   Log(string message);                     // into the app diagnostics log
void   Log(LogLevel level, string message);     // same, at an explicit verbosity level
HttpClient HttpClient { get; }                  // shared, pooled — do NOT dispose it
string InstanceCacheDirectory { get; }          // a per-instance folder you may write to
string? GetSecret(string key);                  // credential store (optional DPAPI encryption at rest; off by default)
void   SetSecret(string key, string? value);
string? GetToolPath(string toolName);           // bundled tools, e.g. "yt-dlp", "ffmpeg"
void   ReportStatus(string message);            // user-facing status (host marshals to UI thread)
```

Use `HttpClient` for network calls (respecting the host's configured timeout); use
`InstanceCacheDirectory` for any index/catalog you persist.

---

## 8. Threading & safety rules

- **No UI, no WPF, no dispatcher.** Return data; the host renders it.
- **Assume no thread.** Your methods may be called from a background thread or the UI thread. Don't
  touch thread-affine state. If you keep mutable caches, guard them (the reference uses a lock).
- **Honor `CancellationToken`.** Search/browse/refresh can be cancelled; check the token in loops.
- **Don't throw for expected failures.** `TestConnectionAsync`/`RefreshAsync` should return a *failed
  result* (with a message) for unreachable hosts or bad credentials, not throw.
- **Async all the way** for I/O; the reference wraps its folder walk in `Task.Run` and reports
  `IProgress<RefreshProgress>` so the UI can show a bar.

---

## 9. Checklist

- [ ] .NET 8 class library, contract referenced **compile-only**, no host reference.
- [ ] `IPhosphorSourceProvider` with a **public parameterless ctor**, stable `TypeId`,
	  `ApiVersion => PluginApi.Current`, and a settings schema.
- [ ] `IPhosphorSource` with `InitializeAsync`/`ApplySettings`, honest `IsConfigured`.
- [ ] Only the capabilities you actually support.
- [ ] Opaque `SourceState` carries per-item/-node identity.
- [ ] `IDisposable`/`IAsyncDisposable` if you hold resources.
- [ ] No UI, thread-safe, cancellation-aware, no-throw on expected failures.
- [ ] Deployed to `plugins/<Name>/<Name>.dll`; **no** bundled `Phosphor.Plugin.Abstractions.dll`.

---

## 10. Where to look in the repo

- **`Phosphor.Plugin.Abstractions/`** — the contract. `IPhosphorSource.cs`,
  `IPhosphorSourceProvider.cs`, `Capabilities.cs`, `PluginSettingDescriptor.cs`, `SourceItem.cs`,
  `SourceCategory.cs`, `ResolvedStream.cs`, `IPluginHost.cs`, `PluginApi.cs`.
- **`Phosphor.Plugins.LocalFolder/`** — the full reference plug-in (this guide's running example).
- **`Phosphor/Plugins/Loader/PluginLoader.cs`** — how the host discovers, version-gates, and isolates
  plug-ins (for understanding, not something you call).
