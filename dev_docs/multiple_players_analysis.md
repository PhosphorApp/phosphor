# Phosphor — Multiple Simultaneous Players Analysis

Exploratory analysis / feasibility + phasing for allowing **two media items to play
at once** — e.g. the **Backglass** playing a music video while the **Topper** plays a
second item (music-only, video, or ambience).

> **STATUS (Phases 0–2 complete on branch `multiplayer`; Phase 3 not started).** Phase 0
> (engine/orchestration extraction), Phase 1 (real second player on the Topper), and Phase 2
> (per-player now-playing state + dual now-playing bars) are all done and validated. Several
> post-Phase-2 tweaks also landed (audio isolation, settings moves, full Topper bar, live
> enable/disable). The next chapter is **Phase 3 — per-player queues**. **Start a new session
> from the "Phase 3 kickoff" section below.**

---

## 🚀 Phase 3 kickoff — self-contained handoff (per-player queues) (start here)

> Written so a fresh session needs no other context. Skim the Phase 1 & 2 kickoff sections and
> the milestone sections for architecture; this section is the Phase 3 brief.

> **STATUS (Phase 3 Stage A complete on branch `multiplayer`).** Per-player queue *state* landed:
> a new `Phosphor/Playback/PlayerQueue.cs` holds `Queue`/`QueueIndex`/`LastKnownQueueIndex`/
> `CurrentQueueItem`/`RepeatEnabled`/`AutoDjEnabled`/`HasNextTrack`/`HasQueueItems` + `OwnerName` +
> per-queue persistence. `Player1` (Backglass → `queue.json`) and `Player2` (Topper →
> `queue_topper.json`) each own a `PlayerQueue`. The VM's queue members delegate to `Player1.Queue`
> (PropertyChanged forwarded via `WirePlayerQueue`), so the single-queue UI + persistence are
> byte-for-byte unchanged when the 2nd player is off. Navigation is player-parameterized
> (`PlayNowOn`/`PlayNextOn`/`PlayFromQueueIndexOn`) so a queue advance targets its **owning** player's
> context/engine; `Player2Skip`/`Player2Previous` now drive the Topper's own queue. The queue title
> shows the owner name ("(Backglass)"/"(Topper)") instead of the item count when the 2nd player is on.
> **Deferred to Stage B:** Player 2 Repeat/AutoDJ/Shuffle wiring; relocating AutoDJ-fill/prefetch/
> preemptive-cache bodies into `PlayerQueue`; Player 2 natural-end (PCM-callback) gapless advance;
> making `PlayTransitioning` per-player (currently a shared VM flag). **Stage B** is next: the DMD
> queue panel follows `ActivePlayer` (swap `ItemsSource` + commands) + "focus active queue" affordance.

> **POST-STAGE-A FIX:** `PlayTransitioning` is now per-player — it routes through the owning
> `PlayerContext` (`PlayNowOn` sets the target; `NotifyPlaybackStarted(context)` and the resolve-failure
> paths clear the hosting player) so each now-playing bar's loading spinner reflects only its own
> player. `VM.PlayTransitioning` delegates to `Player1`. This closes the Stage-A "make PlayTransitioning
> per-player" follow-up early.

> **STATUS (Phase 3 Stage B complete on branch `multiplayer`).** The single DMD queue panel now
> **follows `ActivePlayer`**. The VM exposes an active-queue projection (`ActiveQueue`,
> `ActiveCurrentQueueItem`, `ActiveQueueIndex`, `ActiveQueueCountText`, `HasActiveQueueItems`,
> `ActiveRepeatEnabled`, `ActiveAutoDjEnabled`) plus active-targeted commands (`ClearActiveQueue`,
> `ShuffleActiveQueue`, `RemoveFromActiveQueue`, `ToggleActiveRepeat`, `ToggleActiveAutoDj`,
> `PlayFromActiveQueueIndex`, `ToggleActiveQueueFocus`); `AddToQueue` targets the active player's queue.
> On an active-player switch the VM swaps a forwarding subscription (PropertyChanged +
> CollectionChanged) to the newly-active `PlayerQueue` and raises the active-* members so the panel
> refreshes. The queue command bodies are player-parameterized (`ClearQueueOn`/`ShuffleQueueOn`/
> `RemoveFromQueueOn`/`AutoDjFillQueueOn(player)`, with per-queue `IsAutoDjFilling`), so Repeat/AutoDJ/
> Shuffle are **per-queue** for whichever player is active. The DMD panel XAML + code-behind
> (double-click, drag-reorder, play-selected, nav ring, auto-hide, make-playlist, current-item
> scroll/highlight) all bind to the active-* surface. A header **"switch queue"** affordance (`⇄`,
> shown only when the 2nd player is enabled) flips the visible queue; the title shows the active owner
> name. The VM's Player-1-bound `Queue`/`QueueIndex`/`CurrentQueueItem`/`HasNextTrack`/
> `AdvanceQueueGapless`/`GetNextGaplessTrack` stay unchanged, so Backglass host/gapless/prefetch are
> unaffected.

> **STATUS (Phase 3 integration wrap-up complete on branch `multiplayer`).** The remaining deferrals
> are done: (1) **Player 2 natural-end advance** — the Topper's jukebox engine `EndReached` is now
> subscribed (`OnJukeboxMediaEnded` in `TopperWindow.PlaybackHost.cs`, wired once in
> `AttachJukeboxViewModel` / unwired in `DetachJukeboxViewModel`), advancing Player 2's own queue via
> `PlayNextOn(Player2)` (live-stream-guarded, yields to ambient when the queue finishes). The Topper's
> PCM-gapless `CreateGaplessPlayer` now advances Player 2's queue too (`TrackAdvanced` →
> `AdvanceQueueGaplessOn(Player2)`; `PlaybackFinished` → `PlayNextOn(Player2)` or ambient).
> (2) **Prefetch cursor** moved into `PlayerQueue.PrefetchingVideoId` (structurally per-queue);
> `AdvanceQueueGapless` is now player-parameterized (`AdvanceQueueGaplessOn(player)`). Public members
> still delegate to Player 1, so Backglass behavior is unchanged.
>
> **FOLLOWUP — concurrent-stream limits:** YouTube playback is typically gated to a *single* player
> instance; running the Backglass and Topper on YouTube simultaneously complicates/limits that.
> Persistent caching is a partial workaround (a cached Backglass track frees the live YouTube session
> for the Topper). Several other sources are also single-stream by nature. This is likely best handled
> with **UX verbiage/guidance** about concurrent-stream usage rather than a hard technical fix — TBD.

> **FOLLOWUP — VM delegation-bloat / tech debt (architecture assessment after Phase 3):** The domain
> abstractions landed well — `PlayerQueue` (per-player queue state + persistence), `PlayerContext`
> (per-player now-playing/transition/audio-only), and the player-parameterized `...On(player)` cores
> (`PlayNowOn`/`PlayNextOn`/`AdvanceQueueGaplessOn`/`ClearQueueOn`/`ShuffleQueueOn`/`RemoveFromQueueOn`/
> `AutoDjFillQueueOn`) mean the playback/queue *logic* now exists once and is targeted by parameter.
> **The cost:** `JukeboxViewModel` (~5.4k lines) now carries a heavy delegation surface — ~49
> `=> Player1.…` forwarders + ~45 `Active*` projection members — so queue state is exposed as **three
> overlapping views** (`Queue`/`QueueIndex`/… → Player 1 for host/gapless consumers; `Active*` → the
> panel's active player; `Player2*` → the Topper bar). It works and kept XAML/persistence byte-for-byte
> stable, but a reader must know which projection a member represents, and the forwarding plumbing
> (`WirePlayerQueue`, `SwapActiveQueueSubscription`, the PropertyChanged switch statements) all lives on
> the VM. Some cross-player state is deliberately shared and app-scoped, **by design, not as unfinished
> extraction**: `_history` (one chronological play log per cabinet — both players already write to the
> single `history.json`; a per-screen split would fragment a naturally-unified timeline for no user
> benefit) and `StatusText` (a single "what's happening" feedback line — a stack-of-1 where the most
> recent writer wins, bound to one DMD status label). The AutoDJ de-dup set is likewise one shared set
> across both queues (now inside `AutoDjService`). These are correct as shared app concerns; the only
> nicety left is *placement* (they live as VM fields rather than clearly app-scoped services).
>
> **Suggested cleanup (not urgent; no user-visible change):**
> 1. **Collapse the three queue views into one.** Make the host/gapless callers
>    (`BackglassWindow` `PlayNext`/`HasNextTrack`/`AdvanceQueueGapless`/`GetNextGaplessTrack`,
>    `JukeboxPlayer`) player-aware so the panel can bind everything through the `Active*` projection —
>    eliminating roughly half the `=> Player1` delegators.
> 2. **Move the forwarding plumbing off the VM** into a small `PlayerQueueViewModel` (or onto
>    `PlayerContext`/`PlayerQueue`) so the VM stops being the PropertyChanged switchboard.
> 3. **Extract a `QueueService` / AutoDJ engine** off the VM to start dismantling the God object
>    (own the shared `_autoDjUsedIds`/RNG/fill orchestration; take `PlayerQueue` as a parameter).
> Overall grade: solid **B+** — abstractions are right; the VM just needs a follow-up diet.

> **STATUS (VM delegation-bloat cleanup complete on branch `refactor/vm-queue-cleanup`).** All three
> suggested cleanups landed as build-validated, individually-committed slices:
> 1. **Collapsed the three queue views into one.** `BackglassWindow`'s host/gapless callers are now
>    player-aware (`GetNextGaplessTrackOn`/`AdvanceQueueGaplessOn`/`PlayNextOn(vm.Player1)`,
>    `vm.Player1.Queue.HasNextTrack`), mirroring the Topper. The nine now-unused Player-1-bound
>    forwarders (`Queue`-subset `QueueIndex`/`CurrentQueueItem`/`RepeatEnabled`/`AutoDjEnabled`/
>    `LastKnownQueueIndex`/`HasNextTrack`/`PlayNext()`/`AdvanceQueueGapless()`/`GetNextGaplessTrack()`)
>    were deleted; the DMD panel already bound through the `Active*` projection so XAML is untouched.
>    Internal VM + `App.xaml.cs` callers repoint to `Player1.Queue.*`. (commit `9a4123a`)
> 2. **Moved the forwarding plumbing off the VM.** The active-queue PropertyChanged/CollectionChanged
>    switchboard (`SwapActiveQueueSubscription`/`OnActiveQueue*`/`RaiseActiveQueueChanged`) now lives in
>    `Phosphor/Playback/ActiveQueueForwarder.cs`; the VM keeps thin `Active*` getters and delegates the
>    subscription bookkeeping. (commit `a1f252a`)
> 3. **Extracted the AutoDJ engine.** `Phosphor/Playback/AutoDjService.cs` owns the used-id de-dup set,
>    RNG, refill thresholds, and the genre/video fill bodies, operating on a `PlayerContext`/`PlayerQueue`
>    passed as a parameter. All VM couplings (status, source search binding `AutoDjProviderId`, UI
>    marshalling, next-track playback, active-genre resolution, history titles, exception logging) are
>    injected as delegates. The VM keeps thin `AutoDjFillQueueOn`/`AutoDjFillQueue` wrappers (call sites
>    unchanged); `ShuffleQueueOn` shares the engine RNG via `_autoDj.Shuffle`. (commit `a187125`)
>
> Net: `JukeboxViewModel` ~5.4k → ~5.15k lines; the `=> Player1` delegator count dropped and two
> cohesive concerns (active-queue forwarding, AutoDJ orchestration) moved to dedicated `Phosphor.Playback`
> types. Zero user-visible change — XAML bindings and `queue.json`/`queue_topper.json` persistence are
> byte-for-byte stable. Priority 3's original "risk" (deep VM coupling) collapsed cleanly into a small set
> of injectable delegates, so the God-object dismantling is genuinely under way rather than deferred.

**Where things stand (end of Phase 2 + tweaks):**
- `PlayerContext` (`Phosphor/Playback/PlayerContext.cs`) is the **per-player state holder**
  (`INotifyPropertyChanged`): `CurrentlyPlaying`, `PlaybackPosition/Duration`, `Volume`,
  `IsPaused`, `PlayTransitioning`, `AudioOnly`, chapters, and derived getters
  (`NowPlayingTitle`, `PlaybackTimeText`, `IsLiveStream`, `NowPlayingSourceText`). It also
  carries the command events (play/stop/pause/resume/seek/volume).
- The VM (`JukeboxViewModel`) owns `Player1` (Backglass) and `Player2` (Topper), plus
  `ActivePlayer`/`IsPlayer1Active`/`IsPlayer2Active`/`TargetTopperPlayer`. The VM's now-playing
  properties **delegate to `Player1`** (with `PropertyChanged` forwarding via
  `WirePlayerContext`), so the single-bar UI is unchanged when the 2nd player is off.
- `PlayNow` sets `ActivePlayer.CurrentlyPlaying` and raises `ActivePlayer` play; the main DMD
  transport (Play/Pause/Stop/Seek) is bound to **Player 1**; the Topper bar's transport is bound
  to `Player2*` commands (`Player2Play/Pause/Resume/Stop`, and `Player2Skip`/`Player2Previous`
  which are **currently stubs** awaiting per-player queues).
- Each player has its own `JukeboxPlayer` + `MediaEngine` + `IPlaybackHost`. The Topper's engine
  uses an **audio-isolated DirectSound LibVLC** (`MediaEngine.UseIsolatedAudioInstance()`) so
  per-window volume is real. The Topper's second player can be enabled/disabled **live** (App
  `SetSecondPlayerEnabled` → `TopperProxy.Attach/DetachViewModel`).
- The DMD has **two now-playing bars** (Player 1 + Player 2), click-to-activate, active-bar
  highlight + `[BACKGLASS]`/`[TOPPER]` label qualifiers; collapses to the single bar when the
  2nd player is disabled.

**Phase 3 goal:** give **each player its own queue**. Backglass (Player 1) keeps `queue.json`;
the Topper (Player 2) gets `queue_topper.json`. The queue UI panel stays a single panel that
**follows the active player** (swaps its bound queue when you switch active). AutoDJ / Repeat /
Shuffle become **per-queue**. Wire `Player2Skip`/`Player2Previous` to the Topper's queue.

**Locked decisions for Phase 3 (do not revisit):**
- **One queue panel that follows `ActivePlayer`** (swap `ItemsSource`/commands on active change),
  NOT two simultaneous queue panels. Optionally a "focus active queue" affordance later.
- **Per-queue** AutoDJ / Repeat / Shuffle (each player has its own).
- Backglass persists to `queue.json` (unchanged path); Topper persists to `queue_topper.json`.
- No settings migration / back-compat shims (testers only).

**The core refactor (this is the crux — the queue is the most VM-coupled subsystem, ~281
queue references):** extract a **`PlayerQueue`** object owning `Queue` (ObservableCollection),
`QueueIndex`/`CurrentQueueItem`/`LastKnownQueueIndex`, `RepeatEnabled`, `AutoDjEnabled`, the
prefetch/preemptive-cache cursor, and persistence (its own json path). Give `Player1` and
`Player2` each a `PlayerQueue`. Then relocate the queue-navigation methods
(`PlayNext`, `PlayFromQueueIndex`, `AdvanceQueueGapless`, `AddToQueue`, `RemoveFromQueue`,
`ClearQueue`, `ShuffleQueue`, AutoDJ fill, prefetch/preemptive-cache) to operate on a given
`PlayerQueue`. The VM's existing queue members become **delegators to `Player1`'s queue** (same
proven pattern as the Phase 2 now-playing delegation) so all current XAML bindings + persistence
keep working byte-for-byte when the 2nd player is off.

**Key files:** `Phosphor/JukeboxViewModel.cs` (queue state/nav/persistence — the bulk),
`Phosphor/Playback/PlayerContext.cs` (or a new `Phosphor/Playback/PlayerQueue.cs`),
`Phosphor/Windows/DmdWindow.xaml(.cs)` (queue panel bind-to-active + collapse logic in
`ApplyQueueCollapsedState`/`SetQueuePosition`), queue persistence (`queue.json` →
add `queue_topper.json`).

**Suggested Phase 3 stages (plan fresh in the new session; work in build-validated,
individually-committed slices):**
- **Stage A — per-player queue *state* (no UI change):** create `PlayerQueue`; give both players
  one; delegate the VM's queue members to `Player1`'s; add `queue_topper.json`; relocate queue
  navigation to operate on a `PlayerQueue`; wire `Player2Skip`/`Player2Previous` to the Topper's
  queue. *Validate: single-queue behavior identical; Topper advances its own queue via its bar.*
- **Stage B — queue UI follows the active player:** the DMD queue panel's `ItemsSource` +
  Clear/Shuffle/AutoDJ/Repeat commands bind to `ActivePlayer`'s queue; add the "focus active
  queue" affordance. *Validate: switching active swaps the visible queue; per-queue controls.*

**Watch-outs:**
- Queue navigation drives `CurrentlyPlaying` (now per-player), prefetch, and gapless priming —
  make sure the Topper's queue advance targets `Player2`'s context/engine, not Player 1's.
- `AdvanceQueueGapless` is called from the gapless PCM callback inside each host — it must
  advance the **owning** player's queue.
- Preemptive/prefetch cache is shared (one `VideoCache`/`PrefetchCache` on the VM) — that's fine;
  only the *cursor* (which next item) is per-queue.
- The DMD `CurrentQueueItem` highlight + auto-hide-when-empty logic assume one queue; rebind to
  the active queue in Stage B.

---


---

## 🚀 Phase 1 kickoff — self-contained handoff (start here)

> This section is written so a fresh session needs no other context. Read the milestone
> sections + "How playback works today" + "Later phases → Phase 1" below for detail.

**Where things stand:** `JukeboxPlayer` is a window-agnostic playback controller. Command
flow is:
```
VM → Player1 (PlayerContext) → JukeboxPlayer.Play/Stop/Seek
      ├─ MediaEngine     (VLC MediaPlayer + gapless + seek-verify + last-stream ctx + live clock)
      └─ IPlaybackHost   (the host window: video surface, idle visuals, timers, DMD notify)
```
`BackglassWindow` implements `IPlaybackHost` and owns one `JukeboxPlayer` (bound to
`vm.Player1` in `AttachViewModel`). The `MediaEngine` is owned by the `JukeboxPlayer`.
Everything is validated end-to-end on the Backglass and committed.

**Phase 1 goal:** a real second player on the **Topper** — one player plays a music video,
the other music-only/ambience. No duplicated playback logic; the Topper gets its own
`JukeboxPlayer` + `MediaEngine` + `IPlaybackHost`, driven by a second `PlayerContext`.

**Key files:** `Phosphor/Playback/{IPlaybackHost,JukeboxPlayer,PlayerContext,MediaEngine}.cs`,
`Phosphor/JukeboxViewModel.cs` (owns `Player1`), `Phosphor/Windows/BackglassWindow*.cs`
(reference `IPlaybackHost` implementation), `Phosphor/Windows/TopperWindow*.cs` +
`TopperWindow.Ambient.cs` (has its own STA thread + dedicated ambient VLC engine; needs a
jukebox `IPlaybackHost` implementation), `Phosphor/App.xaml.cs` (wires windows/VM).

**Locked conventions (do not revisit):**
- Backglass = **Player 1** = primary, and ALWAYS the audio-reactive driver (enforced in
  `DmdWindow.ApplyReactiveBlobs`). A second player never becomes the reactive driver.
- Audio mixing is the user's job via **per-player volume**.
- **Audio-only per player** (reuse the existing `SetAudioOnly` path for "Topper as audio,
  video on Pinup/popper").
- Queue stays bound to **Player 1** for the first pass (tabbed dual queues are Phase 2).
- **Option X** is the player↔model relationship: `JukeboxPlayer.Attach(vm)` stores the VM
  as `Model` for now-playing state + shared services. Phase 2 later moves per-player state
  into `PlayerContext`.

**Suggested Phase 1 steps (plan fresh in the new session):**
1. VM: add `Player2` (a second `PlayerContext`) + an `ActivePlayer` pointer; "play item"
   routes to the active context. Keep `Player1`'s public event surface unchanged.
2. `TopperWindow`: implement `IPlaybackHost` (its ambient engine already exists — this is
   the jukebox surface alongside it) and give it a `JukeboxPlayer` driven by `Player2`.
   Its `IPlaybackHost` maps EnterMediaMode/ReturnToIdle to "take over from / yield back to"
   its ambient mode (Pinup/folder/image/video), NOT the Backglass blob/logo visuals.
3. Settings: **"Enable second media player"** checkbox in the Topper section gates it.
4. UI: duplicate the DMD now-playing bar — one bound to `Player1`, one to `Player2`, with
   an active-target selector. Queue stays bound to `Player1`.
5. Per-player **audio-only** toggle (reuse `SetAudioOnly`).
6. Validate: Backglass + Topper playing two different items simultaneously; per-player
   volume balances the mix; Backglass still drives audio-reactive.

**Watch-outs:** two LibVLC pipelines double CPU/GPU/network; the Topper's Pinup playlist
mode is externally driven by `PinupSyncCoordinator` and jukebox playback must cleanly take
over / yield back; the `MediaEngine` currently adopts the app's shared `LibVLC` (both
players can share one `LibVLC` but MUST own separate `MediaPlayer`s — already the case).

---

## 🚧 Phase 0 progress (branch `multiplayer`)

Done so far (each step builds clean; VM public binding/event surface unchanged):

- **`Phosphor/Playback/IPlaybackHost.cs`** — the window-side seam (`EnterMediaMode`,
  `ReturnToIdle`, `DetachVideoView`, `ReportPlaybackFailed`).
- **`BackglassWindow.PlaybackHost.cs`** — `BackglassWindow` implements `IPlaybackHost`
  by forwarding to its existing `HideIdleForJukeboxVideo` / `ShowIdleBackground` /
  `DetachVideoView`; also hosts a lazily-created `JukeboxPlayer`.
- **`Phosphor/Playback/JukeboxPlayer.cs`** — engine component skeleton holding the
  `IPlaybackHost` seam and a `PlayerContext` (via `Attach`).
- **`Phosphor/Playback/PlayerContext.cs`** — the per-player command channel (play / stop /
  pause / resume / seek / volume). The VM now owns a single `Player1` and **re-exposes the
  existing `PlayRequested`/`StopRequested`/`PauseRequested`/`ResumeRequested`/
  `SeekRequested`/`VolumeChanged` events as pass-throughs to `Player1`** — all XAML
  bindings and the Backglass's `AttachViewModel` subscriptions keep working unchanged.
- **`DmdWindow.ApplyReactiveBlobs`** — documented the Backglass-drives-reactive
  convention at the single reactive-wiring choke point.

**Deferred (was Step 2/4 in the plan):** moving the ~2700-line playback *engine*
(fields `_playCts`, `_gaplessPlayer`, `_nextMediaPlayer`, seek-verify, position timer,
live clock, prefetch, `OnPlayRequested`/`OnSeekRequested`) out of `BackglassWindow` and
into `JukeboxPlayer`. That logic is tightly coupled to window internals (`_colorTimer`,
`EnsureVlcInitialized`/`_mediaPlayer`, `RootGrid`/`_videoView`, dispatcher, `DataContext`),
so a wholesale lift risks the zero-change guarantee. It is best done as its own
incremental increment (route methods through `JukeboxPlayer`/`IPlaybackHost` one at a
time) once the command-channel plumbing above is validated.

---

## ✅ MILESTONE — Phase 0.5: command routing fully player-agnostic (validated)

**Tag: `phase-0.5-command-routing`.** Tested well on the Backglass (play / stop / seek /
pause / resume / volume all behave identically). Command routing is now completely
decoupled from the window:

```
VM → Player1 (PlayerContext) → JukeboxPlayer → IPlaybackHost → BackglassWindow
```

- **Slice 1 (`8a73696`)** — pause / resume / volume: both subscription AND body migrated
  into `JukeboxPlayer` (they were thin forwarders).
- **Slice 2 (`41074ab`)** — play / stop / seek: subscription ownership migrated onto
  `JukeboxPlayer`; the engine bodies (`OnPlayRequested`/`OnStopRequested`/`OnSeekRequested`)
  stay in the window behind `IPlaybackHost.Play`/`Stop`/`Seek` forwarders.
- `IPlaybackHost` expanded with `Pause` / `Resume` / `SetVolume` / `Play` / `Stop` / `Seek`.
- **`BackglassWindow.AttachViewModel` now subscribes to ZERO VM command events** — all six
  are owned by `JukeboxPlayer`.

**Significance:** a second player (Topper) could now receive commands from a `Player2`
context through its own `JukeboxPlayer` + `IPlaybackHost`, with no Backglass-specific
wiring — even before the engine bodies relocate.

**Still in the window (next increment):** the play/stop/seek engine bodies + VLC/gapless
state (`_playCts`, `_gaplessPlayer`, `_nextMediaPlayer`, seek-verify, position timer, live
clock, prefetch), reached via the `IPlaybackHost` forwarders.

---

## ✅ MILESTONE — Phase 0.6/0.7: engine + orchestration fully relocated (validated)

**Tag: `phase-0.7-orchestration-relocated`.** The playback engine AND its orchestration
now live entirely outside `BackglassWindow`. The window is a **pure `IPlaybackHost`** —
video surface + idle/logo visuals + engine-host callbacks — holding ZERO playback logic.

Final architecture (validated end-to-end on the Backglass: YouTube, cached/prefetched,
gapless PCM, audio-only, track→track transition, live stream, first-frame timeout):

```
VM → Player1 (PlayerContext) → JukeboxPlayer.Play/Stop/Seek
      ├─ MediaEngine        (VLC MediaPlayer + gapless + seek-verify + last-stream ctx + live clock)
      └─ IPlaybackHost      (window: video surface, idle/blob visuals, timers, DMD notify)
```

Engine extraction (Phase 0.6):
- **`MediaEngine` (`7e8dad4`)** — VLC lifecycle (shared-VLC adoption, init handshake,
  EnsureInitialized), last-stream context, live clock, player-swap.
- **`f6bc1cd`** — gapless engine state + `StopGaplessPlayer`/`DisposeGaplessNext`.
- `MediaEngine` ownership moved into `JukeboxPlayer`; the window references it via `_engine`.

Orchestration relocation (Phase 0.7):
- **Stop (`4eb86dc`)** — pilot: `JukeboxPlayer.Stop()`.
- **Seek (`2e60fb0`)** — `JukeboxPlayer.Seek` + `SwitchToCachedFileAndSeek`; adopted
  **Option X** (the player holds the VM as its `Model` for now-playing state + shared
  services; Phase 2 later moves per-player state into `PlayerContext`).
- **Play (`ebff678`)** — the ~430-line `OnPlayRequested` → `JukeboxPlayer.Play`.
- `IPlaybackHost` seam grew: host-thread marshalling (`CheckHostAccess`/`BeginInvokeOnHost`/
  `InvokeOnHostAsync`), view callbacks (color-cycle / position-timer / info-timer /
  transition-overlay / idle / video-surface / DMD-notify), and `CreateGaplessPlayer`.

**Significance:** `JukeboxPlayer` is now a **window-agnostic playback controller**. Phase 1
(a real second player on the Topper) is now the "wiring exercise" the plan promised — the
Topper gets its own `JukeboxPlayer` + `MediaEngine` + `IPlaybackHost`, driven by a
`Player2` context, with NO duplicated logic and NO calling back into the Backglass.

**Remaining minor cleanup (optional):** the view-coupled gapless helpers
`CreateGaplessPlayer` / `PrepareGaplessNext` still live in the window and are reached via
the `CreateGaplessPlayer` host callback — they work correctly there; relocating them is
low-value polish, not a blocker for Phase 1.

### Manual validation matrix (run on the Backglass only; expect identical behavior)

- YouTube stream (search → play)
- Cached / prefetched playback
- Gapless audio transition (track → track)
- Seek forward / back + seek-verify recovery
- Live stream (SiriusXM) elapsed clock + non-seekable
- Chapters (tick marks, Skip / PreviousTrack chapter jumps)
- Pause / resume
- Volume slider
- `audio-only` toggle
- Pinup / ambient handoff (stop returns to idle/ambient)

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
