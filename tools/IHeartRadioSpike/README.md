# iHeartRadio Spike (`tools/IHeartRadioSpike`)

Throwaway pure-C# harness (no auth, no yt-dlp) that probes the two unknowns deciding how feasible an
`iHeartRadio` **source** plug-in is. Endpoints referenced from
[`api-evangelist/iheart-radio`](https://github.com/api-evangelist/iheart-radio) (host `api.iheart.com`).

Run it:

```
cd tools/IHeartRadioSpike
dotnet run            # default query "rock"
dotnet run <terms…>   # e.g. dotnet run classic rock
```

## What it probes

1. **Discovery** — `GET /api/v1/catalog/searchAll?keywords=…` → live **stations**, **tracks**,
   **artists**, talk shows. Key-less.
2. **Playback (the crux)** — `GET /api/v2/content/liveStations/{id}` → the `streams` object with raw
   URLs (`hls_stream`, `shoutcast_stream`, `secure_hls_stream`, `secure_shoutcast_stream`).
3. **Podcasts** — `GET /api/v3/podcast/podcasts/{id}/episodes` for on-demand audio (skipped when the
   query surfaces no talk-show id).

## Result — **GO (with the live-radio caveat)**

Against live `api.iheart.com`, **unauthenticated, no key, no yt-dlp**:

- ✅ **Search works** — `searchAll` returned real stations (`Q104.3`, `100.7 WZLX`, …), tracks, and
  artists. Results are grouped at the **top level** (`stations[]`, `tracks[]`, `artists[]`,
  `talkShows[]`), not under a `results` node.
- ✅ **Live-station streams resolve to raw URLs** — e.g. station `4443` →
  `https://stream.revma.ihrhls.com/zc4443/hls.m3u8`.
- ✅ **No DRM** — the HLS master is a plain `#EXTM3U` with `#EXT-X-STREAM-INF … CODECS="mp4a.40.2"`
  (AAC) and **no `#EXT-X-KEY`**. Unlike SiriusXM (AES-128 segments needing a decrypt/rewrite proxy),
  iHeart's live HLS can be handed **straight to LibVLC** — no proxy at all.

### Verdict

An `IHeartClient` (pure `HttpClient`, mirroring `SxmClient`/`DailymotionClient`) is fully viable with
**zero credentials and no proxy**:

- **Discovery:** `searchAll` (+ genre/live-station browse endpoints) → `IBrowsable` +
  `ITextSearchCapable`, key-less like Dailymotion.
- **Playback:** live stations are continuous HLS ⇒ ride the existing **`IsLiveStream`** host path
  (already built for SiriusXM: no seek/progress, no auto-advance, tuner-style nav). `IPlayableResolver`
  just returns the `secure_hls_stream` URL — no decrypt, no yt-dlp, no ffmpeg.

The only real friction is the **live-radio UX mismatch** (the cross-cutting `IsLiveStream` concern),
which the host already solved for SiriusXM. Podcasts/on-demand are the cleaner finite/seekable fit and
can be layered later.

This is a throwaway probe, not part of the app.
