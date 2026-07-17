# Phosphor.Plugin.Abstractions

The stable contract for authoring **Phosphor** source plug-ins. Phosphor is a WPF
music/video jukebox for virtual pinball cabinets; sources (YouTube, Plex, and
third-party providers) implement the interfaces in this package to add searchable /
browsable / playable media.

## Referencing (important)

Reference this package **compile-only**. The Phosphor host loads the single shared
copy at runtime — if your plug-in ships its own copy, the host will not recognize
your types (two assembly identities = two different `IPhosphorSource` types).

```xml
<PackageReference Include="Phosphor.Plugin.Abstractions" Version="0.1.0">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

## Authoring a source

1. Implement `IPhosphorSourceProvider` (the plug-in type + factory). Report the
   `ApiVersion` you built against and whether you support multiple instances.
2. Have `CreateInstance(...)` return an `IPhosphorSource` (a configured instance).
3. Implement the capability interfaces your source supports — only what it can do:
   - `ITextSearchCapable` — free-text search
   - `IBrowsable` — hierarchical browse (libraries → artists → albums → tracks, …)
   - `IPlayableResolver` — resolve an item to a playable `ResolvedStream`
   - `IDownloadable` — download raw streams for the host's cache
   - `IConfigurable` — interactive setup actions (e.g. "browse libraries")
   - `IConnectionTestable` — a "test connection" button
   - `IRefreshable` — rescan content / rebuild your catalog
   - `IFavoritable` — let users star items (shows a per-row star + a "Favorites" view)
   - `IHideable` — let users hide items (a themed manage dialog; you persist + filter)

For continuous radio-style streams, set `IsLiveStream` on the `SourceItem`/`ResolvedStream` so the
host suppresses seek/duration and shows a live badge.

Your source is a **pure data producer**: the host calls in and gets plain data back.
Do not touch UI, assume a thread, or call back into the host except through the
`IPluginHost` services handed to `InitializeAsync`.

## Versioning

`PluginApi.Current` is the contract version. The package version tracks it. The host
rejects plug-ins built against an incompatible `ApiVersion` rather than crashing.

See the project repository for the full architecture notes.
