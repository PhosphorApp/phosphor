# Spotify spike (discovery layer only)

Throwaway/experimental harness. Proves — in **pure C#**, no browser or Python at runtime — the
**answerable** half of Spotify integration: that a SpotAPI-style discovery client works from .NET.

1. Turn a browser `sp_dc` cookie into a Spotify **web access token**.
2. Call `/v1/me`, `/v1/search`, and `/v1/me/playlists` against the public web API.

## ⚠️ What this spike does NOT do — audio (on purpose)

Spotify web-player audio is **per-track-key encrypted over the proprietary AP protocol**
(Widevine/EME). **yt-dlp has no Spotify extractor**, and there is **no static-key trick** like the
SiriusXM one we exploited. So this spike deliberately stops at discovery and prints that conclusion.

Real Premium audio needs a **`librespot`** bridge (a separate, high-effort spike — "Path B" in
`SOURCE_PLUGIN_CANDIDATES.md` → Spotify). This harness only proves the discovery layer that
`Aran404/SpotAPI` demonstrates in Python.

## Result (what we learned)

- ✅ **Discovery is feasible in pure C#** — cookie → token → search + private playlists, mirroring
  the SiriusXM "auth + lineup" phase. This is the artist+title data a hybrid (YouTube-audio) source
  would match against.
- ❌ **Audio is still the blocker** — unchanged by SpotAPI. Decides viability; needs librespot.

### Actual run findings (with a valid `sp_dc`)

The token step reaches Spotify's app tier (403-edge → 400-from-app), returning:
```json
{ "totpVerExpired": "error", "totpValidUntil": "2025-10-18T09:00:00.000Z",
  "error": { "code": 400, "message": "Unauthorized request",
    "extra": { "_notes": "Usage of this endpoint is not permitted under the Spotify
                          Developer Terms and Developer Policy, and applicable law" } } }
```
Two decisive signals:
1. **Version-stamped, expiring TOTP secret** (`totpVerExpired` / `totpValidUntil`) — a hardcoded
   constant rots; discovery would need to **live-scrape the current secret from the web-player JS
   bundle** every few weeks.
2. **Explicit inline ToS-violation notice** — a materially stronger policy signal than SiriusXM.

**Verdict: recommend against building Spotify.** Two brittle subsystems (rotating-secret discovery +
an unbuilt librespot audio bridge) plus an on-the-record ToS rejection — a poor trade vs. the clean
yt-dlp candidates. See `SOURCE_PLUGIN_CANDIDATES.md` → Spotify for the full write-up.

## Run it

Auth uses a browser cookie (headless user/pass is CAPTCHA-gated — that's why SpotAPI needs a solver).
Get `sp_dc` from a logged-in browser: **open.spotify.com → DevTools → Application → Cookies → `sp_dc`**.

Credentials are never committed. Provide the cookie one of two ways:

**Environment variable:**
```powershell
$env:SPOTIFY_SP_DC = '<sp_dc cookie value>'
dotnet run
```

**Local file** (gitignored): create `spotify.local.json` next to the csproj:
```json
{ "sp_dc": "<sp_dc cookie value>" }
```
then `dotnet run`.

Optional search term (defaults to `weezer`):
```powershell
dotnet run -- daft punk
```

## Flow (reference: `Aran404/SpotAPI`)

1. `GET open.spotify.com/api/server-time` → Spotify's clock, then `GET open.spotify.com/api/token?...&totp=…&totpVer=…&ts=…`
   (with `sp_dc` cookie + a TOTP derived from server-time) → `{ accessToken, isAnonymous }`. An
   **anonymous** token means `sp_dc` wasn't accepted — re-copy a fresh one; `/me` endpoints will 401.
   (The old `get_access_token` endpoint is now CDN-blocked — see "Known brittleness" below.)
2. `GET api.spotify.com/v1/me` (Bearer token) → identity + `product` (needs `premium` for Path B audio).
3. `GET /v1/search?q=…&type=track` → public catalog search.
4. `GET /v1/me/playlists` → the user's private library.

### Known brittleness (confirmed)

The plain `get_access_token` endpoint is now **CDN-blocked** — a bare request returns
`HTTP 403 "URL Blocked / Error 54113"` from Varnish before it ever reaches Spotify's app tier.
The web player now hits `/api/token` with a **TOTP one-time code validated against Spotify's own
server clock** (`/api/server-time`), so naive local-time TOTP fails too.

This spike implements that flow (RFC 6238 TOTP over Spotify server-time, using the web-player's
shared-secret constant). **But the TOTP secret rotates** — when Spotify changes it, the token step
403/400s again until the constant is refreshed. That rotating-secret cat-and-mouse is precisely the
fragility `SpotAPI` hides behind its session/solver code, and the concrete reason Spotify is rated
`🟠 Hard` in `SOURCE_PLUGIN_CANDIDATES.md`: even the *discovery* layer needs ongoing maintenance,
before we ever get to the (harder) librespot audio question.
