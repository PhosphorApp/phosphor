# Phase 2 — `IVideoEngine` Seam (no behavior change) 🚧

**Status:** In progress.
**Goal:** Introduce a video-engine abstraction and route the three YouTube *video*
call sites through it, wrapping today's YoutubeExplode logic. **Zero behavior change** —
default engine is YoutubeExplode; no yt-dlp code runs yet.

> This phase is a **pure refactor to create a switch point.** If anything changes
> observable behavior, it's a bug.

---

## Scope: the three video call sites (today)

All three currently use `new YoutubeClient()` + `_youtube.Videos.Streams.*` + `StreamSelector`:

1. **Live playback** — `Windows/BackglassWindow.xaml.cs` (~L746 `OnPlayRequested`):
   `GetManifestAsync` → `StreamSelector.SelectAudio/SelectVideo/SelectMuxed` →
   builds `Media` (+ `AddSlave` for audio) → `_mediaPlayer.Play`. Also sets
   `_lastVideoStreamUrl/_lastAudioStreamUrl/_lastMuxedStreamUrl` and `infoForOverlay`.
2. **Persistent cache** — `Caching/VideoCache.cs` (~L111 `CacheVideoAsync`):
   `GetManifestAsync` → select video+audio → `DownloadAsync` ×2 → ffmpeg mux to `.mkv`
   (keeps chapters XML, index, eviction).
3. **Prefetch cache** — `Caching/PrefetchCache.cs` (~L83 `PrefetchAsync`):
   `GetManifestAsync` → select video+audio → `DownloadAsync` ×2 → ffmpeg mux to `.mkv`.

`StreamSelector` (Services/StreamSelector.cs) is shared by all three.

---

## Design

### Interface (`Phosphor/Video/IVideoEngine.cs`)
```csharp
public interface IVideoEngine
{
	// Live playback: resolve short-lived playable URLs for VLC. Resolve fresh per play.
	Task<VideoStreams?> ResolveStreamsAsync(
		string videoId, VideoQualityPreference quality, bool preferStereo,
		bool audioOnly, CancellationToken ct = default);

	// Cache/prefetch: download raw video+audio files into destinationDir.
	// Returns the raw file paths + resolution; the CALLER still muxes (keeps existing
	// mux/index/eviction). yt-dlp one-shot remux is layered in at Phase 3.
	Task<VideoDownload?> DownloadStreamsAsync(
		string videoId, VideoQualityPreference quality, bool preferStereo,
		string destinationDir, CancellationToken ct = default);
}
```

### DTOs
```csharp
public enum VideoStreamKind { SeparateVideoAudio, Muxed, AudioOnly }

public sealed record VideoStreams(
	VideoStreamKind Kind,
	string PrimaryUrl,       // video-only, muxed, or audio-only URL
	string? AudioSlaveUrl,   // set only for SeparateVideoAudio
	string Resolution);      // "WxH" for overlay; "" for audio-only

public sealed record VideoDownload(
	string VideoFilePath,    // raw downloaded video (or muxed) file
	string AudioFilePath,    // raw downloaded audio file ("" if muxed/none)
	string VideoContainer,   // e.g. "webm"/"mp4"
	string AudioContainer,   // e.g. "webm"/"m4a"
	string Resolution);      // "WxH"
```

### `YoutubeExplodeVideoEngine` (`Phosphor/Video/YoutubeExplodeVideoEngine.cs`)
- Owns a `YoutubeClient` (default, no custom timeout — matches today).
- Keeps `StreamSelector` as an internal detail (do **not** force it neutral).
- `ResolveStreamsAsync`: lifts the exact branch logic from BackglassWindow
  (audio-only → audio URL; else video+audio → primary+slave; else muxed fallback).
  Returns `null` when nothing resolves (caller handles as today).
- `DownloadStreamsAsync`: lifts the manifest+select+`DownloadAsync ×2` from the caches,
  writing `{videoId}_video.{ext}` / `{videoId}_audio.{ext}` into `destinationDir`.
  Returns paths + resolution; caller does the ffmpeg mux exactly as now.

### Selection + settings
- `enum VideoEngineKind { YoutubeExplode, YtDlp }` (in AppSettings.cs with the others).
- `AppSettings.VideoEngine { get; set; } = VideoEngineKind.YoutubeExplode;` (+ default_settings.json if that file enumerates props — check).
- `VideoEngineFactory.Create(VideoEngineKind)` → `IVideoEngine`.

### Wiring
- `JukeboxViewModel`: add `public IVideoEngine VideoEngine { get; private set; }`,
  built from `AppSettings.VideoEngine` during setup. Pass into `VideoCache`/`PrefetchCache`
  (constructor or method param). `BackglassWindow` reads `vm.VideoEngine` in `OnPlayRequested`.
- Remove `BackglassWindow._youtube` (only used at L746) and each cache's `_youtube`
  once the engine owns resolution — **or** keep the field but have it delegate; prefer
  removal for clarity. Search still uses its own `YoutubeClient` in the VM — untouched.

---

## Steps (mirror of the registered plan, steps 3–11)
3. Create `IVideoEngine` + DTOs.
4. Implement `YoutubeExplodeVideoEngine` (lift resolve + download logic).
5. Add `VideoEngineKind`, `VideoEngineFactory`, `AppSettings.VideoEngine`.
6. Wire VM: build engine, pass to caches, expose to windows.
7. Route `VideoCache` download through engine.
8. Route `PrefetchCache` download through engine.
9. Route `BackglassWindow` live resolve through engine; overlay via resolution string.
10. Build + validate no behavior change.
11. Update tracker ledger; commit locally.

## Resume-from notes
- If interrupted mid-phase: check which of `Phosphor/Video/*.cs` exist and whether the
  three call sites still reference `_youtube.Videos.Streams`. The call sites are the
  last things to change (steps 7–9); the new files (steps 3–5) are additive and safe.
- Validation gate: **full `run_build` must pass**; behavior must be identical (engine
  defaults to YoutubeExplode).

## Acceptance criteria
- [x] Build green.
- [x] All three call sites go through `IVideoEngine`.
- [x] Default `VideoEngine = YoutubeExplode`; app plays/caches/prefetches identically.
- [x] No yt-dlp process is spawned by normal operation.

## Outcome (as built)
- Added `Phosphor/Video/{IVideoEngine,YoutubeExplodeVideoEngine,VideoEngineFactory}.cs`.
- `AppSettings.VideoEngine` (+ `VideoEngineKind`) default YoutubeExplode; `default_settings.json` updated.
- VM owns `IVideoEngine` (`VideoEngine` prop, `SetVideoEngine`), seeds caches; wired at
  `App.xaml.cs` (startup) and `DmdWindow` (settings-change).
- `VideoCache` / `PrefetchCache` download via `DownloadStreamsAsync`; `BackglassWindow`
  live-resolves via `ResolveStreamsAsync`. `StreamSelector` + `Videos.Streams.*` now
  isolated in `YoutubeExplodeVideoEngine`.
- **Fidelity fix:** engine audio-only branch falls through to video when no audio
  stream exists (matches original `if (isAudioOnly && audioStream != null)`).
