# Phosphor — Source Plug-in Candidates

First-pass, high-level feasibility notes for potential third-party **source** plug-ins,
built on the capability-based contract in `Phosphor.Plugin.Abstractions` (see
`PLUGIN_ARCHITECTURE_ANALYSIS.md` and `PLUGIN_AUTHORING_GUIDE.md`).

> **Scope.** These are *first-pass* assessments — feasibility, difficulty, cost, and
> limitations only, not designs. Nothing here is committed work.

---

## 🔑 The lens we assess against

Every candidate is judged against what the architecture already gives a source:

- **Transport-agnostic playback.** `IPlayableResolver` returns a `ResolvedStream` with a
  `StreamTransport` (`Http` / `File` / `Other`), a `StreamLayout`
  (`SeparateVideoAudio` / `Muxed` / `AudioOnly`), primary + optional audio-slave URIs, and
  **per-stream HTTP headers** (cookies/referer). Any source that can produce an HTTP(S) URL —
  even a short-lived, header-bound one — is playable. Low bar.
- **yt-dlp is in-box.** The YouTube source already resolves via yt-dlp. Any site with a mature
  yt-dlp extractor can reuse that path instead of hand-rolling resolution.
- **Audio-only is first-class.** `StreamLayout.AudioOnly` plays fine under the audio-reactive
  visuals — audio-only services are welcome.
- **DRM / licensing is the usual blocker.** If there's no legal way to obtain a raw playable
  stream (DRM-encrypted, licensed-player-only), the plug-in cannot resolve it.
- **⚠️ The jukebox model assumes finite, seekable tracks with a duration.** Live/continuous
  radio streams don't fit natively (no track boundaries, no progress, no auto-advance). See the
  cross-cutting note at the bottom.

---

## 📊 Summary

| Source | Content shape | Playback path | Difficulty | Cost | Verdict |
|---|---|---|---|---|---|
| **Jellyfin** | On-demand music + video (self-hosted server) | Custom C# REST client, direct HTTP stream URLs | Low–Med | Free (self-host) | 🟢 **Shipped** — `Phosphor.Plugins.Jellyfin` (see below) |
| **Vimeo** | On-demand video+audio | yt-dlp resolve + Vimeo API browse/search | Low–Med | Free | 🟢 **Shipped** — `Phosphor.Plugins.Vimeo` (see below) |
| **SoundCloud** | On-demand audio | yt-dlp extractor (proven) | Low–Med | Free (+API gating) | ✅ Best audio candidate |
| **Bandcamp** | On-demand audio | yt-dlp extractor (proven) | Low–Med | Free | ✅ Feasible (discovery is the work) |
| **iHeartRadio** | Live stations + podcasts | yt-dlp (partial) / stream URLs | Med | Free | 🟡 Partial (on-demand fits; live needs stream handling) |
| **SiriusXM** | Live channels (auth) + some on-demand | Custom C# client (auth+lineup ✅ proven) + HLS AES proxy | Med–High | Paid sub | 🟢 **In progress** — auth+lineup validated (see below) |
| **Spotify** | On-demand audio (huge catalog) | Discovery: SpotAPI-style C# client ⚠️ (rotating-secret TOTP) / Audio: **librespot** (Premium) ❌ | High | Paid sub (Premium) | 🟠 Hard — **spiked; recommend against** (brittle discovery + ToS rejection + unbuilt audio) |
| **Tidal** | On-demand audio | ❌ DRM, no legal stream URL | High/blocked | Paid sub | ❌ Not viable |
| **Pandora** | Personalized radio session | ❌ DRM + session model | High/blocked | Free/Paid | ❌ Not viable |

Ranked "worth doing": **~~Vimeo (shipped)~~ → SoundCloud → Bandcamp → (iHeart / SiriusXM, if pursuing live) →
Spotify (only if the librespot audio bridge proves out) → skip Tidal & Pandora.**

---

## ✅ Good fits

### Jellyfin — **shipped (v1)**
- **Why:** the on-demand, finite/seekable counterpart to SiriusXM — a self-hosted media server
  (music + video) that validates the browse contract without any live-stream complications.
- **Compatibility:** Excellent — the **Plex shape**. Token auth, hierarchical browse
  (library → artist → album → track, plus movies/videos), and **direct long-lived HTTP stream URLs**,
  so **no local proxy** and **no `IsLiveStream`** needed.
- **Playback:** custom pure-`HttpClient` REST client (`JellyfinClient`) — `POST /Users/AuthenticateByName`
  (`X-Emby-Authorization`) → `AccessToken`; `/Users/{id}/Views` + `/Users/{id}/Items` for browse;
  `/Audio/{id}/universal` + `/Videos/{id}/stream` for playback. Plays through the host's normal
  `StreamUrl → Media → Play` path — no ffmpeg, no yt-dlp.
- **Stereo (2.1):** a **"Stereo audio"** setting (default on) forces `MaxAudioChannels=2` — imperative
  on pinball cabs whose surround channels drive mechanical/ball exciters, mirroring the Plex stereo option.
- **Capabilities:** `IBrowsable` + `ITextSearchCapable` + `IPlayableResolver` + `IConnectionTestable`.
  Multi-instance (home + friend's server).
- **Effort:** Low–Med — shares the Plex interaction shape; built out-of-tree exactly like SiriusXM
  (self-deploy → auto-discovery, **zero host changes**).
- **Deferred:** favorites (`IFavoritable`, server-side like Plex), paged browse, scoped search, gapless.

### Vimeo — **shipped (v1)**
- **Why:** the smallest lift of the video candidates — on-demand video that maps onto the
  YouTube-shaped model (video items, muxed or separate A/V), resolvable by the mature yt-dlp Vimeo
  extractor.
- **What shipped (`Phosphor.Plugins.Vimeo`, out-of-tree):**
  - **Playback:** reuses the *tool* (host-bundled `yt-dlp` via `GetToolPath`), not the host's internal
    engine (unreachable across the plug-in load boundary). A small `YtDlpResolver` replicates the
    `-g` resolution shape (separate/muxed/audio-only) + `--dump-single-json` metadata.
  - **Discovery:** the official **Vimeo API** via a pure-`HttpClient` `VimeoClient` using an
    **unauthenticated (public-scoped) access token** — `Authorization: bearer <token>`, **no OAuth
    redirect flow / no client secret**. Browses Vimeo's curated **categories** plus curated
    **channels** (**Staff Picks**), and searches the public catalog.
  - **Capabilities:** `IBrowsable` + `IPagedBrowsable` (lazy "load more") + `ITextSearchCapable` +
    `IFavoritable` (star to pin, rich records persisted to the instance dir, id-only enrichment via
    `GET /videos/{id}`) + `IPlayableResolver` + `IConnectionTestable`.
  - **Perf:** implements the new **`IDeferredStreamResolution`** contract marker so the host resolves
    the (expensive) yt-dlp stream **lazily at play time**, not once per search/browse row — keeps
    lists fast (the fix that generalized YouTube's built-in deferral into the contract).
- **Token model:** per-user — each user registers a free Vimeo app and supplies their own token
  (stored as a `Secret`, never embedded). Required for discovery; playback of a pinned favorite still
  needs no token. Vimeo has no keyless search, unlike YouTube.
- **Deferred:** user-OAuth private library (likes/folders/uploads), more curated channels, paged
  *search* (browse is paged), `IDownloadable`.
- **Limitations:** Private/password/domain-locked videos won't resolve; a filmmaker/creator platform,
  not a music catalog (categories reflect that — Animation, Documentary, …).

### SoundCloud
- **Compatibility:** Excellent. On-demand **audio-only** (`AudioOnly`, `IGaplessCapable` candidate).
- **Playback:** Mature yt-dlp SoundCloud extractor (HLS/progressive). No new resolution code.
- **Discovery:** Real API (search, playlists, users) — but **new API keys are gated/waitlisted** and
  OAuth is required. MVP fallback = URL/artist-page paste + yt-dlp, no key needed.
- **Cost:** Free content; API access is approval-gated, not paid.
- **Limitations:** Some tracks preview-only or stream-disabled; API-key gating; audio-only.

### Bandcamp
- **Compatibility:** Good but **audio-only** (`AudioOnly`, `IGaplessCapable` candidate).
- **Playback:** yt-dlp Bandcamp extractor resolves streamable tracks (≈128 kbps MP3 stream).
- **Discovery:** The main friction — **no supported public search API**. Options: scrape
  `bandcamp.com/search` JSON (fragile, ToS-sensitive) or accept URL/artist/label-page paste
  (`IBrowsable` over a pasted page is a clean, low-risk MVP).
- **Cost:** Free.
- **Limitations:** Stream quality capped at preview bitrate; scraping fragility; audio-only.

---

## 🟡 Partial fits (live-radio UX mismatch)

### SiriusXM — **in progress (Phase 0 proven)**
- **Why:** (a) already a subscriber, (b) an interesting challenge to see how **infinite streams** fit
  into the jukebox model.
- **Compatibility:** Almost entirely **live channels**, plus some on-demand shows.
- **Playback:** ⚠️ **yt-dlp does NOT support SiriusXM** (verified against yt-dlp 2026.07.04 — no
  extractor; 0 of 1,752 match). The earlier "yt-dlp works" note was wrong. A real integration needs a
  **custom C# client** that (1) authenticates, (2) fetches the lineup, and (3) runs a local HLS proxy
  that rewrites the channel `.m3u8` and serves the segment AES key. The HLS AES key is a **static,
  publicly-known constant** (`0Nsco7MAgxowGvkUT8aYag==`), which significantly de-risks the proxy phase.
- **Discovery:** Channel lineup via the authenticated session; not a searchable on-demand catalog.
- **Cost:** Paid subscription (already held). Credentials stored as `Secret` settings — covered by the
  shipped opt-in DPAPI encryption.
- **Limitations:** Live-stream model (no track boundaries/gapless/progress/auto-advance), auth/session
  refresh complexity, ToS sensitivity, ads on lower tiers.

#### ✅ Phase 0 spike — auth + lineup (DONE, `siriusxm` branch)
A standalone pure-C# console spike (`tools/SiriusXmSpike/`) proved the two hardest unknowns against
live subscriber credentials — **no browser, no Python, no yt-dlp**:
- **Login** → `POST /rest/v2/experience/modules/modify/authentication` (deviceInfo + standardAuth) →
  `SXMAUTHNEW` cookie.
- **Authenticate** → `POST .../resume?OAtrial=false` → `AWSALB` + `JSESSIONID` cookies.
- **Lineup** → `POST /rest/v4/experience/modules/get?type=2` → **436 channels** enumerated with clean
  `channelId` slugs (e.g. `octane`, `howardstern100`, `thepulse`), numbers, and names.
- All POST bodies wrap `{"moduleList":{"modules":[{"moduleRequest":…}]}}`; responses unwrap via
  `["ModuleListResponse"]`. Constants: `SXM_APP_VERSION=5.36.514`, `SXM_DEVICE_MODEL=EverestWebClient`.
- Flow reverse-engineered from `AngellusMortis/sxm-client`.

**Go/No-Go decision: GO.** Auth is reproducible with `HttpClient` + `CookieContainer` and no bot/JS
challenge; the lineup is complete and clean.

#### ✅ Phase 1 spike — playback (DONE, `siriusxm` branch)
The spike's `--play <channelId>` mode proved the **entire playback chain** end-to-end in pure C#:
- `GET get/configuration` → HLS root substitutions (`siriusxm-priprodlive.akamaized.net`).
- `GET tune/now-playing-live` (assetGUID + channelId + timestamps) → master `.m3u8` URL templated with
  `%Live_Primary_HLS%`.
- Master → variant playlist; segments are AES-128 with `#EXT-X-KEY:METHOD=AES-128,URI="key/1"`.
- Downloaded a 315 KB segment and **AES-128-CBC decrypted it with the static key**
  (`0Nsco7MAgxowGvkUT8aYag==`) — no per-session key needed.
- A tiny local `HttpListener` HLS proxy rewrites the playlist (strips `EXT-X-KEY`, serves
  decrypted-in-transit AAC). The host's bundled **ffmpeg decoded it to AAC LC 44100 Hz stereo 256k →
  valid WAV**, confirming a real, playable audio stream.

**Both unknowns are now solved.** A native `Phosphor.Plugins.SiriusXM` plug-in is fully viable with
**no Python and no yt-dlp**: auth + lineup + a local decrypt proxy, all in C#.

#### ✅ Phase 2 — lean plug-in (BUILT, `siriusxm` branch)
`Phosphor.Plugins.SiriusXM` now exists and builds/deploys to `plugins/SiriusXM/`:
- `SxmClient` (auth + lineup + master-playlist resolve), `SxmProxy` (local HLS proxy),
  `SiriusXmSource` (`IBrowsable` + `IPlayableResolver` + `IConnectionTestable`),
  `SiriusXmSourceProvider` (username/password/region schema).
- **Proxy style B (LibVLC decrypts) — chosen.** The proxy keeps `EXT-X-KEY`, rewrites its URI to a
  local `/key` endpoint serving the static key, and injects SXM auth tokens onto segment requests;
  **LibVLC does the AES-128 decryption**. Rationale: less work in our hot path and a thinner proxy.
  Style A (we decrypt in-transit, proven in the spike) is the fallback. See the plug-in README.
- Host plays the local proxy URL via the existing `StreamUrl → new Media → Play` path (no ffmpeg).
- Live handling: `IsLiveStream` flows `ResolvedStream → SourceItem → VideoItem → VM`, giving
  `M:SS / *` time, disabled seek, and no auto-advance.
- **Deferred (lean v1):** channel grouping/hiding (~200 sports channels shown flat), robust
  session/token refresh (one-shot 403 re-auth for now), now-playing metadata, tuner UI polish.

#### `IsLiveStream` flag (implemented)
Marks a source item/stream as a continuous live stream so the host:
  - Suppresses the progress bar / seek UI and duration display (shows `M:SS / *`).
  - Never auto-advances the playlist (the stream never "ends"); "next" becomes a channel change.
  - Treat the visualizer as always-on over the live audio.
  - Possibly a distinct "tuner" navigation surface (channel list) vs. the track/playlist surface.
  - **This is an architecture decision, not just a plug-in** — the host must learn about endless items.

### iHeartRadio
- **Compatibility:** Mixed — **live stations** (continuous HLS) + **podcasts/on-demand** (discrete
  audio items). Both audio-only, `Http` + `AudioOnly`.
- **Playback:** Live station stream URLs often obtainable (public HLS); yt-dlp has partial support for
  on-demand/podcast content. Live = no duration/gapless/seek.
- **Discovery:** Unofficial/reverse-engineered API for station/podcast search; no clean dev program.
- **Cost:** Free.
- **Limitations:** Live-radio UX mismatch, ad-supported streams, unofficial API fragility.
- **Verdict:** On-demand/podcasts are a clean fit; live stations need the same `IsLiveStream` handling
  as SiriusXM. Scope to on-demand first if pursued.

---

## 🟠 Hard (two-subsystem: discovery ≠ audio)

### Spotify — **re-evaluated vs. the SiriusXM approach (spike: `tools/SpotifySpike/`)**
- **Why re-look:** the SiriusXM win — a custom C# auth+lineup client plus a local decrypt proxy —
  raised the question of whether the same playbook lands Spotify. It does **not**, and it's important
  to see *why*: Spotify is **two independent problems**, and only one of them is like SiriusXM.
- **The reference (`Aran404/SpotAPI`):** a Python wrapper that emulates the private/public **web** API
  — search, playlists, albums, artists, likes/follows, and player *remote-control* (Spotify Connect).
  Crucially, **SpotAPI returns metadata and control, never a decryptable audio stream.** It is the
  *lineup half* of the SiriusXM problem, not the *decrypt half*. So it answers "what can we browse?"
  and says nothing about "how do we get bytes we can play?"
- **Why the SiriusXM trick does NOT transfer:** our SiriusXM playback hinged on one gift — HLS
  segments encrypted with a **static, publicly-known AES-128 key**, so a tiny proxy decrypted them.
  Spotify has **no equivalent**: per-track keys are exchanged over Spotify's proprietary **AP
  (access-point) protocol** (Widevine/EME in the web player), **yt-dlp does not support Spotify**, and
  there is no static key to serve. There is no thin-proxy shortcut.

| | SiriusXM | Spotify |
|---|---|---|
| Auth | user/pass → cookies ✅ | user/pass → **CAPTCHA-gated** (SpotAPI needs Capsolver or cookie import) ⚠️ |
| Discovery / lineup | custom C# client ✅ | custom C# client (SpotAPI shape) ✅ **feasible** |
| Audio encryption | AES-128, **static public key** ✅ | per-track keys via AP protocol / Widevine ❌ |
| yt-dlp support | none (custom) | **none** ❌ |

#### Two honest paths
- **Path A — metadata-only, audio via YouTube (hybrid).** Port SpotAPI's discovery to a C#
  `SpotifyClient` (mirrors `SxmClient`): browse the user's playlists/likes/albums, then resolve each
  track's audio through the **existing YouTube/yt-dlp engine** by artist+title match. Fully feasible,
  reuses everything — but it's "Spotify as a *catalog* over YouTube audio," not real Spotify sound
  (imperfect matches, wrong versions/live cuts). A novelty, not a true Spotify source.
- **Path B — real Premium audio via librespot.** The only route to genuinely decrypted Spotify audio
  is [`librespot`](https://github.com/librespot-org/librespot) (Rust, reverse-engineered Spotify
  Connect client, **Premium-only** — fits the "user has Premium" assumption). We'd bundle
  `librespot` as an external tool exactly like `yt-dlp.exe`/`ffmpeg.exe`, have it authenticate and
  expose a local stream/pipe the host plays — the SiriusXM proxy *shape*, but librespot does the AP
  handshake + decryption instead of our static-key proxy. **Discovery still comes from a SpotAPI-style
  C# client** (librespot is playback-only).

#### Effort & verdict
- **High.** Path B is materially heavier than SiriusXM: CAPTCHA-gated headless login (SpotAPI uses a
  CAPTCHA solver *or* imported browser cookies), Spotify Connect device registration, a local audio
  bridge, and version-chasing an actively cat-and-moused protocol. Strongest ToS sensitivity of
  anything assessed.
- **Not `❌ Not viable` like Tidal** — librespot + Premium *does* yield a real playable stream — **but
  not a `✅ Good fit`** either. Lands `🟠 Hard`: two subsystems (SpotAPI-style discovery **+** librespot
  audio bridge), gated auth, brittle protocol.
- **Recommendation:** if the goal is *real Spotify audio on the cab*, run a **Phase-0 spike like
  SiriusXM's — but pointed at `librespot`, not SpotAPI**: prove (1) headless Premium login with stored
  creds, and (2) a decrypted PCM/OGG stream out of a local bridge that ffmpeg accepts. SpotAPI is the
  right reference **only for the discovery half**; it cannot answer the playback question, which is the
  one that decides viability.

#### Spike — `tools/SpotifySpike/` (discovery layer)
A throwaway C# harness that proves the **answerable** half in pure C#: import an `sp_dc` browser
cookie, mint a web access token, and exercise `/v1/search` + `/v1/me/playlists` to confirm the
SpotAPI-style discovery layer works from .NET. It **deliberately does not attempt audio** — that stays
the open librespot question — and prints an explicit "audio still needs librespot" conclusion, mirroring
how the SiriusXM spike phased auth/lineup ahead of the proxy.

**First-run finding (token step is the immediate wall).** The plain `get_access_token` endpoint is now
**CDN-blocked** — a bare request returns `HTTP 403 "URL Blocked / Error 54113"` (Varnish) before
reaching Spotify. The web player switched to `/api/token` with a **TOTP one-time code validated against
Spotify's own server clock** (`/api/server-time`). The spike now implements that (RFC 6238 TOTP over
server-time + the web-player shared-secret constant), **but the secret rotates** — when Spotify changes
it, the token step 403/400s again until the constant is refreshed. Takeaway: even the *discovery* layer
carries ongoing cat-and-mouse maintenance (the fragility SpotAPI hides behind its session/solver code),
reinforcing the `🟠 Hard` rating — all before the harder librespot audio question.

**Second-run finding (valid `sp_dc`, and the verdict crystallizes).** With a real cookie the TOTP flow
*reaches Spotify's app tier* (403-edge-block → 400-from-app, so the shape is correct), and the body is
decisive:
```json
{ "totpVerExpired": "error", "totpValidUntil": "2025-10-18T09:00:00.000Z",
  "error": { "code": 400, "message": "Unauthorized request",
    "extra": { "_notes": "Usage of this endpoint is not permitted under the Spotify Developer Terms
                          and Developer Policy, and applicable law" } } }
```
Two hard signals: (1) the TOTP secret is **version-stamped and expiring** (`totpVer` had a hard expiry),
so a hardcoded constant is guaranteed to rot — discovery would need to **live-scrape the current secret
from the web-player JS bundle** on a ~weeks cadence; and (2) Spotify now returns an **explicit inline
ToS-violation notice**, a materially stronger policy signal than anything SiriusXM surfaced.

**Recommendation: park Spotify — recommend against building it.** Not because a token is unobtainable
(scraping the live `totpVer` would likely mint one), but because the spike converted "hard" into
evidence: **two brittle subsystems** (rotating-secret discovery **+** an unbuilt librespot audio bridge)
**plus an on-the-record ToS rejection**. That's a poor trade against the clean yt-dlp drop-ins
(Vimeo/SoundCloud/Bandcamp). Revisit only if the goal specifically requires Spotify and someone accepts
the maintenance + policy exposure; the next step would be a **librespot** audio spike, not more discovery
work.

---

## ❌ Not viable

### Tidal
- Audio-only **and DRM-protected** (encrypted FLAC/MQA). No legal way to obtain a raw playable URL,
  and **yt-dlp does not support Tidal** — `IPlayableResolver` has nothing to return.
- Requires a **paid subscription**; official dev API is catalog/metadata only, streaming needs a
  licensed DRM player. Metadata-only browse is possible, but playback is blocked.

### Pandora
- **Personalized radio session** model (thumbs-driven, server-decided next track) — doesn't fit
  `IBrowsable` / `ITextSearchCapable` / `IPlayableResolver` at all.
- Streams are **DRM/session-token protected**; yt-dlp does **not** support Pandora. No raw URL.
- Blocked by both the DRM/session model and the "you don't pick tracks" paradigm.

---

## 🧵 Cross-cutting: finite tracks vs. infinite streams

The jukebox model assumes **finite, seekable tracks with a duration**. SoundCloud, Vimeo, and Bandcamp
fit that natively. **SiriusXM-live and iHeart-live do not** — supporting them well means the host
learning about "endless stream" items via an `IsLiveStream` / `IsInfinite` flag (no progress bar, no
auto-advance, tuner-style navigation). That is a **host/architecture change**, tracked here as a
prerequisite for the live-radio candidates rather than a per-plug-in concern.

---

## 🔐 Cross-cutting: secret handling / DPAPI (implemented, opt-in)

Some candidates (notably **SiriusXM**) need stored subscriber credentials. Phosphor now supports
**opt-in at-rest encryption** of plug-in secrets, so a token isn't required to sit in plaintext:

- **Toggle:** `AppSettings.EncryptSecrets`, default **off**, surfaced at the **top of the Plug-ins tab**
  with a "settings file is no longer portable" warning. It's an app-level function, but since only
  plug-ins consume secrets, that's where the caveat is most relevant.
- **What gets encrypted:** any settings key a provider declares `Secret` in its schema
  (`PluginSettingDescriptor.Type == Secret` / `.Secret == true`), resolved via
  `PluginSettingsFactory.SecretKeysFor(typeId)`.
- **How (Option B — in-blob, schema-driven):** values stay inline in `settings.json` as a
  self-describing wrapper `enc:dpapi:<base64>`, encrypted/decrypted transparently at the
  `AppSettings.Save`/`Load` boundary by `Phosphor.Plugins.Host.SecretProtector`
  (`System.Security.Cryptography.ProtectedData`, `DataProtectionScope.CurrentUser` + fixed app entropy).
  **Plug-ins are oblivious** — `ApplySettings` always receives plaintext.
- **Toggle-safe:** the `enc:dpapi:` prefix makes each value self-identifying, so decryption on load runs
  regardless of the flag; flipping it re-serializes secrets on the next save (plaintext ⇄ ciphertext).
- **Caveats (by design):** DPAPI `CurrentUser` binds ciphertext to the Windows user + machine — copying
  the file elsewhere won't decrypt (values degrade to empty, not a crash). It is **not** a defense
  against code already running as the same user; it protects at-rest and against other users.
