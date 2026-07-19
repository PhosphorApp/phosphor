# iHeartRadio Podcast / On-Demand Spike (`tools/IHeartPodcastSpike`)

Throwaway pure-C# harness (no auth, no yt-dlp) probing iHeart's **on-demand podcast** surface — the
finite/seekable, ad-light counterpart to the **live** radio the shipped `Phosphor.Plugins.IHeartRadio`
plug-in already streams. Same key-less host (`api.iheart.com`).

Run it:

```
cd tools/IHeartPodcastSpike
dotnet run            # default query "comedy"
dotnet run <terms…>   # e.g. dotnet run true crime
```

## What it probes

1. **Categories** — `GET /api/v3/podcast/categories` → 190 categories (Comedy, Crime, Business, …).
2. **Podcasts in a category** — `GET /api/v3/podcast/categories/{id}` → podcasts listed **inline**.
3. **Keyword search** — `GET /api/v3/search/all?keywords=…&podcast=true` → podcasts under
   `results.podcasts` (**the `&podcast=true` flag is required** — without it that array is empty).
4. **Episodes** — `GET /api/v3/podcast/podcasts/{id}/episodes` → episode list with **real durations**
   (finite, seekable — unlike the live streams).
5. **Playback (the crux)** — `GET /api/v3/podcast/episodes/{id}` → a direct **`mediaUrl`** MP3.

## Result — **GO**

Against live `api.iheart.com`, **unauthenticated, no key, no yt-dlp** — 5/5:

- ✅ **Discovery** — 190 categories; `categories/{id}` lists podcasts inline; keyword search works with
  `&podcast=true` (e.g. "crime" → Crime Junkie, Casefile, …).
- ✅ **Episodes are finite** — episode lists carry real `duration` values (e.g. 2351s), so they're
  seekable tracks, not endless streams.
- ✅ **Direct audio** — `episodes/{id}` returns a plain **`mediaUrl`** (podtrac/omny redirect to an
  `.mp3`). A HEAD confirmed **HTTP 200, `audio/mpeg`, ~36 MB** — a real, seekable file LibVLC plays
  directly. No DRM, no proxy, no yt-dlp.

### Verdict

Podcasts are the **clean jukebox fit** the live streams aren't: finite, seekable, `Duration`-bearing,
and far less ad-laden. A natural **second capability** for the existing iHeart plug-in rather than a
new plug-in:

- **Browse tree:** add a **"Podcasts"** branch under the root → category → podcast → episodes
  (episodes are ordinary `SourceItem`s with `Duration`, **not** `IsLiveStream`).
- **Resolve:** `episodes/{id}` → `mediaUrl` → `ResolvedStream(Http, AudioOnly, mediaUrl)` (no
  `IsLiveStream`, so the host shows normal seek/progress and auto-advance).
- **Reuse:** `IHeartClient` gains `GetPodcastCategoriesAsync` / `GetPodcastsInCategoryAsync` /
  `SearchPodcastsAsync` / `GetEpisodesAsync` / `GetEpisodeMediaUrlAsync`; the source keeps its live
  station tree and just adds the podcast subtree + a non-live resolve branch.

This is a throwaway probe, not part of the app.
