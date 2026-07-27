# Phosphor — Multiple Simultaneous Players Analysis

Exploratory analysis / feasibility + phasing for allowing **two media items to play
at once** — e.g. the **Backglass** playing a music video while the **Topper** plays a
second item (music-only, video, or ambience).

> **STATUS (analysis / not started).** This document is a design and phasing plan.
> No code has been written yet. The intent is to do the hard, non-user-visible
> refactor first (**Phase 0**) so the later feature work is a wiring exercise rather
> than a rewrite.

---

## 🎯 Goal

The **DMD window** is the main controller — users browse, search, play items, and
manage the queue there. The currently-playing item shows in the **now playing**
section, but the actual jukebox playback happens on the **Backglass window**.

The proposal: let the user run a **second media player** on the **Topper** window
(which already has a very similar structure to the Backglass). One player might play
a music video; the other might play music-only or an ambience video.

User-facing shape (target end state, not Phase 0):

- A settings checkbox (in the **Topper** section) — **"Enable second media player"** —
  turns the feature on.
- The DMD **now playing** bar is effectively **duplicated**: one bar for the Backglass
  player, one for the Topper player, with a way to mark one **active** so newly-played
  items target it.
- **Queue** stays bound to Player 1 for the first pass; tabbed dual queues come later.

### Conventions decided up front

- **Backglass = Player 1 = primary.** It always drives the **audio-reactive** visuals,
  by convention. A second player never becomes the reactive driver.
- **Audio mixing is the user's job** via per-player volume — each player exposes its own
  audio level so the user can balance the mix.
- **Audio-only per player.** There is a real use case for the **Topper as audio-only**
  while the video stays on the Pinup popper (or another chosen source). This already
  works on the Backglass today via the **"audio only"** setting; it should be exposed
  per-player.
- **Pinup / DOF are not a blocker.** `PinupSyncCoordinator` coordinates *popper*
  (Pinup) video playback across screens; it is orthogonal to jukebox playback. Jukebox
  playback on the Topper just needs to cleanly take over from / yield back to the
  ambient/Pinup mode.

---

## 🔍 How playback works today (single-player broadcast model)

Everything is centered on a single `JukeboxViewModel`:

- The VM owns **one** set of "now playing" state: `CurrentlyPlaying`, `Queue`,
  `PlaybackPosition`, `PlaybackDuration`, chapters, plus command events
  (`PlayRequested`, `StopRequested`, `PauseRequested`, `ResumeRequested`,
  `SeekRequested`, `VolumeChanged`).
- `BackglassWindow.AttachViewModel(vm)` subscribes to those events and does the real
  jukebox playback: stream resolution, gapless audio, seek/verify, chapters,
  live-stream clock, cache prefetch. A `DispatcherTimer` writes position/duration
  **back** into the VM.
- The **Topper already runs on its own STA thread** (`TopperProxy`, mirroring
  `BackglassProxy`) and **already owns a dedicated LibVLC MediaPlayer**
  (`TopperWindow.Ambient.cs`) — separate from the Backglass's shared VLC instance.
  But today it only does **ambient** content (static image, single video, folder
  rotation, Pinup playlist sync). It has **no jukebox pipeline** (no stream
  resolution, no queue, no chapters/seek).

### Why this is the right foundation

The two hardest prerequisites already exist:

1. **Thread isolation** — Topper has its own STA thread + proxy, like the Backglass.
2. **Independent media engine** — Topper owns a separate VLC player, so two
   simultaneous streams won't fight over one player instance.

### What blocks a true second player

**Playback state is a singleton on the VM**, and the Backglass's ~2700-line playback
logic is **woven into `BackglassWindow`'s view concerns**. `OnPlayRequested`
interleaves:

- **Pure playback logic** — stream resolution, gapless priming, seek/verify, chapters,
  live-stream clock, cache prefetch, position write-back.
- **View concerns** — `HideIdleForJukeboxVideo()`, `ShowIdleBackground()`,
  `DetachVideoView()`, the blob `_colorTimer`, logo dim/morph, expand-button reveal.

Playback fields (`_playCts`, `_gaplessPlayer`, `_usingGaplessPlayer`, `_nextMediaPlayer`,
`_lastVideoStreamUrl`, `_seekVerifyCts`, `_positionTimer`, `_audioOnly`, …) sit right
next to blob/logo/idle-overlay fields in the same class.

---

## ✅ Feasibility verdict

**Feasible, moderate effort.** The thread isolation and the second media engine are
already in place. The real work is a **structural refactor**: extract playback so it is
(a) not a singleton on the VM and (b) not fused to the Backglass's specific idle/logo
visuals — so a second consumer (Topper) can drive the playback half without inheriting
Backglass-only view behavior.

---

## 🧱 Phase 0 — Non-surfacing refactor (the hard part, zero user-visible change)

**Guiding constraint:** zero user-visible change. Backglass remains the only player,
still drives audio-reactive by convention, and behaves identically. Each step is
validated against current behavior before moving on.

### Step 1 — Define the seam (interfaces only, no logic moved)

Introduce abstractions in a new `Phosphor/Playback/` folder:

- `IPlaybackHost` — what the playback engine needs **from** its window: attach/detach
  video view, show/hide the media surface, signal "entering media mode" / "returning
  to idle", report failures. Backglass implements it by delegating to existing
  `HideIdleForJukeboxVideo` / `ShowIdleBackground` / `DetachVideoView`.
- `IMediaPlayerEngine` (may stay concrete initially) — the
  play/stop/seek/pause/resume/volume surface plus position/duration/chapter callbacks.

No behavior moves yet; Backglass gains an adapter that forwards to its own methods.
Build + run, confirm identical.

### Step 2 — Extract playback state into a `JukeboxPlayer` component

Create `JukeboxPlayer` owning the **playback-only** fields/logic currently in
`BackglassWindow`: CTS, gapless players, last-stream context, seek verification,
position timer, chapter polling, live-stream clock, cache/prefetch handles. It talks to
the window only through `IPlaybackHost`.

Move method-by-method (`OnPlayRequested` playback core, `OnSeekRequested`, gapless
prime/dispose, `StartVideoInfoPolling`), leaving thin forwarders in `BackglassWindow`
at first so nothing else changes. Validate after each move: build + manual
play/seek/gapless/live-stream pass.

### Step 3 — Route now-playing state through a `PlayerContext`

Extract the VM's single-player state (`CurrentlyPlaying`, `Queue`,
`PlaybackPosition`/`Duration`, chapters, `IsPaused`/`IsPlaying`, and the command events
`PlayRequested`/`Stop`/`Pause`/`Resume`/`Seek`/`VolumeChanged`) into a `PlayerContext`.

The VM keeps a single `Player1` instance and **re-exposes the existing property/event
names as pass-throughs to `Player1`** so all current XAML bindings and the Backglass's
`AttachViewModel` subscriptions keep working unchanged.

> ⚠️ **Highest regression risk.** Every existing XAML binding and the Backglass event
> subscription depend on the current VM surface. Keep the public VM surface
> byte-for-byte compatible. Do this as its **own commit** with a dedicated validation
> pass, separate from the `JukeboxPlayer` extraction, so a break can be bisected
> cleanly.

### Step 4 — Make `JukeboxPlayer` consume a `PlayerContext`

Wire `JukeboxPlayer` to read commands from / write position back to a `PlayerContext`
rather than the VM directly. After this the path is:
`PlayerContext (Player1) → JukeboxPlayer → BackglassWindow (IPlaybackHost)`.
Still one player, still identical behavior.

### Step 5 — Centralize the audio-reactive convention

Verify `AudioReactiveService` is fed by `Player1`/Backglass **explicitly** (not
incidentally), and enforce that convention in one place so a future Player 2 never
becomes the reactive driver. No behavior change — just making the convention
intentional.

### Step 6 — Validation pass

Full manual matrix on the Backglass only: YouTube stream, cached/prefetched, gapless
audio transition, seek forward/back + verify, live stream (SiriusXM), chapters,
pause/resume, volume, `audio-only` toggle, Pinup ambient handoff. Build clean, commit
as a self-contained "Phase 0: playback extraction (no functional change)."

### Phase 0 exit criteria

- `JukeboxPlayer` + `PlayerContext` exist and fully drive the Backglass.
- `BackglassWindow` implements `IPlaybackHost`; its playback fields are gone (delegated).
- VM's public binding/event surface is unchanged.
- Nothing new is visible to the user; no new settings.

---

## 🪶 Later phases (lightweight sketches)

### Phase 1 — Second player (video/audio on Topper); queue stays on Player 1

- VM gains `Player2` (a second `PlayerContext`) + an `ActivePlayer` pointer; "play item"
  routes to the active context.
- Give `TopperWindow` a `JukeboxPlayer` driven by `Player2`, with a Topper-flavored
  `IPlaybackHost` (its ambient engine already exists, so this is mostly wiring the
  jukebox surface alongside it).
- Settings: **"Enable second media player"** checkbox in the Topper section gates the
  whole thing.
- UI: duplicate the now-playing bar — one bound to `Player1`, one to `Player2`, with an
  active-target selector. Queue stays bound to `Player1`.
- **Audio-only reuse:** the existing `SetAudioOnly` path is exactly the "Topper as
  audio, keep video on popper/backglass" case — expose it per-player so Player 2 can run
  audio-only while Pinup/popper owns the video.
- Convention locked in Phase 0: Backglass (`Player1`) always drives audio-reactive.

### Phase 2 — Polish

- Tabbed dual queues (`Queue` moves into `PlayerContext`; add a second — low risk after
  Phase 0).
- Per-player volume sliders surfaced together so users balance the mix.
- Optional per-player "audio-only / ambience vs video" defaults.

---

## ⚠️ Watch-outs

- **Audio mixing:** two simultaneous streams both produce sound. Per-player volume is
  the answer; consider an audio-only/muted-ambience default for Player 2.
- **Audio-reactive visuals:** `AudioReactiveService` currently keys off the one player.
  Convention: Backglass (Player 1) is always the driver.
- **DOF / Pinup sync:** the Topper's Pinup playlist mode is externally driven by the
  DMD's `PinupSyncCoordinator`. Jukebox playback on the Topper must cleanly take over
  from / yield back to that ambient mode.
- **Resource load:** two LibVLC pipelines + prefetch caches roughly doubles CPU/GPU/
  network for playback.

---

## 📁 Key files (current architecture)

- `Phosphor/JukeboxViewModel.cs` — single-player now-playing state + command events
  (`CurrentlyPlaying`, `Queue`, `PlayRequested`, `SeekRequested`, …).
- `Phosphor/Windows/BackglassWindow.xaml.cs` — the real jukebox playback pipeline
  (~2700 lines) fused with idle/logo/blob view concerns; `AttachViewModel` subscribes
  to the VM events.
- `Phosphor/Windows/BackglassProxy.cs` — thread-safe proxy (own STA thread).
- `Phosphor/Windows/TopperWindow.xaml.cs` + `TopperWindow.Ambient.cs` — Topper on its
  own STA thread with a **dedicated** LibVLC ambient player (no jukebox pipeline yet).
- `Phosphor/Windows/TopperProxy.cs` — Topper's thread-safe proxy.
- `Phosphor/Visuals/AudioReactiveService.cs` — audio-reactive driver (Player 1 only, by
  convention).
- `Phosphor/App.xaml.cs` — creates Backglass/Topper on their own threads and wires the VM.
