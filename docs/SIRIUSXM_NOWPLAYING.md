# SiriusXM Now-Playing — Implementation Notes & Future Work

Status: **label ENABLED for verification** against the experimental `SiriusXM-Exp` plugin (fresh
edge-gateway `liveUpdate` feed). The original cookie-based `SiriusXM` plugin remains untouched as the
A/B baseline and fallback.

This document captures everything needed to finish the SiriusXM "now-playing track" feature
(showing the current song/artist on the now-playing bar) in a future session, without re-deriving
what we already learned.

## Goal

Show the currently-airing track for a live SiriusXM channel, e.g.
`18 · The Beatles Channel · The Beatles · Here Comes The Sun`, using middle-dot (·) separators.

## Current state

The full pipeline exists, builds, and is now **ENABLED** to verify the experimental `SiriusXM-Exp`
plugin. The feature gates were originally OFF because the *old* metadata endpoint trailed the audio
by ~1 minute (confusing "wrong song" effect); the new plugin sources from the ahead-of-broadcast
edge-gateway `liveUpdate` feed, so the label should now track the audio correctly.

Both gates are **source-agnostic** — they light up for any live source implementing
`ILiveNowPlayingProvider`, so `SiriusXM-Exp` drives them with no other host change. Keep them in sync:
- `Phosphor/Playback/PlayerContext.cs` → `ShowLiveTrackLabel = true` (appends the label to `NowPlayingTitle`).
- `Phosphor/JukeboxViewModel.cs` → `LiveNowPlayingEnabled = true` (starts the background poll loop).

To revert to the hidden state (e.g. if verification stalls), set both back to `false`.

### A/B testing note
Both plugins can be loaded at once. The label shows for whichever live source is actually playing, so
tune the **`SiriusXM-Exp`** source when validating the fresh feed; play the old `SiriusXM` source to
see the stale-by-~1min behavior for comparison.

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

### Why the web player has a rewind/DVR — same mechanism

This also explains the web UI's ability to scrub back a long way on a "live" channel. It's not a
separate DVR feature bolted on: the `liveUpdate` schedule already spans a wide window (well behind and
~30–45 min ahead of the live edge), and the HLS master playlist exposes a deep buffer of past
segments. So the client already *has* both the audio segments and the exact cut/episode metadata for
everything in that window — rewinding is just picking an earlier point on a timeline it already holds,
with correct now-playing metadata following along because every cut is timestamped. Our accurate
now-playing and SXM's "rewind" are two views of the same ahead-of-broadcast, buffered timeline.

### Anchoring math we validated (for whichever feed is used)

- Live edge: `liveChannelData.cuePointList.cuePoints[]` where `layer=="livepoint"` (epoch ms) — in
  the old feed. The listener's audio instant = `liveEdge - buffer`, or `TUNE_START + playbackPos`
  (note: `TUNE_START` from `customAudioInfos[].position` DRIFTS — it's refreshed per poll, not a
  fixed stream origin, so it's unreliable as an absolute anchor).
- Empirically the player buffer behind live was a stable ~35–50 s.
- Select the cut whose `[time, time+duration)` window contains the audio instant.

## The remaining work for a full fix (Path A)

Adopt the edge-gateway `liveUpdate` feed. The blocker is **auth**: edge-gateway uses **bearer-token
(JWT)** auth, NOT the cookie session `SxmClient` currently uses. This is no longer a mystery — the
full request URLs, headers, and token-mint flow are documented below under
"Reference implementation: `yob15662/sxm-player`", which reverse-engineered the entire gateway from a
web-player HAR capture. Confirmed answers to the original open questions:

1. Request: `POST https://api.edge-gateway.siriusxm.com/playback/play/v1/liveUpdate` with JSON body
   `{ channelId, startTimestamp, endTimestamp }` (no query params).
2. Headers: `Authorization: Bearer <jwt>`, `x-sxm-clock`, `x-sxm-platform: browser`, `x-sxm-tenant: sxm`.
3. Token mint/refresh: headless 4-step chain from stored username/password (device → anonymous →
   password identity grant → authenticated access token), with proactive expiry refresh. Details below.

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

## Reference implementation: `yob15662/sxm-player` (validates Path A end-to-end)

`https://github.com/yob15662/sxm-player` is a .NET SiriusXM proxy (Icecast/HLS re-streamer for
Linux/MPD) built **entirely on the new `api.edge-gateway.siriusxm.com` gateway** — the exact fresh
feed this doc identified as the fix. It is not directly reusable (server app, not a Phosphor plugin;
uses NSwag-generated clients from a captured HAR), but it is a complete, working answer to every open
question below. Key files: `SXMPlayer.Client/APISession.cs` (auth), `ClientExtensions.cs`
(bearer-header injection), `Services/MetadataService.cs` (`liveUpdate` fetch + cut selection),
`sxm_cleaned.yaml` (the reverse-engineered OpenAPI for the whole gateway).

### Is there a newer API? — YES, and it's what the web player uses

- Base host: `https://api.edge-gateway.siriusxm.com` (replaces `player.siriusxm.com/rest/v2|v4`).
- Now-playing: `POST /playback/play/v1/liveUpdate` with body
  `{ "channelId": "...", "startTimestamp": "<ISO8601>", "endTimestamp": "<ISO8601>" }`. Returns the
  ahead-of-broadcast schedule (`items[]` = cuts, `episodes[]` = shows) exactly as captured above.
- Also present: `/playback/play/v1/tuneSource` (stream URL resolution),
  `/playback/stream-enforcement/v1/status`, `/playback/key/v1/{id}` (HLS key), `/time/v1/metronome`.

### How is auth handled? — bearer-token (JWT) chain, fully transparent from stored user/pass

We store username/password today; that is **sufficient** — the login is a headless multi-step token
exchange (no browser, no interactive flow). The reference's `LoginIfNecessaryInternal` sequence:

1. `POST /device/v1/devices` (unauthenticated) with browser `deviceAttributes` → **device grant**.
   Cache to `device.json`. Refresh via `POST /device/v1/grant/refresh` (`{ refreshGrant, deviceAttributes }`).
2. `POST /session/v1/sessions/anonymous` → **anonymous access token** (+ `accessTokenExpiresAt`).
3. `POST /identity/v1/identities/authenticate/password` with `{ "handle": <username>, "password": <password> }`
   → an **identity grant** (the credential-verifying step). Cache to `tokens.json`.
4. `POST /session/v1/sessions/authenticated` → the final **user JWT access token**. Cache to `access.json`.

Every subsequent request carries these headers (see `ClientExtensions.PrepareRequest`):
- `Authorization: Bearer <token>` — precedence: user access token → identity grant → anon token →
  device grant (whichever is the highest currently held).
- `x-sxm-clock: [0,<monotonic-request-index>]`
- `x-sxm-platform: browser`
- `x-sxm-tenant: sxm`
- Body content-type `application/json; charset=UTF-8`; `Accept: application/json`.

Token lifecycle: proactively drop a token ~10 min before its `*ExpiresAt` and re-mint; on `401`,
clear cached tokens (device too after repeated failures) and retry with backoff; on `500`, back off
~5 min. A background `POST` to a status endpoint every ~50s keeps the session marked active. **Net
effect: stored username/password → silent, self-renewing bearer session.** No user interaction, no
cookie scraping.

### What Phosphor would change (contract stays put — see next section)

All of this is internal to the SiriusXM plugin. `SxmClient` currently does cookie auth against
`player.siriusxm.com`; migrating means: (a) add the 4-step edge-gateway token chain + header
injection (mirror `APISession`/`ClientExtensions`, but as plain `HttpClient` calls — we don't need
NSwag), (b) persist the three token caches under the plugin instance dir (`device.json` /
`tokens.json` / `access.json`), (c) replace `GetNowPlayingAsync` internals to `POST liveUpdate` and
select the `SONG` cut whose window contains the audio instant, and (d) point stream resolution at the
gateway's `tuneSource` if/when we also move playback off the old REST path. Steps (a)–(c) alone
unblock the accurate now-playing label; (d) is the fuller hedge against the old REST host being shut
off. Region (`US`/`CA`) maps to the `x-sxm-tenant`/localization params rather than the old
`app-region`.

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

1. Implement edge-gateway auth (4-step JWT chain + `x-sxm-*`/`Authorization` header injection, token
   caches under the instance dir) and the `liveUpdate` fetch/parse inside `SxmClient` (replace or
   augment `GetNowPlayingAsync`). Mirror `yob15662/sxm-player`'s `APISession` / `ClientExtensions` /
   `MetadataService`, but as plain `HttpClient` calls (no NSwag).
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

## Path A implemented in the experimental plugin (`Phosphor.Plugins.SiriusXM_Exp`)

Status: **working end-to-end.** Path A (edge-gateway `liveUpdate` now-playing) is implemented in a
NEW parallel plugin, `Phosphor.Plugins.SiriusXM_Exp` (in the `phosphor-plugins` repo), so the shipping
`SiriusXM` plugin stays untouched as the fallback/baseline for A/B comparison. Distinct identity
throughout: TypeId `siriusxm-exp`, DisplayName `SiriusXM (Exp)`, deploy folder `plugins/SiriusXM_Exp`,
default proxy port `8913`, and its own token caches (`device.json` / `tokens.json` / `access.json`)
under the plugin instance dir. Only now-playing runs on the gateway; **live HLS streaming still uses
the old cookie `player.siriusxm.com` path** (copied verbatim) — migrating playback to the gateway
`tuneSource` is a documented FOLLOW-UP (the deprecation hedge), not yet done.

New client: `SxmEdgeClient.cs` — plain `HttpClient` (no NSwag). Implements the 4-step JWT chain,
`x-sxm-*` + `Authorization: Bearer` injection (precedence access → identity grant → anon → device),
token caches, proactive ~10-min expiry refresh, and 401/500 clear-and-retry. `SiriusXmExpSource`
routes `GetNowPlayingAsync` through it while keeping the cookie `SxmClient`/`SxmProxy` for lineup +
streaming (dual auth).

### Gotchas resolved while bringing it up (do not re-derive)

1. **Device grant 400.** `POST /device/v1/devices` rejects guessed values. The body MUST mirror the
   reference exactly: `devicePlatform = "web-desktop"` and `deviceAttributes.browser` = Edge/121.0.0.0
   (`browser:"Edge"`, `app:"web"`, `sdk:"web"`, matching userAgent). `"browser"`/`"sxm"` → 400.
2. **`liveUpdate` requires a UUID `channelId`, not the slug.** The gateway 400s with
   `String '9446' does not match pattern '^[0-9a-f]{8}-...'`. The cookie lineup already carries the
   UUID as `SxmChannel.Guid` (the `channelGuid`) — prefer it. The `all-channels` container map is only
   a fallback, and note its shape: the UUID is `entity.id`, the channel number is
   `decorations.channelNumber` (NOT under `entity`), which is why an early `entity.channelNumber`
   parse returned 0 entries. `SxmEdgeClient` now returns null (skips `liveUpdate`) rather than send a
   slug the gateway will reject.
3. **Error-body logging.** `SendJsonAsync` logs a snippet of the gateway's JSON error body on any
   non-2xx — the two 400s above were diagnosed entirely from that payload. Keep it.

### Anchoring on the edge feed — `LiveAudioLagMs`

The edge `items[]` are broadcast-schedule timestamps (ISO8601). Select the SONG cut (skip
`isInterstitial`) whose `[timestamp, timestamp+duration)` window contains the **audio instant**, where
`audioInstant = UtcNow - LiveAudioLagMs`. The lag is the listener's HLS buffer behind live (LibVLC
queues a few ~10s segments). With `LiveAudioLagMs = 0` the label led the audio and the "next" track
popped early (~27s). **Tuned value: `LiveAudioLagMs = 30000` (30s)** — measured on ch.18: 27000 was
~3s early, 30000 lands within the natural ~one-segment (~10s) jitter. `NextChangeUtc` (the host's
next-poll / label-flip time) is shifted **forward** by the same lag, since the listener hears the
cut boundary `LiveAudioLagMs` after the schedule time. The `SXM np:` diagnostics now print
`audioInstant / songStart / audio-songStart / songEnd / end-audio`; aim for a small positive
`audio-songStart`.

### Dynamic lag — considered, deferred (a constant is deliberate)

A truly dynamic lag would need LibVLC's live-buffer DEPTH at the moment of the poll, which is not
observable from the plugin. The available signals are all indirect: `playbackPosition` is
elapsed-since-tune-start (not distance-behind-live); `TUNE_START` drifts (see above); the proxy could
read `#EXT-X-PROGRAM-DATE-TIME` off the last served segment to compute the live edge, but VLC still
plays an unknown ~3-segment buffer behind whatever we serve — so we'd be back to estimating that
buffer with a constant, now with more moving parts (missing PDT tags, playlist jitter) to shave a few
seconds that already vary by ~one segment. Verdict: **keep the constant**; revisit only if the fixed
value proves too jittery in the A/B test. If revisited, the cleanest source is the proxy's own
distance-behind-live (it already trims segments for the DVR offset) exposed to `SxmEdgeClient`.

### Re-enable / A/B checklist for the Exp plugin

1. Build with `PHOSPHOR_HOST_DIR` set so it deploys into the host bin (else it lands in repo `dist/`).
2. Host flags: set `JukeboxViewModel.LiveNowPlayingEnabled = true` (starts the poll loop) and
   `PlayerContext.ShowLiveTrackLabel = true` (renders the label). Without these the plugin is polled
   but nothing shows.
3. Watch the host Debug output for `SXM np:` lines; confirm correct `Artist - Title` and sane
   `audio-songStart` before trusting the on-bar label.
4. A/B: compare `siriusxm` (old cookie feed) vs `siriusxm-exp` (edge feed) on the same channel.

## Streaming migrated to the edge gateway too (`SiriusXM_Exp` — off legacy `player.siriusxm.com`)

Status: **implemented.** Live HLS playback in `SiriusXM_Exp` now runs on the edge gateway
(`tuneSource` + `key`, bearer/JWT), NOT the deprecated cookie `player.siriusxm.com/rest/v2|v4` path.
Motivation: the old REST host is on borrowed time; the goal is for this plugin to be **100% off
legacy APIs**. The only remaining legacy caller is the **channel lineup** (still cookie `SxmClient`) —
that's the last follow-up for true 100% (edge equivalent: `GET /relationship/v1/container/all-channels`,
already used as the now-playing channelId-map fallback).

### Endpoints (from `yob15662/sxm-player` `PlaylistService` / `HlsEncryptionService`)

- **Resolve stream:** `POST /playback/play/v1/tuneSource` (bearer) with body
  `{ id=<channel UUID>, type="channel-linear", hlsVersion="V3", manifestVariant="WEB", mtcVersion="V2" }`.
  Response `streams[].urls[]`; pick `isPrimary==true`. Each `url` is a **PRE-SIGNED akamai** master
  playlist (`validUntil`, `encryptionKeyId`). For live radio `type` is `channel-linear` →
  `manifestVariant="WEB"` (non-linear would be `FULL`).
- **Content key:** the variant's `#EXT-X-KEY` URI carries a GUID; fetch the AES-128 key via
  `GET /playback/key/v1/{guid}` (bearer) → `{ keyId, key }`, `key` is base64 (16 bytes).
- **Segments/playlists:** fetched **verbatim from the pre-signed CDN URL** — NO bearer, NO cookie
  `token`/`gupId` params (any injected auth breaks the akamai signature). This is the key difference
  from the legacy proxy, which appended cookie token params to every akamai request.

### Implementation

- `SxmEdgeClient` gained `TuneSourceAsync` (returns the primary master URL), `GetKeyAsync` (raw AES-128
  key by GUID), and `GetCdnAsync` (a SEPARATE plain `HttpClient` with no base address / no auth headers
  for the pre-signed fetches).
- New `SxmEdgeProxy` (bearer replacement for `SxmProxy`, proxy style B — LibVLC decrypts): resolves
  master→variant via `tuneSource`, rewrites `#EXT-X-KEY` URI → local `/key/{guid}` (serves the gateway
  key, cached), segment URIs → local `/seg/`, strips `EXT-X-ENDLIST`/`PLAYLIST-TYPE` so VLC keeps the
  live window open-ended, and re-resolves via `tuneSource` when the pre-signed window expires/stalls.
- `SiriusXmExpSource.ResolveAsync` branches on a compile-time `UseLegacyStreaming` const. Default
  **false** = edge path. On the edge path it **force-fails with NO fallback** to the cookie path — a
  deliberate testing choice so any gateway streaming problem is obvious rather than masked. The legacy
  cookie path (`SxmClient`/`SxmProxy`) is retained behind the const purely for manual rollback.

### What's still legacy in `SiriusXM_Exp` (the last follow-up)

Only the **channel lineup** (`SxmClient.GetChannelsAsync` on cookie `rest/v4`). To reach 100%-off-legacy,
port lineup to the gateway `all-channels` container (entity.id = UUID, `decorations.channelNumber`,
images under `entity.images`) and drop the cookie `SxmClient` from the plugin entirely. Once that's
done, `UseLegacyStreaming`/`SxmClient`/`SxmProxy` can be deleted from the Exp plugin.

### Streaming gotchas resolved during bring-up (VLC `EndReached` at Time=0)

First playback attempt died instantly (`EndReached State=Ended Time=0 Length=0`). A standalone probe
(reused `SxmEdgeClient` outside the host) proved the upstream chain was FINE — auth, tuneSource,
master (4 bitrates), 216KB media playlist, key fetch (16 bytes), segment fetch (200/315KB) all worked.
The bugs were in the proxy's serve/rewrite path:

1. **tuneSource returns a MASTER, not a media playlist.** `9446_variant_web_v3.m3u8` lists 4 variants
   (`#EXT-X-STREAM-INF` 256/128/64/32k). The proxy must detect master vs. media (`#EXT-X-STREAM-INF`
   vs `#EXTINF`) and, for a master, resolve the first sub-variant. `ResolveVariantAsync` now branches.
2. **`/seg/` 400 from http.sys BEFORE the handler runs (the real killer).** The pre-signed CDN URLs
   carry a ~700-char session token in ONE path segment. Base64url'ing the full URL into the local
   proxy path (`/seg/<base64>`) produced a ~1000-char path segment, which Windows' http.sys rejects
   with 400 (`UrlSegmentMaxLength` ≈ 260) — so our handler never even ran, and the direct fetch of the
   identical URL succeeded, which was the tell. Fix: the proxy now maps a SHORT key (the `.aac`
   filename) → full URL in a dictionary and serves `/seg/<filename>`; no long path segment.
3. **Huge DVR window.** The gateway media playlist is ~1845 segments (~5h, 216KB) with no ENDLIST —
   serving it whole makes VLC start hours behind live. The proxy now trims to the last
   `LiveWindowSegments` (12 ≈ 2 min) and adjusts `#EXT-X-MEDIA-SEQUENCE` accordingly (verified:
   served playlist 1.7KB, seq bumped by the dropped count, KEY tag preserved + redirected).

The content-key GUID in `#EXT-X-KEY` is the all-zeros GUID `00000000-...`; `GET /playback/key/v1/{that}`
still returns a valid 16-byte key. A throwaway probe harness (compile `SxmEdgeClient`/`SxmEdgeProxy`
directly, drive from `SXM_USER`/`SXM_PASS` env vars) was invaluable for isolating proxy vs. gateway —
recreate it if streaming regresses.

## Channel lineup migrated too — `SiriusXM_Exp` is now 100% off legacy APIs

Status: **done.** The channel lineup now comes from the edge-gateway `all-channels` container instead
of the cookie `rest/v4`. With auth, now-playing, streaming, AND lineup all on the gateway, the Exp
plugin no longer calls `player.siriusxm.com` at all in normal operation.

### Endpoint + field mapping (confirmed from a live dump — 712 channels, single set)

`GET /relationship/v1/container/all-channels?containerId=3JoBfOCIwo6FmTpzM1S2H7&useCuratedContext=false&entityType=curated-grouping&entityId=403ab6a5-d3c9-4c2a-a722-a94a6a5fd056&offset=0&size=1000&setStyle=small_list&key=<base64(guid)>` (bearer).
Response: `container.sets[].items[]`, each with:
- `entity.id` — the channel **UUID** → used as BOTH `SxmChannel.Id` and `.Guid` (so now-playing/tuneSource
  get the UUID they require directly; no more slug/number lookup).
- `entity.texts.title.default` — channel **name** (e.g. "The Beatles Channel"). NOTE: an earlier map
  parse wrongly used `decorations.contentTypeLabel` (which is "CHANNEL") — use `texts.title.default`.
- `decorations.channelNumber` (int) — channel **number** (`channelNumberCanonical` as fallback).
- `decorations.genre` — a display **genre** (e.g. "Pop", "Hip-Hop") used as the single category. This
  replaces the old cookie lineup's category *keys*; `SxmCategoryMap` keys off a slugified genre.
- `entity.images.{tile|logo}.aspect_1x1.default.url` — a RELATIVE key (e.g. `if/1f/..png`). Resolve it
  through the SXM image server: `https://imgsrv-sxm-prod-device.streaming.siriusxm.com/<base64>` where
  the base64 encodes `{"key":"<relativeKey>","edits":[{"format":{"type":"jpeg"}},{"resize":{"width":600,"height":600}}]}`
  (verified 200/JPEG). Mirrors `yob15662/sxm-player`'s image URL builder.

`size=1000` returns the whole lineup in one call (712 today) — no paging needed. Implemented as
`SxmEdgeClient.GetChannelsAsync`; `SiriusXmExpSource` uses it for browse (`EnsureChannelsAsync`),
`RefreshAsync`, and `TestConnectionAsync`. `LineupCacheVersion` bumped to 3 to reject old cookie-slug
caches (the ids changed from slugs to UUIDs).

### Category grouping caveat

The gateway gives a display genre, not the old stable category *key*, so `SxmCategoryMap`'s Music/Talk/
Sports super-grouping now buckets off slugified genres. There are **31 genres** total (dumped live);
`categories.json` + the built-in `Seed` now map all 31 into Music/Talk/Sports (verified none fall to
"Other"). Slug rule: `genre.ToLowerInvariant().Replace(" & ","").Replace(" ","")` (e.g.
"Dance & Electronic" → `danceelectronic`, "R&B" → `r&b`, "Sports & Recreation" → `sportsrecreation`).

Two gotchas resolved here:
- **Sub-genre granularity is lost vs. the web UI.** The `all-channels` container carries ONE coarse
  genre per channel, so decade channels (50s on 5, 60s on 6, 70s on 7 …) all report `Pop`/`Rock` and
  merge under those tiles. The web UI's decade/sub-genre tiles come from separate curated-grouping
  containers (different `containerId`/`entityId` per super-genre) — reproducing them is a future
  "Path B" (nested browse), not in scope yet.
- **`SxmCategoryMap` loaded the map from the wrong directory.** It used `AppContext.BaseDirectory`,
  which for a dynamically-loaded plug-in is the HOST exe dir — so the bundled `categories.json` (in
  `plugins/<Name>/`) was never found and it silently fell back to `Seed` (symptom: only the genres
  whose slugs matched `Seed` appeared). Fixed to resolve from the plug-in ASSEMBLY location
  (`typeof(SxmCategoryMap).Assembly.Location`). NOTE: the same bug exists in the shipping `SiriusXM`
  plug-in (masked because its `Seed` matches its json) — left untouched per "copy, don't modify".

### Legacy removal is now unblocked (follow-up)

With lineup on the gateway, nothing in normal operation needs the cookie path. The only remaining
references are the gated legacy streaming path (`UseLegacyStreaming=false` → `SxmClient`/`SxmProxy`
in `ResolveAsync`) and `EnsureClientAsync`. Once the gateway streaming is trusted in the A/B test,
delete `SxmClient.cs` + `SxmProxy.cs` + the `UseLegacyStreaming` branch to fully retire the legacy code.

## Consolidated back into the shipping `SiriusXM` plugin (v1.1.0) — one plugin again

Status: **done.** The experimental `SiriusXM_Exp` plugin has been folded back into the shipping
`Phosphor.Plugins.SiriusXM` project (in place, history preserved) and the `_Exp` project deleted, so
there is a SINGLE SiriusXM plugin again. The A/B baseline is intentionally gone now that the gateway
path is trusted.

What the consolidation did:
- **Identity kept** = drop-in replacement: TypeId stays `siriusxm`, DisplayName "SiriusXM", default
  proxy port 8912, deploy folder `plugins/SiriusXM`. Existing configured instances keep working.
- **Code brought over:** `SxmEdgeClient.cs` + `SxmEdgeProxy.cs` added; `SiriusXmSource.cs`,
  `SxmCategoryMap.cs`, `categories.json` replaced with the gateway versions (renamespaced to
  `Phosphor.Plugins.SiriusXM`, `SiriusXmSource`/`SiriusXmSourceProvider`, DisplayName "SiriusXM", and
  all `SXM (Exp):` log strings normalized to `SXM:`). Legacy `SxmClient.cs`/`SxmProxy.cs`/`SxmNode.cs`
  kept unchanged.
- **Legacy fallback retained** behind `SiriusXmSource.UseLegacyStreaming` (default false) for rollback.
- **Version bumped to 1.1.0.**

**BREAKING for existing users (accepted):** channel ids changed from the old cookie **slugs**
(`9446`, `octane`) to gateway **UUIDs**, so previously saved **favorites and hidden channels reset**
(they stored slugs that no longer match any channel). `LineupCacheVersion` is 3 so stale slug caches
are rejected automatically. Re-favoriting/hiding is required once after upgrade — acceptable for the
tester audience; no slug→UUID migration was written.

Remaining cleanup (unchanged from above): once gateway streaming is fully trusted, delete
`SxmClient.cs`/`SxmProxy.cs`/`SxmNode` legacy references and the `UseLegacyStreaming` branch to retire
the cookie path entirely. Note the `SxmCategoryMap` assembly-location load fix now lives in the
shipping plugin (the original bug — loading from `AppContext.BaseDirectory` — is fixed here).

## Future: "Up next" / "Coming up" (scoped separately)

A follow-on feature would surface **what's coming up next** on a live channel. SiriusXM is a natural
first implementer: the edge `liveUpdate` response we already fetch for now-playing carries the
**forward schedule** (`items[]` ahead of the current cut) — today `ExtractNowPlaying` selects the
current cut and discards the later ones, so "up next" is nearly free (pick the next `SONG` cut after
the current one, same `LiveAudioLagMs` anchor). HDHomeRun (EPG "next program") is a later candidate.

This needs a small abstractions rev (**0.16.0**): a new opt-in capability
`ILiveUpNextProvider` (single next item) plus a reserved `ILiveUpcomingProvider : ILiveUpNextProvider`
(forward list, for a future search/discovery view), and a `LiveUpNext` record. Full design + locked
decisions are in **`docs/LIVE_UPNEXT_SCOPING.md`** — implement from there.
