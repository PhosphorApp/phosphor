# SiriusXM Now-Playing — Implementation Notes & Future Work

Status: **plumbing complete, label HIDDEN** pending a fresher metadata source.

This document captures everything needed to finish the SiriusXM "now-playing track" feature
(showing the current song/artist on the now-playing bar) in a future session, without re-deriving
what we already learned.

## Goal

Show the currently-airing track for a live SiriusXM channel, e.g.
`18 · The Beatles Channel · The Beatles · Here Comes The Sun`, using middle-dot (·) separators.

## Current state (what's shipped, behind a flag)

The full pipeline exists and builds, but the on-screen label and the background poller are both
disabled by feature flags because the metadata we can currently fetch trails the audio by ~1 minute
(confusing "wrong song" effect). Everything is intact for a clean re-enable.

Feature flags to flip ON when ready (keep them in sync):
- `Phosphor/Playback/PlayerContext.cs` → `ShowLiveTrackLabel` (appends the label to `NowPlayingTitle`).
- `Phosphor/JukeboxViewModel.cs` → `LiveNowPlayingEnabled` (starts the background poll loop).

Pipeline components (all present):
- **Abstractions (0.15.0):** `ILiveNowPlayingProvider.GetNowPlayingAsync(itemId, playbackPosition, ct)`
  returns `LiveNowPlaying(Title, Artist, Album, NextChangeUtc)`. See `Capabilities.cs` /
  `PlaybackPreferences.cs`.
- **Plugin:** `SxmClient.GetNowPlayingAsync(...)` + `ExtractNowPlaying(...)`, and
  `SiriusXmSource` implements `ILiveNowPlayingProvider`.
- **Host:** `VideoItem.LiveTrackText` (observable), a per-player poll loop in `JukeboxViewModel`
  (`StartLiveNowPlayingPoller` / `PollLiveNowPlayingAsync`), and the title composition in
  `PlayerContext.NowPlayingTitle`.
- **Proxy DVR offset (disabled):** `SxmProxy.SegmentDelayCount` can trim trailing segments to play
  behind live; paired with `SxmClient.LiveAudioLagMs`. Both currently `0`.

## Why the label is hidden — the root cause

Two different SXM endpoints expose track metadata, with very different freshness:

1. **What the plugin currently uses (STALE):**
   `GET https://player.siriusxm.com/rest/v2/experience/modules/tune/now-playing-live`
   (cookie-authenticated; same session as auth/lineup/stream resolution).
   Its newest `cut` marker **trails the broadcast live edge by ~90 s**. Measured repeatedly: while
   the audio played song N, the newest published cut was still song N-1. No amount of client-side
   anchoring fixes this — the data literally isn't published yet at the edge.

2. **What the web player uses (FRESH — the fix):**
   `GET https://api.edge-gateway.siriusxm.com/playback/play/v1/liveUpdate`
   (also see `.../playback/stream-enforcement/v1/status`).
   This returns the **programmed playout schedule ~30–45 min AHEAD** of the broadcast — the current
   song's cut already exists. Anchoring at (or just behind) the live edge lands on the correct song.
   This is why the web player is accurate.

### Anchoring math we validated (for whichever feed is used)

- Live edge: `liveChannelData.cuePointList.cuePoints[]` where `layer=="livepoint"` (epoch ms) — in
  the old feed. The listener's audio instant = `liveEdge - buffer`, or `TUNE_START + playbackPos`
  (note: `TUNE_START` from `customAudioInfos[].position` DRIFTS — it's refreshed per poll, not a
  fixed stream origin, so it's unreliable as an absolute anchor).
- Empirically the player buffer behind live was a stable ~35–50 s.
- Select the cut whose `[time, time+duration)` window contains the audio instant.

## The remaining work for a full fix (Path A)

Adopt the edge-gateway `liveUpdate` feed. The blocker is **auth**: edge-gateway almost certainly
uses **bearer-token (OAuth-style)** auth, NOT the cookie session `SxmClient` currently uses. Capture
from browser DevTools (Network tab, on the `liveUpdate` request) before starting:

1. Full **request URL** with query params (channel id? gupId? timestamps?).
2. **Request headers** — especially `Authorization: Bearer …` and any `x-*` gateway headers.
3. How the **bearer token is obtained/refreshed** — look for an earlier request to edge-gateway
   (login/token/oauth/session) and record its request + response shape.

### Response shape of `liveUpdate` (confirmed from a capture)

```
{
  "channelNumber": 18,
  "channelName": "The Beatles Channel",
  "episodes": [ { "name", "startTimestamp", "duration", "showName", ... } ],
  "items": [
	{
	  "type": "cut-linear",
	  "artistName": "The Beatles",
	  "albumName": "Abbey Road",
	  "name": "Here Comes The Sun",     // song title
	  "duration": 183000,               // ms
	  "timestamp": "2026-08-11T12:11:40.762Z",  // broadcast start (schedule)
	  "isInterstitial": false,          // true for station-ID/DJ chatter — skip
	  "cutFlags": ["NAVIGABLE","SONG"]  // "SONG" vs "INTERSTITIAL"
	}, ...
  ],
  "nextLiveUpdateStartTimestamp": "..."  // paging/refresh hint
}
```

Selection: filter `cutFlags` contains `"SONG"` (skip `isInterstitial==true` / `INTERSTITIAL`); the
current song is the item whose `[timestamp, timestamp+duration)` contains the listener's audio
instant (live edge minus buffer). `episodes[]` gives the show title for talk fallback.

## Does the 0.15.0 contract already cover this? — YES

The `ILiveNowPlayingProvider` contract is **source-agnostic and sufficient** for Path A. Switching to
the edge-gateway feed is entirely internal to the SiriusXM plugin (`SxmClient`): the host still just
calls `GetNowPlayingAsync(itemId, playbackPosition, ct)` and receives `LiveNowPlaying`. No host or
contract change is expected. The one thing Path A adds is a *new auth flow inside the plugin*, which
is invisible to the contract. `playbackPosition` is already threaded through in case the fresh feed
benefits from precise position anchoring; if unused it's harmless.

Caveat: if a future need arises to surface album art or a "next track", the `LiveNowPlaying` record
would need new optional fields — additive, so it wouldn't break existing plug-ins.

## Re-enable checklist (once the fresh feed + auth are in)

1. Implement edge-gateway auth + `liveUpdate` fetch/parse inside `SxmClient` (replace or augment
   `GetNowPlayingAsync`).
2. Confirm anchoring lands on the correct song at the live edge (diagnostics helper below).
3. Set `PlayerContext.ShowLiveTrackLabel = true` and `JukeboxViewModel.LiveNowPlayingEnabled = true`.
4. Leave `SxmProxy.SegmentDelayCount = 0` / `SxmClient.LiveAudioLagMs = 0` (no DVR offset needed with
   the fresh feed).
5. Rebuild plugin with `PHOSPHOR_HOST_DIR` set so it deploys into the host bin (see below).

## Dev/build notes (gotchas we hit)

- **Local abstractions feed:** the host and plugins consume `Phosphor.Plugin.Abstractions` as a NuGet
  package. The unreleased 0.15.0 was packed to `E:\phosphorapp\local-nuget` and wired via a
  `nuget.config` in both `phosphor` and `phosphor-plugins` repos. After changing the contract,
  repack and clear the cached copy:
  `Remove-Item ~/.nuget/packages/phosphor.plugin.abstractions/0.15.0 -Recurse; dotnet pack ... -o E:\phosphorapp\local-nuget`.
- **Plugin deploy:** the plugin self-deploys via `Directory.Build.targets` into
  `$PHOSPHOR_HOST_DIR/plugins/SiriusXM`. Set `PHOSPHOR_HOST_DIR` to the host bin
  (`E:\phosphorapp\phosphor\Phosphor\bin\Debug\net8.0-windows`) before `dotnet build`, or it deploys
  to a repo-local `dist/` instead and the running app won't see it.
- **Verifying a DLL contains a change:** .NET string literals are UTF-16 in the DLL — scan with
  `[System.Text.Encoding]::Unicode`, not UTF-8, or you'll get false "stale" readings.
- **Spike tool:** `tools/SiriusXmSpike` has a `--dump-nowplaying <channel>` mode that dumps the
  (old) now-playing-live JSON and scans for marker/cut fields — handy for the old endpoint.

## Diagnostics helper (currently in SxmClient)

`ExtractNowPlaying` logs a `SXM np:` debug line with `tuneStart / liveEdge / audioInstant /
edge-audio / songStart / audio-songStart => 'Artist - Title'`. Invaluable for tuning the anchor —
keep it (Debug level) while iterating, remove before final ship.
