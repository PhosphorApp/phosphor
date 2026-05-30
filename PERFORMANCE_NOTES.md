# Phosphor — Visualization Performance Notes

Running notes from a perf review of the blob/pattern rendering layer (Mandelbrot
and ProjectM excluded). Tracks what's been optimized, what's still on the table,
and rationale.

Reminder: every per-tick cost is multiplied by the number of windows showing
blobs concurrently (`PlayfieldWindow` and `BackglassWindow` each run on their
own dispatcher thread).

---

## ✅ Done

### MatrixBlobPattern — GPU / allocation wins
- **Removed `BitmapCache` from trail `TextBlock`s.** Trails change `Text` and
  `Opacity` every tick, which invalidated the cache each frame — net cost was
  higher than no cache, plus up to ~1500 GPU textures per window.
- **Removed per-leader `BlurEffect`.** The parent `ZoomLayer` canvas already has
  a `BlurEffect`; nesting two shader passes per element was redundant.
- **Froze `ClassicGreenBrush`** (used when `ColorCycling = false`).

### MatrixBlobPattern — O(1) trail removal
- Per-tick expiration uses **swap-and-pop** on `_trails` instead of
  `List.RemoveAt(i)` (was O(n) per removal, O(n²)/sec under steady-state
  churn at 1500 cap).
- Capacity eviction does swap-and-pop at index 0 (close-enough FIFO).
- `ZoomLayer.Trails` list replaced with `TrailCount` int — eliminated the O(n)
  `Remove(trail)` scan per dead trail.

### MatrixBlobPattern — DOF pulse fix
- Bug discovered during the review: `PulseDominantColor` filtered trails by
  `RoygbivHelper.FromHue(trailHue) != band`, but the pulse only fires on a band
  *change*, at which moment all live trails still wear the previous band's
  color. Net result: only the very first pulse ever appeared.
- Fixed by removing the per-trail hue filter. All Matrix trails belong to the
  same dominant hue family by design, so flashing every visible trail is
  correct.

### `BlobPatternBase.ApplyAudioReactive` — direct scale with lerp
- Audio tick fires at ~60 Hz; `ReactiveSpeedMs` defaults to 120 ms. Each tick
  was creating 2 `DoubleAnimation` objects per blob that were replaced 16 ms
  later — ~7/8 of frames were wasted.
- Replaced with exponential lerp (`ScaleX += (target - ScaleX) * lerpFactor`).
  `lerpFactor` derived from `reactiveSpeedMs` so the user's Reactive Speed
  setting still controls smoothing. Smooth on 120/144/240 Hz monitors.
- Clears any in-flight animation once via `HasAnimatedProperties` guard.
- `FerrofluidClusterPattern` calls `base.ApplyAudioReactive` → inherits fix.

### `FractalBoxPattern.ApplyAudioReactive` — ease + transform lookup
- Replaced per-call `new QuadraticEase` with the base class's static frozen
  `_reactiveEase` (changed from `private` to `protected`).
- Same exponential lerp for per-blob scale.
- `ScaleTransform` ref cached on `BlobState.CachedScaleTransform` (new field)
  to avoid scanning `TransformGroup.Children` every tick.
- Canvas-level blur animation kept as `BeginAnimation` (one per canvas, not
  per blob — acceptable).

### `BounceSimulator.FlashBlob` — tick-based flash decay
- Replaced per-collision `DispatcherTimer` allocation with a
  `BlobState.FlashRemaining` double field (seconds). Set to 0.08 on collision;
  the existing `OnRendering` loop decays it and restores `BaseOpacity` on
  expiry. Zero allocations per collision.

### `LightCycleSimulator.CheckTrailCollision` — spatial grid index
- Added `SegmentGrid`: axis-aligned segments bucketed by cross-axis coordinate
  (horizontal segments by Y-bucket, vertical by X-bucket). Bucket size =
  `CollisionMargin`.
- Point queries check only 2–3 nearby buckets instead of all segments across
  all cycles. Live segments (one per cycle) still checked via brute force.
- `CommitTrailSegment` adds to both `c.Segments` and `_segmentGrid`.
- `FadeTrails` removes owner's segments from the grid on cycle death.
- `PointNearAxisAligned` exploits H/V alignment for early rejection.

---

## 🟠 Medium-impact, still open

### 1. `MatrixBlobPattern.PulseDominantColor` — animation count
Up to 400 simultaneous `ColorAnimationUsingKeyFrames` per pulse, each with a
`Completed` lambda. Pulses can stack if bands change rapidly.

**Fix idea:** decay manually in the per-tick loop using a `PulseAmount` field
on `TrailChar`, eliminating WPF storyboards entirely.

### 2. `MatrixBlobPattern.PickNonOverlappingX` — LINQ + lambdas
```csharp
Enumerable.Range(0, bandCount).OrderBy(_ => _rng.Next()).ToList();
```
Allocates iterator + list + N delegates per call. Fires on every column
respawn.

**Fix idea:** in-place Fisher–Yates on a reused `int[]`.

### 3. `OrbitalBlobPattern` / `Fractal` / `LavaLamp` / `Random` / `FractalBox`
   — `_blobs.IndexOf(blob)` on every animation completion
O(n) lookup. Fires every 10–25 s per blob, so low frequency, but trivially
fixable.

**Fix idea:** stash the index on `FrameworkElement.Tag` or use a
`Dictionary<FrameworkElement, int>`.

### 4. Easing allocations across patterns
`RandomBlobPattern`, `OrbitalBlobPattern`, etc. allocate fresh
`SineEase`/`CubicEase` per retarget.

**Fix idea:** one static frozen instance per ease, shared.

### 5. `MatrixBlobPattern` uses `DispatcherTimer` @ 33 ms
Not vsync-aligned; can drift/judder under load. Every other simulator uses
`CompositionTarget.Rendering`.

**Fix idea:** switch to `CompositionTarget.Rendering` + `Stopwatch` for `dt`
(matches `BounceSimulator`/`LightCycleSimulator`).

### 6. `BlobPatternBase.CreateBlobs` — per-blob `BitmapCache(0.5)`
Caching helps when the brush color is stable; thrashes when color cycles.
`BlobPatternConfig.UseBitmapCache` exists for callers — confirm
externally-color-cycled patterns are passing `false`.

---

## 🟡 Smaller wins / cleanup

- `MatrixBlobPattern.CreateLeader` allocates `new FontFamily("Consolas")` per
  leader — cache one static instance.
- `LightCycleSimulator.GetTrailBrush` + `CommitTrailSegment` create then
  `Clone().Freeze()` a `SolidColorBrush` per segment commit.
- `LightCycleSimulator` grid rebuilds dozens of `Line` UIElements on resize —
  a single `GeometryDrawing` inside a `DrawingVisual` would be far cheaper.
- `BlobPatternBase.Enter/Exit` use a `DispatcherTimer` for completion — could
  chain off the last animation's `Completed`.
- `BlobPatternBase.ApplyAudioReactive` writes `blob.Opacity` directly while a
  previous animation on `OpacityProperty` may still be ticking underneath.

---

## Suggested order of attack (when picking back up)

1. **#1** `ApplyAudioReactive` rewrite — biggest steady-state CPU/GC win.
2. **#2** `FractalBoxPattern` cleanup — same family, easy.
3. **#3** Bounce flash — quick, removes `DispatcherTimer` allocations.
4. **#9** Matrix on `CompositionTarget.Rendering` — smoother frames, trivial.
5. **#5** Matrix pulse decay inline — closes out the Matrix work.
6. **#4** LightCycle spatial index — only matters at long lifetimes/high cycle
   count.
7. Smaller wins (#6, #7, #8, #10, 🟡 items) opportunistically.

---

## 🔴 Game of Life — SSAA & Scaling Experiments

### Architecture
Each cell is 1 pixel in a `WriteableBitmap` sized `(screenW / cellSize) × (screenH / cellSize)`.
WPF's GPU texture sampling upscales it to screen resolution. The `BitmapScalingMode`
on the `Image` element controls the filter:
- **NearestNeighbor** — crisp blocky pixels, zero GPU filtering cost.
- **Fant** — bilinear/Fant filter, smooth antialiased edges, essentially free (GPU-side).

Now exposed as a user setting (`Scaling: Nearest Neighbor | Smooth (Fant)`).

### Bottleneck: `StepSimulation` is single-threaded
The simulation iterates every cell, checks 8 neighbors with bounds checks,
accumulates color, and applies birth/death rules. Runs on the WPF dispatcher thread.

- `CellSize=5` on 1920×1080: ~77k cells → fast.
- `CellSize=1`: ~2M cells → tight at fast tick rates.

**CPU and GPU utilization appear low** during slowdowns because only one dispatcher
thread is saturated; all other cores are idle.

### SSAA Experiments (reverted)

**Fake SSAA (2×2 pixel blocks):** Rendered each cell as a 2×2 block in a 2× bitmap
with Fant downscaling. Produced identical visual results to plain Fant on a 1:1
bitmap — the GPU filter already blends edges. Pure waste: 4× pixel writes, 4× bitmap
transfer, same output.

**True 2× SSAA (halved cell size):** Doubled the grid in each dimension (4× cells).
Visual quality was excellent — finer structures, smoother edges, richer detail.
However, 4× simulation cost (neighbor checks, birth/death logic per cell) made it
impractical at fast tick rates or small cell sizes.

### Parallelization Opportunity (future)
Moving `StepSimulation` off the dispatcher thread would unlock multi-core scaling:
- Reads `_colorCurrent`, writes `_colorNext` — no overlap, so **row-parallel is safe**.
- Buffer swap and `RenderFrame` must remain on the dispatcher (WPF bitmap access).
- Sector birth/alive counters need per-thread accumulators or `Interlocked` adds.
- Stagnation snapshots are a simple copy — stay single-threaded after the parallel step.
- Expected: near-linear scaling with core count for the simulation step.
- Rendering (`RenderFrame`) is already fast (1 write per cell) and unlikely to need it.

### Phase 1 MT (branch `GoL-MT`)
Implemented row-level `Parallel.For` for both `StepSimulation` and the pixel
write inside `RenderFrame`. Kept everything on the dispatcher thread — the
`WriteableBitmap` `Lock`/`AddDirtyRect`/`Unlock` calls stay on the owning
thread, and inside the lock, multiple threads writing disjoint pixels into the
back buffer is safe.

- Extracted `StepRow(y, …)` and `RenderRow(y, …)` helpers. Each writes only
  to its own row's indices — no shared mutable state across rows.
- Sector counters (`_sectorBirths`/`_sectorAlive`) accumulated into per-worker
  `int[]`s via `Parallel.For`'s `localInit`/`localFinally`, merged under a
  single lock at the end of the step (one lock per worker, not per cell).
- Avg-color sums in `RenderFrame` use the same per-worker pattern.
- Neighbor accumulation in `StepRow` was unrolled (the previous dy/dx loop +
  `dx==0&&dy==0` skip was a hot branch in the inner loop). Y-wrap and x-wrap
  computed once per row / per cell instead of per neighbor.
- `ParallelCellThreshold = 50_000` guard keeps tiny grids (e.g. cellSize=10
  on 1080p) single-threaded, where the thread-pool dispatch cost exceeds the
  work.
- Frame timing log (`DebugLog.Log` every 1000 frames) added to measure the
  before/after.

Phase 2 (offload the whole tick to a worker thread, marshal only `Lock`/
`Unlock`/`UpdateCamera` back to the dispatcher) is on hold pending Phase 1
measurements.

### Phase 1.5 — Inner-loop & per-frame wins
Small but cumulative tweaks layered on top of the Phase 1 MT work:

- **Dropped `Array.Clear(_colorNext)` in `StepSimulation`.** Every code path
  in `StepRow` writes `_colorNext[idx]` unconditionally (color or 0), so the
  up-front zeroing was pure waste — ~4 MB of writes per frame on a 2M-cell
  grid, multiplied by every visible window.
- **Demoted `_age` from `ushort[]` to `byte[]` and clamped at 3.** Only the
  `_age[i] <= 2` test in `RenderRow` ever reads it, so anything past 3 is
  equivalent. Halves age-array bandwidth and improves cache residency next
  to `_colorCurrent`. Also dropped the per-cell `Math.Min(ushort.MaxValue, …)`
  in favor of a single `if (_age[idx] < 3) _age[idx]++;`.
- **Split `StepRow` into edge / interior / edge** so the `w - 2` interior
  cells per row execute without the `x==0 ? w-1 : x-1` and
  `x==w-1 ? 0 : x+1` wrap branches. Inner body extracted into an
  `[AggressiveInlining]` `ProcessCell` helper to keep both call sites tight.
  Also lifted `fadeStart = (byte)Math.Clamp(FadeGenerations, 1, 255)` and the
  sector row base out of the inner loop.
- **Throttled `UpdateDominantBrush` to ~once every 2 seconds of wall-clock
  time** (`interval = max(1, 2000 / max(1, TickIntervalMs))` ticks). Because
  `TickIntervalMs` ranges 1–100 ms via the slider — and can be as low as
  ~4–7 ms when `GameOfLifeUseScreenRate` syncs to a 144/240 Hz display — the
  divisor must use the actual tick rate, not a 16 ms floor, or fast tick
  rates would still update the brush several times per second. DOF
  band-change events already gate themselves with a multi-second cooldown,
  so running the stride-2 visible-region scan + 8-bucket histogram any
  faster than ~0.5 Hz is wasted work. This was the second-most-expensive
  per-frame thing after the sim itself.
- **`InjectCells`: fused the alive-count pass with the stagnant-cells scan**
  and reused a field-level `List<int> _stagnantCells` buffer. Removes one
  full-grid pass per beat and the per-beat list allocation (which on big
  grids with persistent still-lifes could be hundreds of entries).

### Dominant-Color Scoping to Visible Region
`_brushes[0]` (the dummy brush that PlayfieldWindow reads for DOF dominant-color
detection) was previously set to the whole-grid RGB average — expensive on large
grids and semantically wrong when zoomed in (DOF might pulse red while the
viewport shows green).

Replaced with `UpdateDominantBrush()`: a stride-2 scan that builds an 8-bucket
ROYGBIV histogram and sets the brush to the dominant band's average color. When
`CameraRoam` is active and `_cameraZoom > 1.05`, the scan is restricted to the
axis-aligned bounding box of the visible region (derived by inverting the camera
transform at the four display-rect corners). At 5× zoom this touches ~4% of the
grid; at 2× ~25%. Rotation uses the AABB of the rotated rect (worst case ~41%
overshoot, still far smaller than the full grid).

### Restart Simulation on Track Change
Added `GameOfLifeRestartOnTrackChange` setting (default off). When enabled,
`OnPlaybackStartedTransition()` in DmdWindow calls the existing
`RestartGameOfLife()` fan-out on all windows, reusing the same plumbing as
settings-change restarts. No new events or transport needed.

---

## 🔵 Future — P-Core Affinity for Parallel.For

### Problem
On hybrid CPUs (Intel 12th+ gen), `Parallel.For` partitions rows across all
available cores. E-cores (~60–70% IPC of P-cores, smaller caches) become
straggler bottlenecks — P-core workers finish first and wait. Meanwhile,
disabling E-cores in BIOS removes capacity that WPF compositor, audio decode,
and OS background work could use.

### Proposed Solution: P-Core Process Affinity at Startup
Set `Process.ProcessorAffinity` early in `App.OnStartup` to a bitmask covering
only P-cores, letting E-cores absorb OS/WPF background work:

```csharp
// Example for a 14900K: 8 P-cores on logical 0-15 (with HT) or 0-7 (HT off)
// Adjust mask per CPU topology — use `GetLogicalProcessorInformationEx` for
// runtime detection, or make it a configurable AppSettings value.
using var proc = System.Diagnostics.Process.GetCurrentProcess();
// P-cores only (bits 0-7 for 8 P-cores, HT off)
proc.ProcessorAffinity = (nint)0xFF;
```

### Runtime Detection Sketch
`GetLogicalProcessorInformationEx(RelationProcessorCore)` returns per-core
records with an `EfficiencyClass` field (0 = E-core, 1 = P-core on Intel
hybrid). Walk the records, collect P-core logical processor masks, OR them
together:

```csharp
[DllImport("kernel32.dll")]
static extern bool GetLogicalProcessorInformationEx(
    int RelationshipType, IntPtr buffer, ref int returnedLength);

// RelationProcessorCore = 0
// Each PROCESSOR_RELATIONSHIP has: EfficiencyClass (byte) + GroupMask[]
// Build affinity mask from records where EfficiencyClass == 1 (P-core)
```

### Trade-offs
| Approach | Pros | Cons |
|---|---|---|
| HT off + E-cores off (BIOS) | Simplest, most consistent frame times | Reduces total system capacity |
| Process affinity to P-cores | GoL gets fast cores; E-cores absorb WPF/OS | Needs per-CPU mask; incorrect mask → underutilization |
| Thread affinity per `Parallel.For` | Finest control | Complex; `Thread.SetProcessorAffinity` is per-thread, awkward with thread pool |
| No affinity (status quo) | Zero complexity | E-core stragglers inflate worst-case frame times |

### Recommendation
Start with process-level affinity behind an opt-in `AppSettings.PCoreMask`
(default 0 = disabled). If set, apply at startup. Document common masks:
- 14900K (8P+16E, HT off): `0xFF`
- 14900K (8P+16E, HT on): `0xFFFF`
- 13600K (6P+8E, HT off): `0x3F`

Long term, auto-detect via `GetLogicalProcessorInformationEx` so it's
zero-config.

---

## 🟣 Future — Bitboard Simulation + EraBanded Color Mode

### Goal
Hit **144 Hz @ 4K with `cellSize = 1`** (currently `cellSize = 2` runs with
some headroom; `cellSize = 1` cannot keep up). At 4K with `cellSize = 1`
that's a 3840×2160 grid = ~8.3M cells per step, per window.

### Why the current ceiling exists
`StepSimulation` is bandwidth-bound on `_colorCurrent` (4 bytes/cell × 9
neighbor reads per cell). The Phase 1 MT work fans rows across cores, but
each row's inner loop still does 9 scalar `uint` reads + branches per cell.
Bitboards collapse the alive/dead computation to ~1/64th the work, but only
if cells aren't carrying genetic color information that requires
per-neighbor lookups.

### Strategic Move: Decouple Color From the Simulation Rule
The reason the bitboard ceiling for our code looked like "only 3–8× wall
clock" in earlier analysis was the genetic color blend on birth:

```csharp
uint r = rSum / 3, g = gSum / 3, b = bSum / 3;
```

That requires knowing *which* three neighbors were alive and reading their
colors — i.e. scalar 8-neighbor fixup per birth, against the original
`_colorCurrent`. If birth color comes from a single global per-frame value
instead, the fixup collapses to "write `currentHueColor` to every birth
lane." `_colorCurrent` is no longer read during the neighbor scan at all —
only written on births, copied on survivals, snapshotted on deaths.

### Proposed: `GameOfLifeColorMode` Setting
New `AppSettings.GameOfLifeColorMode` enum (default = `Genetic` to preserve
existing look). Two values:

- **`Genetic`** (default, current behavior): births inherit a blended RGB
  average of the three live parents. Visually rich — collisions between
  red and yellow regions birth orange offspring. Uses the existing scalar
  `StepRow` / `ProcessCell` path verbatim.
- **`EraBanded`**: births take the simulation's current rotating-hue value
  (slow ROYGBIV cycle). Survivors keep their birth color until death, so
  regions visually band by age — old still-lifes wear last week's hue, new
  blooms wear this week's. Lets the camera roam reveal "color geology."
  Unlocks the bitboard path.

### Data Layout (EraBanded mode)
- `ulong[] _aliveCurrent`, `ulong[] _aliveNext` — bitpacked, one row padded
  to a multiple of 64 bits. `aliveWordsPerRow = (gridW + 63) / 64`.
- `_colorCurrent: uint[]` stays as today but is only **written** on births,
  **read** on render and `UpdateDominantBrush` — never touched during the
  neighbor scan.
- `_age: byte[]`, `_fade: byte[]`, `_fadeColor: uint[]` unchanged.
- `_currentBirthColor: uint` — recomputed once per tick from the global
  hue rotator.

### Step Algorithm (EraBanded)
1. **Bitboard alive/dead step** (parallelizable per row group):
   For each row word, load three windows (`prevRow`, `thisRow`, `nextRow`)
   and their left/right shifts (handling row-end wrap into the next/prev
   word, with toroidal wrap at row edges). Compute neighbor counts using
   the classic shift-and-add adder tree (4 bits of count per cell, since
   max neighbors = 8). Apply B3/S23: `next = (count == 3) | (alive & count == 2)`.
   Output: `_aliveNext` word.
2. **Color/decay sweep** (single linear pass over `_aliveCurrent` and
   `_aliveNext`, parallelizable):
   - `births = _aliveNext & ~_aliveCurrent` — for each set bit, write
     `_currentBirthColor` to `_colorCurrent[i]`, set `_age[i] = 1`,
     `_fade[i] = 0`, increment sector birth counter.
   - `deaths = _aliveCurrent & ~_aliveNext` — for each set bit, save
     `_colorCurrent[i]` to `_fadeColor[i]`, set `_fade[i] = fadeStart`,
     `_age[i] = 0`, clear `_colorCurrent[i]`.
   - `survivors = _aliveCurrent & _aliveNext` — increment `_age` (clamped
     at 3), increment sector alive counter. Color and fade left alone.
   - `idle = ~_aliveCurrent & ~_aliveNext` — decrement `_fade[i]` if
     non-zero. (Empty bitword shortcut: skip the inner loop entirely if
     both words are zero AND no `_fade` is non-zero in this slice.)
   Use `BitOperations.TrailingZeroCount` to iterate set bits, skipping
   empty regions in bulk.
3. **Sector counters** — accumulated via per-thread arrays in the
   color/decay sweep (same pattern as today's `Parallel.For`
   `localInit`/`localFinally`).
4. **Stagnation snapshots** — `_snapshotA`/`_snapshotB` change from
   `bool[]` to `ulong[]` aligned with the alive bitboard. The "stagnant"
   set becomes a simple `_snapshotA & _snapshotB` AND of bitwords, with
   bit iteration to fill `_stagnantCells` only during `InjectCells`.

### Toroidal Wrap Notes
- **Horizontal wrap** at row edges: the leftmost word's "left shift in"
  bit comes from the bit at `gridW - 1`; the rightmost word's "right
  shift in" bit comes from bit 0 of the same row. When `gridW` is not
  a multiple of 64, the rightmost word is partial — mask off unused
  high bits before storing back, and source the wrap bit from the
  correct in-row position, not from the word's bit 63.
- **Vertical wrap** is unchanged from today's `yUp`/`yDn` logic — just
  applied per word instead of per cell.

### Expected Performance
- **Bitboard alive/dead step alone**: 15–30× the scalar cost on the
  neighbor count (textbook bitboard win).
- **Color/decay sweep**: bandwidth-bound at one `uint` write per
  birth/death and one byte read/write per survivor — much smaller than
  the scalar 9-read inner loop today.
- **Wall-clock net on `StepSimulation`**: estimated **15–25×** vs.
  current scalar. Combined with the existing Phase 1 MT row
  parallelization, this should be the difference between "can't run
  cellSize=1 @ 4K" and "runs with headroom."

### Optional Phase B: SIMD Over the Bitboard
Bitboards are themselves SIMD (64-wide). The natural next step, if
Phase A still isn't enough headroom, is to wrap the shift/add neighbor
math in `Vector256<ulong>` (AVX2) so each loop iteration handles 4
`ulong`s = **256 cells per instruction**. Same algorithm, wider lane.
Falls back to scalar bitboards on non-AVX2 CPUs via
`Avx2.IsSupported` guard.

We would **not** also do a separate `Vector256<byte>` over a `byte[]
_alive` mask — that's a competing data layout for the same problem,
and bitboards are 8× denser. Pick one. Phase B is purely "same
algorithm, wider execution."

### Optional: `Buffer.MemoryCopy` Bitmap Blit
Today's `RenderRow` writes the final pixel value with one `unsafe
uint*` store per cell, so the suggestion "use `Buffer.MemoryCopy`
instead of `WriteableBitmap.SetPixel`" doesn't apply — we're already
past that. However, the EraBanded refactor opens the door to
pre-baking `_colorCurrent` directly in BGRA32 layout (it already is,
modulo per-cell heat/fade post-processing). If those post-processing
passes are folded into the decay sweep, `RenderFrame` collapses to a
single `Buffer.MemoryCopy(_colorCurrent → BackBuffer)`. Modest extra
win (1.5–2× on render only) but a clean simplification.

### Implementation Order
1. **Add `GameOfLifeColorMode` setting + UI toggle** (default `Genetic`).
   No perf work yet. Just plumbing: `AppSettings` field, `SettingsWindow`
   UI, `DmdWindow` fan-out, restart-on-change.
2. **Implement `EraBanded` branch using scalar bitboards.** Keep the
   existing scalar `StepRow` for `Genetic` mode as both the correctness
   reference and the active path when the user prefers the genetic look.
3. **Correctness test** — run both modes side by side on a small grid
   with deterministic seeding, diff `_colorCurrent` snapshots tick by
   tick. Bitboard alive/dead must match scalar exactly modulo color.
4. **Parallelize the bitboard step** by row groups (same threshold
   logic as today's `ParallelCellThreshold`).
5. **Benchmark `cellSize=1` @ 4K** via the existing `[GoL] Frame N: …
   ms` log. Decide if Phase B (AVX2) is needed.

### Risks / Open Questions
- **Visual reception of `EraBanded`** — the genetic blend is part of
  the current pattern's character. Some users will prefer it; the
  setting must default to `Genetic` and the toggle must be clearly
  labeled.
- **Bitboard wrap correctness** — toroidal edge handling at the
  rightmost partial word is the most error-prone bit. The
  side-by-side correctness test above is non-negotiable.
- **Sector counter accuracy** — popcount-per-sector is exact for
  alive, and births/deaths are exact via `xor`/`and-not` popcount.
  Should be no precision loss vs. today.
- **`InjectCells` writes to `_colorCurrent` directly** today — under
  EraBanded it must also set the corresponding bit in `_aliveCurrent`,
  or the next step will overwrite the injection. Same applies to
  `SeedGrid`.

---

## ✅ Phase 3 Results — Bitboard EraBanded (shipped on `GoL-Bitboard`)

Implemented as planned: dedicated `StepSimulationBitboard` for
`ColorMode == EraBanded`, scalar `StepRow` path unchanged for `Genetic`.
`SeedGrid` and `InjectCells` keep writing colors directly and set
`_aliveBitboardDirty = true`; the bitboard path rebuilds `_aliveCurrent`
from colors on its next step. Wrap correctness validated visually on
all `cellSize` values.

### Measured at 4K (2160×3840, 8,294,400 cells), windowed avg frames 501–1500

| Path                              | median | mean   | min    | p95    |
|-----------------------------------|--------|--------|--------|--------|
| Scalar Genetic                    | 5.02 ms| 6.60 ms| 4.50 ms|14.60 ms|
| Bitboard EraBanded (initial)      | 7.48 ms| 8.52 ms| 7.08 ms|15.86 ms|
| Bitboard EraBanded (after fix)    | **3.04 ms**| **4.55 ms**| **2.68 ms**|11.78 ms|

The "after fix" row is what the bitboard path actually delivers when its
serial pre-passes are folded into the parallel worker — **2.5× faster
median than the initial naive bitboard, and ~1.65× faster than scalar
Genetic.** Comfortably clears 144 Hz (6.94 ms budget).

### Key Lesson — Serial Pre-Passes Drown Out Bitboard Compute Wins

The naive Phase 3 implementation had two serial pre-passes before the
parallel `StepRowBitboard` loop:

```csharp
Array.Clear(_colorNext);                  // 32 MB serial memset
for (int i = 0; i < totalCells; i++)      // 16 MB serial scan
    if (_fade[i] > 0) _fade[i]--;
```

These cost ~2–3 ms on a 4K grid (memory bandwidth limited, single
thread), and they **block all worker threads from starting** their
bitboard rows. The bitboard step itself was correctly ~1 ms — but
nobody could see that under 3 ms of stalled workers.

**Fix:** move both pre-passes inside `StepRowBitboard`, per row:

```csharp
Array.Clear(_colorNext, rowBase, w);   // ~15 KB, hot in L1
for (int x = 0; x < w; x++)            // touches the same _fade row
    if (_fade[rowBase + x] > 0) _fade[rowBase + x]--;
```

Now the memory traffic is parallelized across cores, and the cleared/
decayed cache lines are still hot in L1 when the sparse births and
survivors land on the same row moments later. Result: serial overhead
gone, bitboard compute win finally visible.

**Generalizable rule:** when adding a parallel optimization to an
existing algorithm, audit every `Array.Clear`, `memset`-style loop, or
fold pass for whether it can be sliced and pushed inside the worker.
Any serial O(N) pass before a parallel O(N) loop costs you P× the
compute win (P = thread count) because it serializes the workers'
start.

### Remaining Noise (max 24 ms, p95 11.8 ms)

The bitboard step itself is steady at ~3 ms. The p95 / max spikes are
**not** from the bitboard sim — the follow-on polish (rebuild scan
elimination + Parallel.For buffer pool) targeted the suspected causes
and did not move p95 meaningfully. So the remaining spikes are most
likely:
- WPF dispatcher / render-thread contention
- The dominant-brush throttle cycle in `RenderFrame`
- Occasional GC pauses unrelated to the sim allocations

Out of scope for this branch.

### Final Numbers (post-polish, 4K, 1000-frame averaged window)

| Mode      | median  | mean    | min     | p95      | 144 Hz budget (6.94 ms) |
|-----------|---------|---------|---------|----------|-------------------------|
| Genetic   | 5.10 ms | 6.34 ms | 4.66 ms | 10.84 ms | median ✓, mean borderline, p95 misses |
| EraBanded | 2.98 ms | 4.71 ms | 2.60 ms | 12.73 ms | median ✓✓, mean ✓, p95 misses |

EraBanded is ~1.7× faster than Genetic on steady-state cost.
Both clear median budget; both still spike occasionally (WPF, not sim).
Genetic at 4K is do-able with occasional hitches; EraBanded is the
recommended mode if a user wants 4K @ 144 Hz with comfortable headroom.

### Polish Applied (shipped after the main Phase 3)

1. **Eliminated `RebuildAliveBitboardFromColors` (32 MB serial scan)** —
   `SeedGrid` and `InjectCells` now set the bitboard bit inline when
   they write a color. Cost: one OR per injected cell (~9 cells per
   cluster × a few clusters per beat = trivial). Eliminates the
   post-injection rebuild that was the suspected p95 spike source.

2. **Pooled per-thread sector counter buffers** —
   `Parallel.For`'s `localInit` previously allocated
   `new int[sectorCount]` twice per worker per frame. Replaced with a
   `ConcurrentBag<(int[] births, int[] alive)>` pool. After a few
   frames, pool stabilizes at worker-count buffers reused forever.

### What Did NOT Help

- **Pre-decaying fades in a single serial loop** — see above, this was
  the main regression source.
- **Clearing `_colorNext` via a single `Array.Clear`** — see above.
- **Removing rebuild scan + pooling buffers (the polish above)** —
  shipped because they're correct and cheap, but didn't move p95
  measurably. The remaining spikes are outside the sim.

### What Stays as Future Work

- **Phase B (AVX2 over bitboards)** — not needed for 4K @ 144 Hz given
  current numbers. Would buy headroom for hypothetical 8K or `cellSize=1`
  on lower-end hardware.
- **Investigate p95 / max spikes outside the sim** — likely WPF render
  thread, dominant-brush cycle, or unrelated GC. Out of scope for this
  branch.

---

## ✅ Anti-Stagnation (shipped on `master`)

Conway sims naturally collapse into seas of small period-2 oscillators
and still lifes (blinkers, blocks, beehives, beacons). Stable, boring,
no new events for the camera to follow. The optional `AntiStagnation`
toggle (default off) injects gentle perturbations to keep the field
interesting without looking heavy-handed.

### Detection
A cell alive in **three consecutive generations** (N-2, N-1, N) is
"boring" — catches still lifes and the always-on cells of period-2
oscillators (blinker center, beacon corners), while ignoring gliders
and active blooms (their cells turn over each tick). Computed with
**two bitwise ANDs over the rolling alive bitboards** — reuses the
Phase 3 bitboard infrastructure. At 4K that's ~5K ulongs scanned per
intervention, sub-millisecond.

### Intervention Mix (per detected boring cell, when sweep fires)
- **70% NUDGE** — kill the boring cell + birth a random dead 8-neighbor.
  Shifts the shape by 1 cell, almost always breaks the oscillator into
  a glider/bloom/eventual die-out. Looks like organic drift, not a
  hand of god.
- **25% DECAY** — quiet death with normal fade-out. Removes still lifes
  without explosions.
- **5% CATALYST** — stamp a small glider in a nearby empty area.
  Gliders are native to Life and chain-react satisfyingly into debris.

Cadence scales with the user-facing `AntiStagnationIntensity` (1–10,
default 5). At intensity 5 the intervention sweep fires every ~10
ticks (~1 s at 100 ms tick rate) and perturbs ~3 % of boring cells per
sweep (capped at 256 to prevent spikes on enormous still-life seas).
A periodic **sweeper glider** also spawns from a random overscan edge,
aimed roughly at the camera focus area, to clear static regions in
low-density scenes where the boring-cell detector finds nothing.

### Mode Compatibility
EraBanded maintains `_aliveCurrent` natively, so detection is free
data. Genetic mode rebuilds an alive bitmap from `_colorCurrent` into
`_aliveNext` (used as scratch) once per intervention tick —
sub-millisecond at 4K and only runs when AntiStagnation is enabled,
so Genetic users who leave it off pay zero cost.

### Population Gate (shipped after initial rollout)
Initial behavior was always-on once the 3-generation history filled.
Problem: early sparse seedings have isolated blinkers in mostly-empty
grids — they look "boring" to the 3-gen detector, but the sim isn't
stuck; it's still spreading. The detector was pruning the field before
it could build up.

**Fix:** skip `RunAntiStagnationTick` while alive cells < **3% of grid
total** (`AntiStagMinDensity`). 3% sits just under Conway's natural
equilibrium density (~3.5%), so the gate releases the moment the field
fills out. Population sum is essentially free — 64 int adds on
`_sectorAlive`, which the step path already maintains.

Why density, not elapsed time:
- Framerate-independent (tick rate is user-configurable 1–100 ms).
- Grid-size-independent (3% of 4K = 250K cells; of 1080p = 60K cells).
- Tracks actual sim state — fast seedings release in 1–2 s, slow ones
  in 5–10 s, both correctly.

While gated, `_antiStagHistFilled` is also reset so the 3-generation
detection re-warms cleanly when the threshold is crossed. Otherwise
the first post-gate intervention would compare gen N against stale
sparse-seed-era history and over-prune.

---

## 📝 Future — JSON deserialization with streaming

Currently all JSON cache/playlist files (e.g. `plex_concerts`, `playlists.json`,
`cache/index.json`) are loaded with `JsonSerializer.Deserialize<T>(string)`,
which reads the entire file into a single string before parsing.

**Why it works today:** largest file is ~146 KB — trivial parse time, no GC
pressure.

**When to revisit:** if any JSON file approaches **50–100 MB**, switch to
`JsonSerializer.DeserializeAsync<T>(FileStream)` to avoid a single large string
allocation on the Large Object Heap. The synchronous path allocates ~2× file
size in memory (UTF-16 string + object graph); the async/stream path avoids
the intermediate string entirely.

**Sharding:** at 100 MB+ consider splitting into multiple files or moving to
SQLite/LiteDB. Unlikely for Phosphor's current data volumes, but worth noting
if cached chapter data or result caches grow significantly.

**Trade-offs of the async/stream approach:**

| Concern | Impact |
|---------|--------|
| Slower for small files | Async state machine and buffer management add overhead. For files under ~1 MB the synchronous `string` path is faster (microseconds vs. low milliseconds). |
| Sync-only callers | Callers must be `async`. Using `.GetAwaiter().GetResult()` on a UI thread risks deadlocks, so the call chain may need restructuring. |
| Error diagnostics | Stream-based errors report byte positions rather than line/column, making malformed-JSON debugging slightly harder. |
| UTF-8 requirement | Stream path feeds UTF-8 bytes directly (skipping the UTF-16 `string` intermediate — actually an advantage). JSON spec requires UTF-8 anyway, so not a practical issue. |
| No random access | Stream is read forward-only. Fine for deserialization, but partial parsing or retries would need a stream position reset. |
| Code complexity | Minimal — one-line change plus `using var stream = File.OpenRead(...)`. |

**Conclusion:** at current file sizes the synchronous path is faster and simpler.
Only switch when LOH avoidance outweighs the async overhead (50+ MB).

