# SiriusXM spike (Phase 0 auth+lineup, Phase 1 playback)

Throwaway/experimental harness on the `siriusxm` branch. Proves — in **pure C#**, with no
browser, Python, or yt-dlp — that we can (1) authenticate a SiriusXM subscriber, (2) enumerate
the channel lineup, and (3) resolve + decrypt + serve a channel's live HLS stream to a player.

## Result

✅ **SUCCESS (both phases).**
- **Auth + lineup:** login + session resume + lineup work; 436 channels enumerated with clean
  `channelId` slugs.
- **Playback:** `--play <channelId>` resolves the master/variant playlist, AES-128-CBC decrypts a
  segment with the static key, and runs a local HLS proxy that the bundled **ffmpeg decoded to
  AAC LC 44100 Hz stereo 256k** → valid WAV.

See `SOURCE_PLUGIN_CANDIDATES.md` → SiriusXM for the full write-up and go/no-go.

## Run it

Credentials are never committed. Provide them one of two ways:

**Environment variables:**
```powershell
$env:SXM_USER = 'you'; $env:SXM_PASS = 'secret'; $env:SXM_REGION = 'US'  # or 'CA'
dotnet run
```

**Local file** (gitignored): create `sxm.local.json` next to the csproj:
```json
{ "username": "you", "password": "secret", "region": "US" }
```
then `dotnet run`.

### Prove playback for one channel

```powershell
dotnet run -- --play octane        # by channelId slug
dotnet run -- --play 37            # or by channel number
```
This resolves the stream, decrypts a segment, and starts a local proxy at
`http://127.0.0.1:8912/master.m3u8`. Play it with `ffplay`/`vlc`, or decode-test it:
```powershell
ffmpeg -i "http://127.0.0.1:8912/master.m3u8" -t 8 out.wav
```

## Flow (reverse-engineered from `AngellusMortis/sxm-client`)

Base: `https://player.siriusxm.com/rest/v2/experience/modules/{path}` (`rest/v4` for channels).

1. `POST modify/authentication` (deviceInfo + standardAuth) → `SXMAUTHNEW` cookie.
2. `POST resume?OAtrial=false` (deviceInfo) → `AWSALB` + `JSESSIONID` cookies.
3. `POST get?type=2` (v4, ChannelListing module) → channel JSON.

POST bodies wrap `{"moduleList":{"modules":[{"moduleRequest":…}]}}`; responses unwrap via
`["ModuleListResponse"]`.

## Playback flow (`--play`)

4. `GET get/configuration` → HLS root substitutions (`Live_Primary_HLS` / `Live_Secondary_HLS`).
5. `GET tune/now-playing-live` (assetGUID + channelId + timestamps) → master `.m3u8` URL templated
   with `%Live_Primary_HLS%`.
6. Master → variant playlist; segments are AES-128 (`#EXT-X-KEY:METHOD=AES-128,URI="key/1"`).
7. Segments decrypt with the **static public key** (`0Nsco7MAgxowGvkUT8aYag==`) — no per-session key.
8. A local `HttpListener` proxy rewrites the playlist (strips `EXT-X-KEY`) and serves
   decrypted-in-transit AAC, so any HLS player can consume it.

All authenticated akamai requests carry `?token=<SXMAKTOKEN>&consumer=k2&gupId=<SXMDATA.gupId>`.

## Not for shipping

This is a diagnostic spike, not the plug-in. If Phase 1 (playback proxy) succeeds, the logic
graduates into a real `Phosphor.Plugins.SiriusXM` plug-in implementing `IBrowsable` +
`IPlayableResolver`.
