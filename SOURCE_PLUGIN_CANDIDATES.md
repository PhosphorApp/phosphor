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
| **Vimeo** | On-demand video+audio | yt-dlp extractor (proven) | Low–Med | Free | ✅ Good fit |
| **SoundCloud** | On-demand audio | yt-dlp extractor (proven) | Low–Med | Free (+API gating) | ✅ Best audio candidate |
| **Bandcamp** | On-demand audio | yt-dlp extractor (proven) | Low–Med | Free | ✅ Feasible (discovery is the work) |
| **iHeartRadio** | Live stations + podcasts | yt-dlp (partial) / stream URLs | Med | Free | 🟡 Partial (on-demand fits; live needs stream handling) |
| **SiriusXM** | Live channels (auth) + some on-demand | yt-dlp supports w/ subscriber login | Med–High | Paid sub | 🟡 Possible — **leaning candidate** (see below) |
| **Tidal** | On-demand audio | ❌ DRM, no legal stream URL | High/blocked | Paid sub | ❌ Not viable |
| **Pandora** | Personalized radio session | ❌ DRM + session model | High/blocked | Free/Paid | ❌ Not viable |

Ranked "worth doing": **Vimeo → SoundCloud → Bandcamp → (iHeart / SiriusXM, if pursuing live) →
skip Tidal & Pandora.**

---

## ✅ Good fits

### Vimeo
- **Compatibility:** Excellent. On-demand video, maps onto the YouTube-shaped model (video items,
  muxed or separate A/V). `HttpHeaders` covers referer/cookie needs.
- **Playback:** yt-dlp Vimeo extractor (progressive/HLS). Delegate `ResolveAsync` to the existing engine.
- **Discovery:** Official Vimeo API (OAuth2, free tier) for search/browse, *or* URL/embed paste with
  no API at all as the cheapest MVP.
- **Cost:** Free for public content; API rate limits.
- **Limitations:** Private/password/domain-locked videos won't resolve; not a music-focused catalog
  (it's "add a video source" more than "add a music service").
- **Effort:** Smallest lift of all candidates.

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

### SiriusXM — **leaning candidate**
- **Why we're leaning in:** (a) already a subscriber, (b) an interesting challenge to see how
  **infinite streams** fit into the jukebox model.
- **Compatibility:** Almost entirely **live channels**, plus some on-demand shows.
- **Playback:** yt-dlp *has* a SiriusXM extractor that works **with subscriber credentials**,
  resolving the authenticated HLS stream. Playback is technically feasible for a logged-in subscriber.
- **Discovery:** Channel lineup via the authenticated session; not a searchable on-demand catalog.
- **Cost:** Paid subscription (already held). Credentials must be stored — the host has a
  credential-store seam, but note the **deferred DPAPI secret-encryption** item in the architecture doc.
- **Limitations:** Live-stream model (no track boundaries/gapless/progress/auto-advance), auth/session
  refresh complexity, ToS sensitivity, ads on lower tiers.
- **Proposed approach — `IsLiveStream` / `IsInfinite` flag:** Add a flag on the item/stream model that
  marks a source as a continuous live stream, so the host can handle it appropriately:
  - Suppress the progress bar / seek UI and duration display.
  - Never auto-advance the playlist (the stream never "ends"); "next" becomes a channel change.
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
