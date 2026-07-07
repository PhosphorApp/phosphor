# Phosphor — yt-dlp Migration Analysis

Exploratory analysis (branch `yt-dlp`) for replacing **YoutubeExplode** with
**yt-dlp** as the YouTube backend. **Analysis phase only — no code changes yet.**

Primary goal: **more reliable scrubbing** on streaming (non-cached) YouTube
videos. Secondary goals: better search, richer metadata (native chapters),
long-term resilience to YouTube breakage.

---

## 🎯 Why consider this

YoutubeExplode (currently `6.6.0`) is an in-process library that reverse-engineers
YouTube's player. It is excellent when it works, but:

- **It breaks whenever YouTube changes signatures/ciphers**, and fixes depend on a
  new NuGet release + our upgrade. `yt-dlp` ships fixes almost daily and is the de
  facto standard for YouTube extraction.
- **Chapters must be hand-parsed** from the video description today
  (`ParseYouTubeChapters` in `JukeboxViewModel.cs`). yt-dlp exposes a structured
  `chapters` array natively.
- **Format selection is limited** to what YoutubeExplode surfaces; yt-dlp has a far
  richer format query language and better handling of DASH/HLS variants.

Reality check on the **scrubbing** goal (see `README.md` Known Issues): the root
cause is that YouTube progressive/DASH streams lack a complete seek index until
fully downloaded, so a forward scrub can wedge the VLC demuxer. **yt-dlp does not
magically fix live-stream scrubbing** — it feeds the same stream URLs to VLC. The
durable fix remains local caching (download + remux to a seekable container).
Where yt-dlp *does* help scrubbing:

1. **More reliable, faster stream resolution** → the "download then switch to
   cached file" path (already implemented in `SwitchToCachedFileAndSeek`) succeeds
   more often and sooner.
2. **`--download-sections` / partial fragment download + native remux** could let
   us materialize just the neighborhood of a scrub target quickly.
3. Better format choices (e.g. containers/codecs that VLC seeks more gracefully).

---

## 🧩 Current YoutubeExplode integration surface

Every touch point that a migration must replace or adapt:

| Area | File(s) | YoutubeExplode API used |
|------|---------|--------------------------|
| Client + timeout | `JukeboxViewModel.cs` (~L16, L558) | `new YoutubeClient(HttpClient)` |
| Search (videos) | `JukeboxViewModel.cs` (~L1229, L2999, L3074) | `Search.GetVideosAsync` (incremental `IAsyncEnumerable`) |
| Search (playlists) | `JukeboxViewModel.cs` (~L1117, L1163) | `Search.GetPlaylistsAsync` |
| Playlist by id | `JukeboxViewModel.cs` (~L1110, L1132) | `Playlists.PlaylistId.Parse`, `Playlists.GetVideosAsync` |
| Channel uploads | `JukeboxViewModel.cs` (~L1199–1216) | `Channels.GetByHandleAsync` / `GetByUserAsync` / `GetUploadsAsync` |
| Video metadata | `JukeboxViewModel.cs` (~L3192, L3210) | `Videos.GetAsync` → `Duration`, `Description` (chapters) |
| Stream manifest | `Caching/VideoCache.cs` (L111), `Caching/PrefetchCache.cs` (L84), `Windows/BackglassWindow.xaml.cs` (L746) | `Videos.Streams.GetManifestAsync` |
| Stream selection | `Services/StreamSelector.cs` (whole file) | `StreamManifest`, `VideoOnlyStreamInfo`, `AudioOnlyStreamInfo`, `MuxedStreamInfo`, `VideoQuality.MaxHeight`, `Bitrate` |
| Stream download | `Caching/VideoCache.cs` (L122–123), `Caching/PrefetchCache.cs` (L92–93) | `Videos.Streams.DownloadAsync` |
| Live stream URLs → VLC | `Windows/BackglassWindow.xaml.cs` (L756–783) | `stream.Url` passed to `new Media(_libVLC, new Uri(...))` |
| Exception suppression | `App.xaml.cs` (~L549–551) | Filters on `"YoutubeExplode"` in stack trace |

Data model the new backend must still populate: `VideoItem` (`Title`, `Author`,
`ThumbnailUrl`, `VideoId`, `Duration`, `Chapters`) — see `Models/VideoItem.cs`.

**Notable design facts to preserve:**
- Search is an **incremental async stream** feeding pagination + duration filters
  (`FilterVideosAsync`, `AsVideos<T>`). Results appear as they arrive.
- `StreamSelector` centralizes quality/stereo policy — a clean seam to re-target.
- `ffmpeg.exe` is **already bundled** in `dependencies\` and used by
  `PrefetchCache.MuxWithFfmpegAsync`. yt-dlp reuses the same binary for remux.
- Live playback hands **two URLs** (video-only + audio-only via `AddSlave`) to VLC,
  or a muxed URL as fallback.

---

## 🔀 Integration options for yt-dlp

yt-dlp is an external executable (Python-based, shipped as a standalone `.exe`).
Two ways to drive it from .NET:

### Option A — `YoutubeDLSharp` NuGet wrapper (recommended starting point)
- Latest `1.2.0`. Managed wrapper that spawns `yt-dlp.exe`, parses the JSON
  metadata (`--dump-json`), and exposes typed results + download helpers.
- Pros: least glue code; typed `VideoData`/`FormatData`; handles arg building and
  process lifecycle; supports progress + cancellation.
- Cons: another dependency; still requires shipping `yt-dlp.exe` (and it invokes
  ffmpeg, which we already bundle).

### Option B — Direct process invocation
- Spawn `yt-dlp.exe` ourselves (mirrors the existing `MuxWithFfmpegAsync`
  `ProcessStartInfo` pattern) and parse `--dump-single-json`.
- Pros: zero new NuGet deps; full control over args; consistent with how we already
  call ffmpeg.
- Cons: we own JSON parsing, error handling, and arg-escaping.

**Recommendation:** prototype with **Option A** for speed; keep **Option B** in
back pocket if the wrapper's process model fights our incremental-search UX.

---

## ⚖️ Concern-by-concern migration map

### 1. Stream resolution & selection (core of the scrubbing goal)
- Replace `Videos.Streams.GetManifestAsync` with a yt-dlp `--dump-json` call that
  returns the `formats[]` array (has `vcodec`, `acodec`, `height`, `tbr`/`abr`,
  `ext`, `protocol`, `url`, `filesize`).
- Re-implement `StreamSelector` against a yt-dlp format DTO. The existing policy
  (height cap by `VideoQualityPreference`, stereo bitrate ceiling of 192 kbps)
  maps directly onto `height` and `abr`.
- **Watch out:** yt-dlp direct `url`s are time-limited and IP-bound. For *live*
  playback into VLC (`BackglassWindow`) this is fine (short-lived), but they must
  be freshly resolved each play — no long-term caching of URLs.

### 2. Live playback → VLC (`BackglassWindow`)
- Keep the video-only + audio-slave approach; feed yt-dlp-resolved URLs.
- Alternatively, let yt-dlp pick the format and hand VLC a single URL. Prototype
  both; measure which VLC seeks better before/after cache switch.
- **Scrubbing improvement lever:** consider biasing streaming (pre-cache) format
  selection toward containers VLC indexes better, and lean harder on the existing
  `SwitchToCachedFileAndSeek` path by making the cache/prefetch download faster.

### 3. Caching & prefetch (`VideoCache`, `PrefetchCache`)
- Replace `DownloadAsync` (video) + `DownloadAsync` (audio) + ffmpeg mux with a
  single yt-dlp invocation: `-f "bv*+ba/b" --remux-video mkv` (yt-dlp downloads
  both and remuxes via our bundled ffmpeg in one step).
- This likely **simplifies** `PrefetchCache` (drops the manual `MuxWithFfmpegAsync`
  two-file dance) and yields a seekable `.mkv` — exactly what the reliable-scrub
  path wants.
- Preserve clip-length caps, size caps, purge-on-shutdown, and chapter persistence.

### 4. Search (secondary goal — needs care, possible regression)
- yt-dlp supports `ytsearchN:<query>` and `--flat-playlist` for fast, id+title
  results without resolving each video.
- **Risk:** yt-dlp search is a batch process spawn, not an incremental HTTP stream.
  Today results trickle in via `IAsyncEnumerable` with live pagination; a naive
  yt-dlp port could feel slower / less responsive. Mitigations: stream yt-dlp
  stdout line-by-line (`--print`/`--dump-json` per result) into the same
  `IAsyncEnumerable` shape, or fetch pages with `ytsearchN`.
- **Decision point:** search may be the weakest part of a yt-dlp switch. See the
  three-way strategy comparison below — the choice hinges on whether streamed-stdout
  yt-dlp search feels responsive enough to avoid keeping YoutubeExplode at all.

### 4a. Strategy comparison: full vs hybrid vs hybrid-with-fallback
Search/discovery sits **upstream of everything**: free-text search, genre browsing
(`categories.json` search terms → `Search.GetVideosAsync`), AutoDJ, and
`playlist:` / `channel:` lookup all funnel through the same backend. That makes the
discovery backend a **single point of failure for the entire content-in pipeline**
(Plex and direct-id replay from history/saved-queue survive; YouTube discovery does
not). This reframes the hybrid decision as three options, not two:

| Strategy | Happy-path search UX | Resilience (SPOF) | Complexity |
|----------|----------------------|-------------------|------------|
| **Full yt-dlp** | Pays batch-spawn latency on every search | Best — one backend, one failure mode | Lowest |
| **Plain hybrid** (YT-Explode search, yt-dlp resolve/download) | Best — fast incremental | **Worst — YT-Explode search break kills all discovery**, even though playback is resilient | Medium (dual paths) |
| **Hybrid + yt-dlp fallback** | Best — fast incremental | Near-best — auto fail-over to yt-dlp search on error | Highest (dual paths + failover) |

**Key nuance:** YoutubeExplode's recurring breakages historically cluster on the
**stream cipher / nsig** (playback) path far more than on search/metadata (more
stable innertube endpoints). So a hybrid offloads the *historically most fragile*
job to yt-dlp and keeps the *historically more stable* job on YoutubeExplode — the
residual search SPOF is real but **smaller than today's all-in-one exposure**, where
search **and** playback both depend on YoutubeExplode.

**The mitigation that closes most of the gap — dormant yt-dlp search fallback:**
- Happy path: YoutubeExplode incremental search (good UX).
- On **exception** (parse/HTTP fault), auto-fail-over to yt-dlp `ytsearchN:` for the
  session. Fail over on error only — **not** on successful-but-empty results (that's
  a genuine "no matches"). If enumeration faults partway, fall back for the remainder.
- Because both sit behind `ISearchEngine`, failover is a swap, not a rewrite, and a
  **single fallback covers search, genre browse, AutoDJ, and playlist/channel** at
  once (all consume the same engine).
- Add a **"force yt-dlp search" toggle** (`AppSettings`) so the fallback path is
  exercised periodically and doesn't rot untested.

### 5. Playlists & channels
- `playlist:` and `channel:` queries map to yt-dlp playlist/channel URLs with
  `--flat-playlist --dump-json`. `PlaylistId.Parse` (URL/id normalization) needs a
  small replacement helper.

### 6. Metadata & chapters (clear win)
- `Videos.GetAsync` → yt-dlp JSON gives `duration`, `title`, `uploader`,
  `thumbnails`, and a structured **`chapters`** array.
- Could **retire** the description-scraping `ParseYouTubeChapters` for YouTube
  (keep it as a fallback). Chapter persistence to `VideoCache` stays the same.

### 7. Cross-cutting
- **Ship & update `yt-dlp.exe`:** add to `dependencies\` with a `csproj`
  `CopyToOutputDirectory` entry (mirror the `ffmpeg.exe` item). Decide on an
  update strategy (bundle-and-pin vs `yt-dlp -U` opt-in). Self-update is what keeps
  it working long-term.
- **Exception handling:** update the `App.xaml.cs` suppression that keys on the
  `"YoutubeExplode"` stack-trace string.
- **Latency:** every yt-dlp call is a process spawn (100s of ms). Fine for play /
  cache / prefetch; a concern for rapid search/typeahead. Consider reuse/pooling or
  the hybrid approach.
- **Threading:** `BackglassWindow` runs on its own thread/dispatcher — keep yt-dlp
  process calls async and off the UI thread (they already are).

---

## 🚧 Risks & tradeoffs

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Search UX regression (batch vs incremental) | Medium–High | Stream stdout into existing `IAsyncEnumerable`; or hybrid (keep YoutubeExplode for search) |
| **Hybrid search SPOF** — YT-Explode search break disables all YouTube discovery (search, genre browse, AutoDJ, playlist/channel), even though yt-dlp playback still works | **High (in plain hybrid)** | Dormant yt-dlp fallback behind `ISearchEngine`; fail over on exception (not empty results); "force yt-dlp search" toggle to keep the path tested |
| Process-spawn latency per call | Medium | Async everywhere; only for play/cache; pool if needed |
| Shipping + updating `yt-dlp.exe` | Medium | Bundle in `dependencies\`; add opt-in self-update |
| yt-dlp URLs are short-lived/IP-bound | Low–Medium | Resolve fresh per play; never persist URLs |
| Scrubbing may not improve without caching | High (it's the primary goal) | Set expectations: durable fix = faster cache/remux, not live seek |
| Binary size / AV false positives on `yt-dlp.exe` | Low | Document; pin known-good build |
| Two extraction backends during transition | Low | Split seams behind `AppSettings.VideoEngine` / `SearchEngine` enums + factory (see Architecture) |

---

## 🧱 Architecture: two seams (search engine + video engine)

Rather than one `IYouTubeBackend` god-interface, split along a line that **already
exists in the code flow**: the **video ID is the contract** between discovery and
playback. The search engine *produces* IDs (+ `Title`/`Author`/`ThumbnailUrl`/
`Duration` → `VideoItem`); the video engine *consumes* an ID and *produces* stream
URLs or downloaded files. Formalizing that handoff yields two small, independent
seams instead of one large one.

### `ISearchEngine` — discovery + metadata
- Surface: free-text search, `playlist:` / `channel:` lookup, playlist-by-id,
  metadata (`duration`, description → chapters).
- Call sites: concentrated in `JukeboxViewModel.cs` (`Search.GetVideosAsync`,
  `Search.GetPlaylistsAsync`, `Playlists.GetVideosAsync`, `Channels.*`,
  `Videos.GetAsync`).
- Today: **only** a `YoutubeExplodeSearchEngine`. yt-dlp (`ytsearchN:`,
  `--flat-playlist`) can be added later as a second implementation or dormant
  fallback (§4a) — but search is *not* required to move for the video engine to.

### `IVideoEngine` — stream resolution + download
- **Two output shapes**, because its consumers differ:
  - `ResolveStreamsAsync(id, quality, stereo) → StreamSet` — video-only + audio
    slave, or muxed URL — for live playback into VLC (`BackglassWindow`).
  - `DownloadAsync(id, quality, stereo, dest) → file` — downloaded + remuxed
    seekable file — for `VideoCache` / `PrefetchCache`.
- Both YoutubeExplode and yt-dlp implement both cleanly.
- Call sites: `Services/StreamSelector.cs`, `Caching/VideoCache.cs`,
  `Caching/PrefetchCache.cs`, `Windows/BackglassWindow.xaml.cs`.

### What this buys us
- **Incremental, switchable rollout:** build the yt-dlp *video* engine behind an
  `AppSettings.VideoEngine` enum (+ small factory), ship it behind a toggle, and
  **A/B it live against YoutubeExplode while search stays 100% untouched**.
- **Independent failure by construction:** search and video now fail separately —
  the structural version of last section's hybrid-resilience argument. yt-dlp video
  + YoutubeExplode search becomes just a *config combination*, not a special mode.
- **Switchability is nearly free:** interface + factory + enum. The real work
  (writing the yt-dlp implementation) is work we'd do regardless; the seam adds
  little.

### Design decisions to lock before coding
1. **`IVideoEngine` exposes both `ResolveStreams` and `Download`** (see two shapes
   above) — one method can't serve both live-URL and downloaded-file consumers.
2. **`StreamSelector` must go engine-neutral.** Its *policy* (height cap by
   `VideoQualityPreference`, 192 kbps stereo ceiling) is engine-agnostic; lift it off
   YoutubeExplode's `StreamManifest`/`*StreamInfo` onto a neutral format DTO that each
   engine maps its native formats into.
3. **Metadata/chapters ride with `ISearchEngine`** (`Videos.GetAsync` is discovery-
   adjacent). Subtlety: yt-dlp's *native structured* `chapters[]` come from the same
   `--dump-json` the **video** engine already makes — so "better chapters" is only
   realized when the metadata source is yt-dlp. The video engine can *optionally*
   backfill chapters when it already holds the JSON (deferrable optimization).
4. **Plex is orthogonal to both engines** — it plays via `VideoItem.StreamUrl`
   directly and touches neither. The engine abstraction is cleanly YouTube-scoped;
   Plex stays a third, untouched path.

---

## 🗺️ Proposed phased plan (future work — not started)

Each phase is independently shippable and measurable.

1. **Spike / feasibility (this branch).** Add `yt-dlp.exe` to `dependencies\`, wire
   a throwaway resolver that dumps JSON for one video id, and confirm VLC plays a
   yt-dlp-resolved URL. Prototype `Option A` vs `Option B`.
2. **Introduce the two seams (see Architecture above).** Define `IVideoEngine`
   (`ResolveStreams` + `Download`) and `ISearchEngine` (search/playlist/channel/
   metadata). Wrap current YoutubeExplode code as `YoutubeExplodeVideoEngine` +
   `YoutubeExplodeSearchEngine`, selected via an `AppSettings.VideoEngine` /
   `SearchEngine` enum + factory. Make `StreamSelector` engine-neutral. **No behavior
   change** — this is pure refactor to create the switch point.
3. **yt-dlp video engine — download path first (biggest, safest win).** Implement
   `YtDlpVideoEngine.Download` for `VideoCache`/`PrefetchCache` (`-f "bv*+ba/b"
   --remux-video mkv`). Gate behind `AppSettings.VideoEngine=YtDlp`. Measure
   cache/prefetch reliability + speed. Search stays on YoutubeExplode, untouched.
4. **yt-dlp video engine — live playback.** Implement `YtDlpVideoEngine.ResolveStreams`
   and route `BackglassWindow` through it. A/B test scrub reliability (streaming) and
   the cache-switch path against the YoutubeExplode engine via the toggle.
5. **Metadata & native chapters.** Add yt-dlp as a metadata source in `ISearchEngine`
   (or backfill from the video engine's JSON per §6 subtlety); swap `Videos.GetAsync`
   chapter parsing for yt-dlp's `chapters[]`, keeping description parsing as fallback.
6. **Second search engine (optional / last, riskiest).** Add `YtDlpSearchEngine`
   and validate the incremental UX. If YoutubeExplode search is the weak link, wire
   **hybrid + dormant fallback** (§4a): YoutubeExplode happy path, auto fail-over to
   yt-dlp `ytsearchN:` on exception, plus a "force yt-dlp search" toggle to keep it
   exercised.
7. **Cutover & cleanup.** Set default engines, update `App.xaml.cs` suppression, prune
   YoutubeExplode from whichever seam it's fully replaced in, refresh
   `AGENTS.md`/`README.md`.

---

## ❓ Open questions (need decisions before Phase 2)

1. **Strategy (full vs hybrid vs hybrid+fallback):** given discovery is a SPOF for
   all YouTube content-in, do we go full yt-dlp (simplest resilience, slower search),
   plain hybrid (best search UX, unmitigated search SPOF), or hybrid + dormant yt-dlp
   search fallback (best UX, near-zero SPOF, most code)? See §4a. Leaning toward
   hybrid + fallback unless streamed-stdout yt-dlp search proves responsive enough.
2. **yt-dlp update policy:** pin a bundled build, or ship opt-in `-U` self-update?
3. **Wrapper vs direct process:** adopt `YoutubeDLSharp`, or hand-roll like the
   existing ffmpeg invocation?
4. **Scrubbing bar:** is "faster/more-reliable cache switch" an acceptable
   interpretation of "better scrubbing," given live DASH seek can't be fully fixed?
5. **Rollback:** keep both backends behind `AppSettings.UseYtDlp` for a release, or
   commit to a hard cutover?
6. **Distribution/legal:** confirm bundling `yt-dlp.exe` is acceptable for the
   project's distribution model.

---

## 🔎 Fast facts gathered during this review

- YoutubeExplode current version in `Phosphor.csproj`: **6.6.0** (also the latest on
  nuget.org — we are not behind, so this is about *resilience/features*, not a
  version bump).
- `YoutubeDLSharp` latest on nuget.org: **1.2.0** (viable Option A wrapper).
- `ffmpeg.exe` is already bundled and invoked (`PrefetchCache.MuxWithFfmpegAsync`),
  so yt-dlp's remux step needs no new media dependency.
- The seek/scrub recovery logic and the live→cached hand-off already exist in
  `BackglassWindow.OnSeekRequested` / `SwitchToCachedFileAndSeek` — a yt-dlp switch
  should **reuse** this machinery, not replace it.

---

## 📐 Appendix A — Proposed interface / DTO sketch (illustrative, not final)

Signatures below are a **design sketch** to pressure-test the two-seam idea against
the real call sites. Grounded in existing types: `VideoQualityPreference`
(`Low`/`Medium`/`High`/`Max`), `ChapterMarker` (`Title`/`StartTime`/`EndTime`), and
`VideoItem` (`Title`/`Author`/`ThumbnailUrl`/`VideoId`/`Duration`/`Chapters`). Not
committed as code — just the shape.

### Neutral format DTO (replaces YoutubeExplode `*StreamInfo` in `StreamSelector`)
Only the fields the current policy + overlay actually use: height (quality cap),
bitrate (stereo ceiling + tie-break), codec/container, and the resolvable URL. The
video overlay only needs `Width`×`Height` off the selected stream (fps + codec come
from the VLC player at runtime, see `StartVideoInfoPolling`).

```csharp
public enum MediaKind { VideoOnly, AudioOnly, Muxed }

public sealed record MediaFormat(
    string Url,            // resolvable stream URL (live) — short-lived, resolve fresh
    MediaKind Kind,
    int Width,             // 0 for audio-only
    int Height,            // 0 for audio-only; drives VideoQualityPreference cap
    long BitrateBps,       // drives the 192 kbps stereo ceiling + tie-break
    string VideoCodec,     // "" for audio-only
    string AudioCodec,     // "" for video-only
    string Container);     // e.g. "mp4", "webm", "m4a"
```

### `StreamSelector` goes engine-neutral (policy unchanged)
```csharp
// Same policy as today (height cap by preference; 192 kbps stereo ceiling),
// just operating on the neutral DTO instead of YoutubeExplode types.
public static class StreamSelector
{
    public static MediaFormat? SelectVideo(IReadOnlyList<MediaFormat> formats, VideoQualityPreference pref);
    public static MediaFormat? SelectAudio(IReadOnlyList<MediaFormat> formats, bool preferStereo = false);
    public static MediaFormat? SelectMuxed(IReadOnlyList<MediaFormat> formats, VideoQualityPreference pref);
}
```

### `IVideoEngine` — the two output shapes (live URLs + downloaded file)
```csharp
// What live playback feeds VLC: a primary URL + optional audio slave, OR a muxed URL.
public sealed record StreamSet(
    MediaFormat Primary,          // video-only (with AudioSlave) or muxed
    MediaFormat? AudioSlave);     // null when Primary is muxed

public interface IVideoEngine
{
    // BackglassWindow live playback path (L746–783). Resolve fresh each play.
    Task<StreamSet?> ResolveStreamsAsync(
        string videoId, VideoQualityPreference quality, bool preferStereo,
        CancellationToken ct = default);

    // VideoCache / PrefetchCache path. Returns the final seekable file (remuxed .mkv).
    // yt-dlp does download+remux in one shot; YoutubeExplode impl keeps the current
    // two-stream DownloadAsync + ffmpeg mux internally.
    Task<DownloadedMedia?> DownloadAsync(
        string videoId, VideoQualityPreference quality, bool preferStereo,
        string destinationDir, IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public sealed record DownloadedMedia(
    string FilePath,       // seekable local file
    string Resolution,     // "1920x1080" for the overlay/cache metadata
    IReadOnlyList<ChapterMarker>? Chapters); // yt-dlp can fill natively; YT-Explode null
```

### `ISearchEngine` — discovery + metadata (incremental preserved)
```csharp
public interface ISearchEngine
{
    // Incremental — preserves today's IAsyncEnumerable pagination UX (JukeboxViewModel).
    IAsyncEnumerable<VideoItem> SearchVideosAsync(string query, CancellationToken ct = default);
    IAsyncEnumerable<VideoItem> GetPlaylistVideosAsync(string playlistIdOrUrl, CancellationToken ct = default);
    IAsyncEnumerable<VideoItem> GetChannelUploadsAsync(string handleOrUser, CancellationToken ct = default);

    // Playlist search-by-name (the playlist: prefix flow) — first match's id.
    Task<string?> ResolvePlaylistIdAsync(string nameOrIdOrUrl, CancellationToken ct = default);

    // Replaces Videos.GetAsync: duration + chapters (native for yt-dlp, description-
    // parsed for YoutubeExplode). Rides here because it is discovery-adjacent.
    Task<VideoMetadata?> GetMetadataAsync(string videoId, CancellationToken ct = default);
}

public sealed record VideoMetadata(
    TimeSpan? Duration,
    string? Description,
    IReadOnlyList<ChapterMarker>? Chapters);
```

### Selection: enums + factory (the switch point)
```csharp
public enum VideoEngineKind  { YoutubeExplode, YtDlp }
public enum SearchEngineKind { YoutubeExplode, YtDlp }

// AppSettings additions (default = YoutubeExplode → zero behavior change on upgrade):
//   public VideoEngineKind  VideoEngine  { get; set; } = VideoEngineKind.YoutubeExplode;
//   public SearchEngineKind SearchEngine { get; set; } = SearchEngineKind.YoutubeExplode;

public static class EngineFactory
{
    public static IVideoEngine  CreateVideo(VideoEngineKind kind, /* http, ytDlpPath */ ...);
    public static ISearchEngine CreateSearch(SearchEngineKind kind, /* http, ytDlpPath */ ...);
}
```

### How each maps to current call sites (sanity check)
| Current call | New seam | Notes |
|--------------|----------|-------|
| `Videos.Streams.GetManifestAsync` + `StreamSelector.Select*` (BackglassWindow) | `IVideoEngine.ResolveStreamsAsync` | Returns `StreamSet`; caller keeps the `AddSlave` / muxed-fallback branching |
| `GetManifestAsync` + `DownloadAsync` ×2 + ffmpeg (VideoCache/PrefetchCache) | `IVideoEngine.DownloadAsync` | yt-dlp collapses to one call; YT-Explode impl keeps current internals |
| `Search.GetVideosAsync` / `AsVideos` (JukeboxViewModel) | `ISearchEngine.SearchVideosAsync` | Mapping to `VideoItem` moves *inside* the engine |
| `Search.GetPlaylistsAsync` + `PlaylistId.Parse` | `ISearchEngine.ResolvePlaylistIdAsync` | Absorbs URL/id normalization |
| `Playlists.GetVideosAsync` / `Channels.*` | `Get{Playlist,Channel}…Async` | Same incremental shape |
| `Videos.GetAsync` (duration + chapters) | `ISearchEngine.GetMetadataAsync` | Native yt-dlp chapters land here |

### Open modeling questions for the sketch
- **Duration filter / genre browse** currently consume `IVideo` mid-pipeline
  (`FilterVideosAsync`). If `SearchVideosAsync` yields `VideoItem` directly, that
  filter moves to operate on `VideoItem.Title` — trivial, but confirm nothing else
  reads richer `IVideo` fields.
- **Chapter source coupling:** `DownloadedMedia.Chapters` (video engine) vs
  `VideoMetadata.Chapters` (search engine) can both be populated by yt-dlp. Decide
  which is authoritative when both engines are yt-dlp (avoid double-fetching the
  same `--dump-json`).
