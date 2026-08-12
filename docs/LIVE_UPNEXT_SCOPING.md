# Up-Next / Coming-Up — Feature Scoping

Status: **scoping only — no code yet.** This captures the agreed design so it can be implemented in a
future session without re-deriving decisions. It targets a new abstractions contract rev (**0.16.0**)
plus opt-in plug-in implementations.

## Goal

Let a live source report **what's coming up next** on a channel (e.g. the next SiriusXM track, or the
next HDHomeRun EPG program), and — reserved for a future discovery/search feature — an optional
**forward list** of upcoming items. Surfacing it in the UI is a separate, optional host feature
(e.g. a `Next: Artist · Title` line under the now-playing bar behind a "Show up next" setting); the
contract work here does not require the UI to ship at the same time.

## Why a new capability (not more fields on `LiveNowPlaying`)

"Up next" is meaningful only for some live sources (SiriusXM edge `liveUpdate` schedule, HDHomeRun
EPG) and meaningless for on-demand sources (Jellyfin/Plex/local). That asymmetry fits the codebase's
existing **opt-in capability-interface** pattern (`IFavoritable`, `IHideable`,
`ILiveNowPlayingProvider`) — the host feature-detects with `is ILiveUpNextProvider`. Bolting optional
"next" fields onto `LiveNowPlaying` would force every now-playing consumer to carry them even when
they're never populated, and the track-shaped record fits a TV *program* poorly.

## Decisions (locked)

1. **Split interfaces**, not one interface with a default-method list. `ILiveUpcomingProvider` inherits
   `ILiveUpNextProvider` (anything that can list "upcoming" can trivially return the single "next").
   The host detects the richer capability with `is ILiveUpcomingProvider`.
2. **Include `EndsUtc`** on the record — a "coming up" list needs slot boundaries to be useful, and
   start+end is free for both SiriusXM (cut duration) and HDHomeRun (EPG stop time).
3. **Both `lookahead` and `maxItems`** parameters on the list method — lets a future discovery UI bound
   the query (SiriusXM schedule ~30–45 min; HDHomeRun EPG can be hours).
4. **Define both interfaces now in 0.16.0**, but **implement only `ILiveUpNextProvider`** (SiriusXM
   first). `ILiveUpcomingProvider` is reserved: defined so the signature is stable for future
   implementers, unimplemented until the discovery feature is concrete.

## Proposed contract (abstractions 0.16.0 — additive)

Mirror the existing `ILiveNowPlayingProvider` style (`Capabilities.cs`) and `LiveNowPlaying` record
(`PlaybackPreferences.cs`). Pull-based polling; implementations cheap, must not throw, return null
when nothing is available.

```csharp
// Capabilities.cs — the immediate feature: the single item coming up next on a live channel.
public interface ILiveUpNextProvider
{
	/// <summary>
	/// Returns the item scheduled to air NEXT on <paramref name="itemId"/> (a SourceItem.ItemId),
	/// or null when unknown. Polled on a background loop while the live item plays; reuse any live
	/// session/auth and do not throw. <paramref name="playbackPosition"/> has the same meaning/anchor
	/// semantics as ILiveNowPlayingProvider (live audio trails the broadcast edge).
	/// </summary>
	Task<LiveUpNext?> GetUpNextAsync(
		string itemId, TimeSpan? playbackPosition, CancellationToken ct = default);
}

// The future discovery capability: a forward window of upcoming items. Implement ONLY if the source
// can cheaply return a schedule. Inherits ILiveUpNextProvider (list.First() == "next").
public interface ILiveUpcomingProvider : ILiveUpNextProvider
{
	/// <summary>
	/// Returns up to <paramref name="maxItems"/> upcoming items within <paramref name="lookahead"/>
	/// (both optional bounds; null = source default), ordered soonest-first. Empty when unavailable.
	/// </summary>
	Task<IReadOnlyList<LiveUpNext>> GetUpcomingAsync(
		string itemId, TimeSpan? lookahead, int maxItems, CancellationToken ct = default);
}
```

```csharp
// PlaybackPreferences.cs — one record for both the single "next" and each list element.
/// <param name="Title">Song title OR program name.</param>
/// <param name="Subtitle">Artist (music) / episode or short description (TV). Null when unknown.</param>
/// <param name="Album">Album, when known. Null otherwise.</param>
/// <param name="StartsUtc">When the item begins (aligns with LiveNowPlaying.NextChangeUtc). Null if unknown.</param>
/// <param name="EndsUtc">When the item ends (slot boundary for a coming-up list). Null if unknown.</param>
public sealed record LiveUpNext(
	string? Title,
	string? Subtitle = null,
	string? Album = null,
	DateTimeOffset? StartsUtc = null,
	DateTimeOffset? EndsUtc = null)
{
	/// <summary>True when at least one displayable field is set.</summary>
	public bool HasAny => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Subtitle);
}
```

Add a `PluginApi.Current` bump to `0.16.0` with a changelog line:
`0.16.0 — added ILiveUpNextProvider + ILiveUpcomingProvider + LiveUpNext (live "up next" / reserved
"coming up" list). Additive — plug-ins built against 0.15 still load unchanged.`

## Implementation sketch (future, not now)

- **Abstractions (0.16.0):** add the two interfaces + record + version bump. Repack to
  `E:\phosphorapp\local-nuget`, clear the cached copy, bump both consuming repos' references.
- **SiriusXM plug-in (`ILiveUpNextProvider`):** cheap — `liveUpdate` already returns the forward
  schedule (`items[]`) we currently fetch for now-playing and discard. Add an `ExtractUpNext` beside
  `ExtractNowPlaying` in `SxmEdgeClient` that selects the next `SONG` cut whose `timestamp` is after
  the current cut's end (skip `isInterstitial`), anchored with the same `LiveAudioLagMs`. Map
  `name → Title`, `artistName → Subtitle`, `albumName → Album`, `timestamp → StartsUtc`,
  `timestamp+duration → EndsUtc`. `SiriusXmSource` implements the interface and reuses the edge client;
  the host can fetch now + next in one poll cycle (no extra network — same `liveUpdate` response could
  even serve both if we later fold them together).
- **HDHomeRun plug-in (`ILiveUpNextProvider`, optional/later):** "next program" from EPG, gated behind
  its existing "Fetch guide data" setting. Natural candidate for `ILiveUpcomingProvider` too (EPG is a
  list already).
- **Host:** feature-detect `is ILiveUpNextProvider`; optional "Show up next" setting; render a subtle
  `Next: <Subtitle> · <Title>` element. Reuse the existing now-playing poll loop (piggyback the fetch)
  rather than a second cadence. `ILiveUpcomingProvider` powers a future browse/search/discovery view.

## Open items deferred to implementation time

- Whether the host should fold now-playing + up-next into a **single combined poll result** to avoid
  two calls for sources that support both (SiriusXM could return both from one `liveUpdate`). Leaning
  yes eventually, but the split interfaces keep them independently optional for now.
- Whether `ILiveNowPlaying` and `ILiveUpNext` should share a common base record — decided **no** for
  now (keep contract surface small; `LiveNowPlaying` and `LiveUpNext` stay separate, near-identical
  records).
- Discovery UI specifics (paging/filtering) — reserved; the `lookahead`/`maxItems` bounds are the
  minimum stable surface, richer query params can be an additive later rev if needed.
