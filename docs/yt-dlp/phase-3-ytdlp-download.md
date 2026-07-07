# Phase 3 — yt-dlp Video Engine: Download Path ✅ DONE

**Status:** Complete. Commit `<pending>`. **Prereq:** Phase 2 complete.
**Goal:** Implement `YtDlpVideoEngine` and make the **cache/prefetch download** path
use it when `AppSettings.VideoEngine == YtDlp`. Biggest, safest win (offline, measurable).

## Decision — seam unchanged ("Plan X"), one-shot remux deferred ("Plan Y")
The open question (return finished `.mkv` vs. raw files) was resolved in favor of
**keeping the seam contract identical**: `DownloadStreamsAsync` returns raw video +
audio files and the caches mux them exactly as before. Rationale: zero interface/caller
change, lowest risk, and the caches' existing chapters-XML + index + eviction logic is
untouched. The one-shot `--remux-video mkv` optimization (return a finished `.mkv`,
skip the caller's mux) is recorded as a **future optimization**, revisit if the double
process spawn or the caches' mux proves costly.

## What was delivered
- `Phosphor/Video/YtDlpVideoEngine.cs : IVideoEngine`.
  - `DownloadStreamsAsync`: two yt-dlp invocations — video-only `bv*[height<=N]`
    (N per `VideoQualityPreference`; Max = no cap) and audio-only
    `ba[audio_channels<=2]/ba` (stereo) or `ba`. Uses
    `-o "<dir>/%(id)s_{video,audio}.%(ext)s" --print after_move:filepath --no-simulate`
    to download **and** capture the exact final path. Returns `VideoDownload`.
  - `ResolveStreamsAsync` (live) **delegates to `YoutubeExplodeVideoEngine`** — native
    `-g` resolution is Phase 4.
  - `ResolveYtDlpPath` locates `dependencies/yt-dlp.exe` next to the app (PATH fallback).
- `VideoEngineFactory`: `YtDlp` → `YtDlpVideoEngine`.
- Deleted `Phosphor/YtDlp/YtDlpSpike.cs` + empty folder.

## Validation done
- Real terminal downloads (jNQXAC9IVRw): separate video (mp4) + audio (webm) files;
  `--print after_move:filepath` returns exact paths; resolution print = `320x240`.
- Full build green; `yt-dlp.exe` copied to `bin/.../net8.0-windows`; factory routes
  correctly; default engine (YoutubeExplode) untouched.

## Not covered (deferred)
- **Live A/B scrub-reliability measurement** needs the native live path — that's Phase 4.
  With YtDlp today, downloads are yt-dlp but live playback is still YoutubeExplode.
- One-shot remux optimization (Plan Y) — see decision above.

## Open questions for later
- Should preemptive/prefetch caching parallelize the two yt-dlp spawns? (Currently
  sequential video→audio; fine for background, measure if it matters.)
