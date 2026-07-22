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

---

## WPF Multi-Window Rendering Architecture

> Key insights from a performance investigation of topper window hitching when
> running 4 STA windows simultaneously.

### WPF Has ONE Render Thread Per Process
- Each `Window` on its own STA thread only parallelizes **input, layout, and
  data binding**.
- All visual composition for every window funnels through a **single MIL render
  thread** managed by `MediaContext`.
- Adding more STA threads does **not** scale rendering — the render thread is
  the shared bottleneck across all four windows.
- Any per-frame work on any window (blur effects, cache invalidation, brush
  animation) steals budget from every other window's composition.

### Visibility States and Render Cost
| State | Layout | Render | Animations |
|---|---|---|---|
| `Visible` | yes | yes | tick + paint |
| `Hidden` | yes | **skipped** | storyboards/timers still tick |
| `Collapsed` | **skipped** | **skipped** | storyboards/timers still tick if alive |

- Backglass `IdleOverlay` is set to `Collapsed` during video playback — render
  cost is essentially zero.
- Topper always shows the logo/blobs — it always pays the render cost, making
  it the most visible victim of render-thread contention.
- Even when `Collapsed`, `DispatcherTimer` callbacks and
  `CompositionTarget.Rendering` handlers still fire. Dispose patterns or stop
  timers explicitly to reclaim CPU.

---

## ✅ Done — Logo / Topper Composition Optimizations

### DropShadowEffect + BitmapCache Invalidation Fix
- A `DropShadowEffect` on a `BitmapCache`d element is cheap in steady state
  (cache hit = one textured quad).
- **But** any change to a child (brush color animation, layout) **invalidates
  the entire cache**, forcing a full re-rasterization through the blur kernel
  on the render thread.
- Cost scales with cache surface area × blur radius. A full-window cache at
  1920×1080 with BlurRadius=7 is extremely expensive per invalidation.
- **Fix:** Strip `Effect` + `CacheMode` during color morph animations; restore
  after completion via ref-counted `DispatcherTimer`. Applied to both
  `TopperWindow` and `BackglassWindow`.

### Shared Brush Animation
- Title text shares one `SolidColorBrush` across all glyphs, but
  `ApplyMorphColors` was calling `BeginAnimation` once per character (~20×),
  each replacing the previous.
- **Fix:** Animate the shared brush exactly once.

### Shrunk Cached Surfaces
- Inner title canvas was sized to the full window (`Width = canvas.ActualWidth,
  Height = canvas.ActualHeight`). In fullscreen that's a huge texture.
- **Fix:** Sized to `radius * 2 + padding + fontSize * 2` (≈ 250 px), centered
  at the window center. Dramatically reduces the per-invalidation cost.

### LogoShadow Toggle
- New `AppSettings.LogoShadow` (default `true`) with a Visual-tab checkbox.
- When off: skip both `DropShadowEffect` and `BitmapCache` entirely on topper
  and backglass. Zero shadow/cache cost, no post-morph re-raster spike.
- When on: cache works on the now-small surface; morph stripping handles the
  animation window.

---

## Common Multi-Monitor / Cabinet Issues

### Multi-GPU Composition
- Cabinets often have monitors on different GPUs (e.g., topper on iGPU,
  playfield on dGPU).
- WPF allocates D3D surfaces on the primary adapter; DWM copies each window's
  swap chain across adapters.
- Cross-adapter copies stall the render thread.
- **Check:** Windows Settings → Display → Advanced display → verify all
  monitors are on the same GPU.

### Refresh Rate Mismatch
- WPF's render loop presents at the primary monitor's refresh rate.
- If the topper is 60 Hz and the playfield is 120/144 Hz (or vice versa),
  uneven frame timing causes periodic hitches.
- Matching refresh rates helps but isn't always practical on a cab.

### ProjectM (OpenGL) GPU Contention
- ProjectM runs on a dedicated background thread with its own OpenGL context —
  **no WPF render thread cost** when using the D3DImage fast path.
- However, heavy presets at high `MeshSize` / `RenderScale` can saturate the
  GPU, starving WPF composition for all windows.
- The fallback `WriteableBitmap` + `glReadPixels` path *does* hit the WPF UI
  thread. Verify the log shows `PBO ASYNC READBACK (D3DImage)` not `FALLBACK`.

---

## How to Measure WPF Performance (.NET 8)

> **Note:** The classic WPF Performance Suite (`Perforator.exe` / `WpfPerf.exe`)
> is obsolete and does **not** work with .NET 8 WPF apps. The tools below are
> the current recommended alternatives.

### Quick Diagnostics

**Rendering Tier:**
```csharp
int tier = System.Windows.Media.RenderCapability.Tier >> 16;
// 0 = software, 1 = partial HW, 2 = full HW acceleration
```
Confirm tier 2 on the target cabinet.

### 1. Visual Studio Performance Profiler (Best Starting Point)
1. **Debug → Performance Profiler** (Alt+F2).
2. Select **CPU Usage** + **.NET Async** tools.
3. Check **"Enable native code debugging"** for mixed-mode profiling — this is
   critical because the WPF render thread lives in native code.
4. Run the app, trigger the scenario (morph, fullscreen toggle, pattern change).
5. Look for hot paths in `System.Windows.Media.*` and
   `System.Windows.Threading.*`.
6. The render thread shows up as `wpfgfx_cor3.dll` in native call stacks.
   Key symbols to watch:
   - `CPartitionThread::RenderPartition` — render thread work per frame.
   - `CDrawingContext` — per-element drawing calls.

### 2. DIY Frame-Rate Monitor (Code-Based)
Add to any window to measure actual composition frame rate. No tools needed.
```csharp
#if DEBUG
private int _frameCount;
private DateTime _lastFpsCheck = DateTime.UtcNow;

private void OnRendering(object? sender, EventArgs e)
{
    _frameCount++;
    var now = DateTime.UtcNow;
    if ((now - _lastFpsCheck).TotalSeconds >= 1.0)
    {
        Title = $"Topper — {_frameCount} fps";
        _frameCount = 0;
        _lastFpsCheck = now;
    }
}

// Hook in constructor or StartAnimation:
System.Windows.Media.CompositionTarget.Rendering += OnRendering;
#endif
```

### 3. DIY Cache Invalidation Detector
No dirty-region overlay exists for .NET 8. Use conditional coloring to visually
confirm when a `BitmapCache`d surface is being re-rasterized:
```csharp
#if DEBUG
// Add to the cached inner canvas — flickers color on every invalidation
inner.LayoutUpdated += (_, _) =>
    inner.Background = new SolidColorBrush(Color.FromArgb(30,
        (byte)Random.Shared.Next(256),
        (byte)Random.Shared.Next(256),
        (byte)Random.Shared.Next(256)));
#endif
```
If the tint flickers, the cache is being invalidated — the exact problem to
look for with `DropShadowEffect` + `BitmapCache`.

### 4. Visual Studio Live Visual Tree
- **Debug → Windows → Live Visual Tree** (Ctrl+Alt+B while debugging).
- Shows real-time element tree, layout dimensions, render bounds.
- Hover elements in the tree to highlight them in the running app.
- Good for verifying element dimensions (e.g., confirming the inner canvas is
  ~250 px, not full-window).
- **Limitation:** no dirty-region overlay or FPS counter.

### 5. ETW Tracing via `dotnet-trace`
```powershell
dotnet-trace collect --process-id <PID> --providers Microsoft-Windows-WPF
```
Captures WPF-specific ETW events including render thread timing. Open the
`.nettrace` file in VS or PerfView.

### 6. `dotnet-counters` (GC / Thread Pool Monitoring)
```powershell
dotnet-counters monitor --process-id <PID> --counters System.Runtime
```
Not WPF-specific but catches GC stalls that cause hitches. Watch for
`gc-pause-time-ratio` and `gen-2-gc-count`.

### 7. PerfView (Deep WPF / ETW Analysis)
1. Download [PerfView](https://github.com/microsoft/perfview/releases).
2. Collect: `PerfView.exe /GCCollectOnly /ThreadTime collect`
3. Look for:
   - `wpfgfx_cor3!CPartitionThread::RenderPartition` — render thread work.
   - `wpfgfx_cor3!CMilChannel::ProcessCommandBatch` — UI→render thread
     command batching.
4. Filter to the process and examine render thread utilization vs. wall clock.

### 8. GPU Profiling
- **Task Manager → Performance → GPU:** check per-engine utilization. If "3D"
  is pegged, GPU contention is the issue, not the WPF render thread.
- **GPUView** (Windows SDK): shows present queues per window, cross-adapter
  copies, and DWM composition timing. Best tool for diagnosing multi-monitor
  DWM stalls. Works regardless of .NET version (it's a driver/DWM tool).
- **NVIDIA Nsight / AMD Radeon GPU Profiler:** for ProjectM shader-level
  profiling if GPU contention is suspected.

### Symptom → Tool Guide

| Symptom | Likely cause | Tool |
|---|---|---|
| One window hitches, others smooth | Cross-adapter copy or cache invalidation | GPUView, DIY cache detector |
| All windows hitch together | Render thread saturated | PerfView (`wpfgfx_cor3` thread), VS Profiler |
| Hitch during color morph | DropShadowEffect re-raster on invalidation | DIY cache detector |
| Hitch when window goes fullscreen | Large BitmapCache surface allocation | Live Visual Tree (check dimensions) |
| Smooth at 60 Hz, periodic stutter | DWM refresh-rate arbitration | GPUView present timing |
| ProjectM causes all windows to stutter | GPU contention (shader-bound) | Task Manager GPU, reduce MeshSize/RenderScale |
| Periodic pauses, not frame-rate related | GC stalls | `dotnet-counters`, PerfView GC analysis |

---

## Future Optimization Candidates (If Needed)

- **Separate process for playfield:** gives it its own WPF render thread. Same
  pattern as `DofBridge`. Communicate via named pipe / shared memory. See
  detailed analysis in [External Playfield Process](#external-playfield-process)
  below.
- **Stop blob patterns during video playback:** dispose pattern + stop timers
  when `IdleOverlay` is collapsed to save CPU on the UI thread.
- **RenderAtScale < 1.0:** `BitmapCache { RenderAtScale = 0.75 }` reduces
  cache texture size with minimal visible quality loss on blurred elements.
- **Pre-rendered shadow layer:** replace `DropShadowEffect` with offset black
  TextBlocks as a static shadow — never invalidates, zero shader cost.

---

## External Playfield Process

Architecture notes for running the playfield window as a separate OS process,
toggled by a `UseExternalPlayfield` setting in `AppSettings`.

### How it would work

A new project — e.g. `Phosphor.Playfield` — launches as a standalone WPF
.NET 8 EXE. The main `Phosphor` app spawns it (same pattern as `DofBridge`)
and communicates over a named pipe (`PhosphorPlayfield`). The setting
`UseExternalPlayfield` in `AppSettings` toggles between in-process (current)
and external.

### Code sharing: extract a library

The visualization code cannot stay in the main app — the external EXE needs
it. The cleanest approach:

1. **New class library: `Phosphor.Visuals`** (.NET 8) containing:
   - `IBlobPattern`, `BlobPatternBase`, all pattern implementations
     (`Visuals/Patterns/*`)
   - `AudioReactiveService` (WASAPI capture)
   - `BlobMotion`, `BlobTransition`, `BounceSimulator`, etc.
   - `RoygbivColor`, `ColorAnalysis`, `FrameColorAnalyzer`
   - The `BlobPattern` enum and pattern factory

2. **`Phosphor.Playfield` EXE** references `Phosphor.Visuals`. Contains only:
   - A stripped-down `PlayfieldWindow` (canvas, blob rendering, OLED defeat)
   - Named pipe server/client for receiving commands
   - Audio capture (runs its own `AudioReactiveService` instance — it captures
     system audio via WASAPI loopback, so it works from any process)

3. **Main `Phosphor` app** also references `Phosphor.Visuals` (for Backglass,
   Topper, DMD patterns — those stay in-process).

### Named pipe protocol (sketch)

Pipe name: `PhosphorPlayfield`

| Direction | Command | Payload |
|-----------|---------|---------|
| Main → Playfield | `SetPattern` | `BlobPattern` enum value, blob count, intensity, speed |
| Main → Playfield | `SetLayout` | left, top, width, height, monitor |
| Main → Playfield | `SetColorCycling` | bool + hue offset |
| Main → Playfield | `SetOledDefeat` | interval, intensity |
| Main → Playfield | `Shutdown` | — |
| Playfield → Main | `ColorBand` | `ColorAnalysis` (dominant color, brightness, selfRendering flag) |
| Playfield → Main | `Heartbeat` | alive signal |

Binary protocol similar to DofBridge's — `BinaryWriter`/`BinaryReader` with a
command char prefix.

### Dominant color & DOF

The playfield process computes `ColorAnalysis` locally (it already does this
in `PlayfieldWindow` via `BlobColorBandChanged`) and sends it back over the
pipe. The main app's `DmdWindow.OnPlayfieldColorBandChanged` receives it and
fires DOF triggers as it does today. The only change is the event source
switches from a cross-thread dispatcher invoke to a pipe message.

### Visualization compatibility

| Visualization | External process? | Notes |
|---------------|-------------------|-------|
| All blob patterns | ✅ Yes | Render on a WPF `Canvas` — fully self-contained once `Phosphor.Visuals` is extracted |
| ProjectM | ✅ Yes | Self-contained P/Invoke to `projectM-4.dll`, owns its OpenGL context. `AudioReactiveService.ConsumeRawPcm()` works locally |
| Mandelbrot (GPU) | ✅ Yes | `MandelbrotGpuRenderer` uses SharpDX/Direct2D — process-local GPU resources |
| Mandelbrot (CPU) | ✅ Yes | Pure computation, no shared state |

All visualizations would work. They're already designed to be self-contained
per-window — each window creates its own pattern instance, owns its own
canvas, and runs its own render tick. The only cross-process dependency is
settings/commands inbound and color analysis outbound.

### Drawbacks & risks

1. **Significant refactoring scope.** Extracting `Phosphor.Visuals` means
   moving ~25 files, updating namespaces, and ensuring no remaining
   dependencies on `JukeboxViewModel` or window-specific types. The patterns
   currently reference WPF types (`Canvas`, `FrameworkElement`,
   `SolidColorBrush`) which is fine for a .NET 8 WPF class library, but any
   accidental coupling to `PlayfieldWindow`-specific logic would need
   untangling.

2. **Two code paths.** The `UseExternalPlayfield` toggle means maintaining
   both in-process and external modes. `PlayfieldProxy` would need an
   alternative implementation (`ExternalPlayfieldProxy`?) that talks over the
   pipe instead of dispatching to a local window. Testing both paths doubles
   QA surface.

3. **Settings synchronization.** Today, changing blob intensity or pattern in
   the settings UI immediately dispatches to the playfield thread. With an
   external process, every setting change needs a pipe message, and you need
   to handle the process not being ready yet, crashing mid-session, etc. The
   DofBridge pattern handles this (reconnect logic in `DofClient`), but it's
   more code.

4. **Startup latency.** Spawning a second WPF process adds ~1-2s to startup.
   DofBridge is lightweight (console app); a full WPF window with GPU
   resources is heavier.

5. **Debugging complexity.** Two WPF processes rendering simultaneously —
   harder to attach debuggers, harder to correlate logs, harder to reproduce
   issues.

6. **Marginal perf gain if the GPU is the bottleneck.** The playfield already
   runs on its own thread/dispatcher. A separate process gives it its own WPF
   render thread (composition thread), which helps if the WPF compositor is
   the bottleneck. But if the GPU is saturated (e.g., ProjectM + Mandelbrot),
   a separate process doesn't help — same GPU.

### Verdict

The architecture is feasible and clean — the DofBridge pattern is well-proven,
all visualizations are self-contained, and the library extraction is
mechanical. The main question is whether the perf gain justifies the
complexity. The win is specifically: **a dedicated WPF render/composition
thread** that can't be starved by the main app's UI work. If the playfield
stutters because the main thread's composition pass takes too long, this fixes
it. If the bottleneck is GPU shader time, it won't help.

**Recommendation:** Before committing to this, profile with PresentMon to
confirm the playfield's stalls correlate with main-thread compositor
contention rather than GPU saturation. If confirmed, the extraction is worth
it.

### Profiling with PresentMon

PresentMon captures ETW present events and produces structured frame timing
CSVs — ideal for answering the compositor-vs-GPU question.

#### Capture

```powershell
PresentMon.exe --output_file phosphor_frames.csv --terminate_on_proc_loss --process_name Phosphor.exe
```

Run playfield patterns for 30-60 seconds, then stop. To capture all processes
(useful for correlating with other apps), omit `--process_name`.

#### Key columns in the CSV

| Column | What it tells you |
|--------|-------------------|
| `MsBetweenPresents` | Frame-to-frame interval. Consistent = healthy; spikes = stutter |
| `MsInPresentAPI` | Time spent in the Present call. High = CPU/compositor bottleneck |
| `MsUntilDisplayed` | End-to-end latency to screen. High + low `MsInPresentAPI` = GPU-bound |
| `MsBetweenDisplayChange` | Actual display refresh intervals — shows dropped frames |

#### Decision matrix

1. **Spikes in `MsBetweenPresents` + high `MsInPresentAPI`** → WPF
   compositor/CPU submission is the bottleneck. External process gives the
   playfield its own render thread → **would help**.

2. **Spikes in `MsBetweenPresents` + low `MsInPresentAPI` + high
   `MsUntilDisplayed`** → GPU-bound. GPU can't finish frames fast enough.
   External process **won't help** — same GPU.

3. **Consistent `MsBetweenPresents` ~16.7 ms** → No problem to solve yet.

#### v2 metrics (`--v2_metrics`)

With `--v2_metrics`, column names change. These are the relevant ones:

| v2 Column | Meaning | What to watch |
|-----------|---------|---------------|
| `FrameTime` | Total frame-to-frame interval (ms) | Spikes = stutter |
| `CPUBusy` | CPU time actively working on the frame | Spikes here = CPU/compositor stall |
| `CPUWait` | CPU idle waiting (for GPU or vsync) | High = GPU-bound or vsync-limited |
| `GPUBusy` | Time the GPU spent rendering | Consistently high = GPU-bound |
| `GPUWait` | GPU idle waiting for work | High = CPU can't feed GPU fast enough |
| `GPUTime` | Total GPU pipeline time | Overall GPU load |
| `DisplayLatency` | Full pipeline latency to screen | Overall health metric |
| `DisplayedTime` | When the frame actually appeared | Gaps = dropped frames |
| `AllowsTearing` | Whether tearing is permitted | Confirms vsync mode |

**v2 decision matrix:**

1. **`FrameTime` spikes + `CPUBusy` high + `GPUBusy` low** →
   Compositor/CPU-bound. External process **would help**.

2. **`FrameTime` spikes + `GPUBusy` high + `CPUWait` high** → GPU-bound.
   External process **won't help**.

3. **Gaps in `DisplayedTime` correlating with high `CPUBusy`** → Smoking gun
   for compositor contention.

4. **`GPUWait` high + `CPUBusy` low** → GPU starved for work, possible
   driver/scheduling issue.

**Quick analysis (PowerShell):**

```powershell
# Capture
PresentMon.exe --v2_metrics --output_file phosphor_v2.csv --process_name Phosphor.exe

# Find stutter frames (>20ms = missed 60Hz vsync)
Import-Csv phosphor_v2.csv |
    Where-Object { [double]$_.FrameTime -gt 20 } |
    Select-Object FrameTime, CPUBusy, CPUWait, GPUBusy, GPUWait, DisplayLatency |
    Format-Table
```

If stuttery frames consistently show `CPUBusy` >> `GPUBusy`, the external
process is worth building.

#### Note on WPF process architecture

Since Phosphor's playfield and backglass run on separate threads but in the
same process, they share one WPF compositor — PresentMon shows one stream of
presents. With the external process, PresentMon would show two separate
process rows, which itself confirms independent composition.

### Empirical Profiling Results (June 2025)

Three PresentMon v2 captures were taken to isolate the bottleneck:

| Dataset | Configuration | Steady-state `FrameTime` | `CPUBusy` | `GPUBusy` | Verdict |
|---------|---------------|--------------------------|-----------|-----------|---------|
| 1 | Everything (all windows, all visuals) | ~27 ms | ~27 ms (≈ FrameTime) | Low / idle | CPU/compositor-bound |
| 2 | No playfield, no topper; backglass blobs near zero; only backglass + DMD | ~27 ms | ~27 ms (≈ FrameTime) | Low / idle | **Still** CPU/compositor-bound |
| 3 | Same as 2, but reactive audio + morphing logo color cycling disabled | Single row captured | — | — | Outlier (too few samples) |

#### Key takeaway

**Window count and blob complexity are not the dominant factor.** Dataset 2
removed the playfield, the topper, and nearly all backglass blobs — yet the
steady-state profile was essentially identical to dataset 1. The CPU was
still pegged at ~27 ms per frame with the GPU mostly idle.

Dataset 3 produced only a single PresentMon row (essentially one frame
captured before the tool stopped), so it cannot be treated as a steady-state
measurement. However, the fact that it captured so few presents — when
reactive audio and morphing logo color cycling were disabled — is itself
suggestive: with those features off, the compositor had almost nothing to
re-render, meaning far fewer presents were issued.

**Primary suspects: reactive audio visualization and/or morphing logo color
cycling.** These features likely drive frequent render invalidation (e.g.,
per-tick property changes, brush updates, or layout passes) that keep the
WPF compositor busy even when the visual tree is small.

#### Revised assessment of external playfield process

Given that removing the playfield window entirely (dataset 2) did not
improve the profile, launching the playfield as a separate process is
**unlikely to help on its own**. The bottleneck appears to be in features
that run on the backglass/DMD windows, not compositor contention from
multiple windows sharing a process.

The external-process architecture remains a valid option for other reasons
(crash isolation, independent scaling), but the immediate performance win is
more likely to come from optimizing reactive audio and/or logo color cycling
invalidation patterns.

### Isolation Test Results (June 2025)

Each test ran for 1 minute with PresentMon `--v2_metrics`. Configuration:
backglass + DMD only (no playfield, no topper), minimal blobs.

#### Test matrix

| Test | Reactive Audio | Logo Color Cycling | Purpose |
|------|---------------|-------------------|---------|
| A | ✅ ON | ❌ OFF | Isolate reactive audio impact |
| B | ❌ OFF | ✅ ON | Isolate logo color cycling impact |
| C | ✅ ON | ✅ ON | Baseline (both on) |

#### Raw results

**Test A — Reactive audio ON, logo color cycling OFF** (9 frames in 60 s)

| FrameTime | CPUBusy | CPUWait | GPUBusy | GPUWait | DisplayLatency |
|-----------|---------|---------|---------|---------|----------------|
| 29.11 | 28.99 | 0.12 | 0.00 | 0.00 | 47.45 |
| 29.10 | 28.93 | 0.18 | 2.12 | 0.78 | 47.57 |
| 22.34 | 22.14 | 0.20 | 1.71 | 0.00 | 40.41 |
| 20.65 | 20.46 | 0.19 | 1.34 | 5.97 | 40.37 |
| 31.94 | 31.80 | 0.14 | 0.00 | 0.00 | 47.49 |
| 30.50 | 30.39 | 0.11 | 0.01 | 0.00 | 47.06 |
| 29.03 | 28.91 | 0.13 | 0.00 | 0.00 | 46.92 |
| 22.56 | 22.24 | 0.32 | 1.67 | 0.38 | 40.28 |
| 23.05 | 22.88 | 0.17 | 1.25 | 0.00 | 40.93 |

**Test B — Reactive audio OFF, logo color cycling ON** (12 frames in 60 s)

| FrameTime | CPUBusy | CPUWait | GPUBusy | GPUWait | DisplayLatency |
|-----------|---------|---------|---------|---------|----------------|
| 23.40 | 23.26 | 0.14 | 0.00 | 0.00 | 33.36 |
| 23.37 | 23.18 | 0.19 | 4.37 | 15.05 | 33.48 |
| 20.05 | 19.93 | 0.12 | 0.09 | 0.00 | 45.35 |
| 20.05 | 19.86 | 0.19 | 4.75 | 12.81 | 31.61 |
| 21.60 | 21.49 | 0.12 | 0.00 | 0.00 | 33.37 |
| 21.66 | 21.47 | 0.19 | 1.63 | 0.00 | 33.55 |
| 22.64 | 22.46 | 0.18 | 3.56 | 0.00 | 33.41 |
| 20.43 | 20.31 | 0.12 | 0.00 | 0.00 | 27.46 |
| 23.68 | 23.55 | 0.13 | 0.06 | 0.00 | 40.98 |
| 23.67 | 23.50 | 0.16 | 2.48 | 14.32 | 34.14 |
| 41.89 | 41.77 | 0.12 | 0.00 | 0.00 | 52.56 |
| 41.88 | 41.67 | 0.21 | 5.90 | 13.89 | 52.67 |

**Test C — Both ON** (25 frames in 60 s)

| FrameTime | CPUBusy | CPUWait | GPUBusy | GPUWait | DisplayLatency |
|-----------|---------|---------|---------|---------|----------------|
| 36.58 | 36.47 | 0.11 | 0.07 | 0.00 | 54.48 |
| 36.58 | 36.39 | 0.19 | 2.07 | 1.11 | 54.59 |
| 24.54 | 24.41 | 0.14 | 0.00 | 0.00 | 33.58 |
| 24.52 | 24.29 | 0.23 | 5.82 | 0.00 | 33.70 |
| 23.80 | 23.55 | 0.25 | 1.21 | 13.96 | 36.61 |
| 27.86 | 27.72 | 0.14 | 0.00 | 0.00 | 43.38 |
| 42.81 | 42.62 | 0.20 | 1.43 | 0.00 | 61.04 |
| 24.39 | 24.26 | 0.13 | 0.00 | 0.00 | 33.58 |
| 24.37 | 24.13 | 0.24 | 4.39 | 16.03 | 33.70 |
| 20.10 | 19.96 | 0.14 | 3.12 | 0.00 | 40.53 |
| 37.82 | 37.67 | 0.15 | 0.00 | 0.00 | 48.94 |
| 37.81 | 37.57 | 0.24 | 1.78 | 14.78 | 49.09 |
| 38.57 | 38.38 | 0.19 | 0.01 | 0.00 | 49.75 |
| 38.52 | 38.27 | 0.25 | 1.98 | 14.65 | 49.90 |
| 30.32 | 30.19 | 0.14 | 0.00 | 0.00 | 42.20 |
| 30.30 | 30.08 | 0.22 | 1.66 | 14.16 | 42.32 |
| 23.15 | 22.97 | 0.19 | 6.51 | 14.81 | 33.50 |
| 23.17 | 23.03 | 0.15 | 0.00 | 0.00 | 33.37 |
| 38.87 | 38.75 | 0.12 | 0.00 | 0.00 | 50.88 |
| 38.87 | 38.67 | 0.20 | 1.76 | 14.02 | 51.00 |
| 22.72 | 22.60 | 0.12 | 0.04 | 0.00 | 40.24 |
| 22.69 | 22.46 | 0.23 | 2.03 | 1.12 | 40.34 |
| 21.63 | 21.48 | 0.15 | 0.02 | 0.00 | 40.88 |
| 27.78 | 27.64 | 0.14 | 0.02 | 0.00 | 47.63 |

#### Summary statistics

| Test | Frames | Avg FrameTime | Avg CPUBusy | Avg DisplayLatency | Max FrameTime |
|------|--------|---------------|-------------|--------------------|----|
| A (audio only) | 9 | 26.5 ms | 26.3 ms | 44.3 ms | 31.9 ms |
| B (color cycling only) | 12 | 25.3 ms | 25.1 ms | 37.6 ms | 41.9 ms |
| C (both) | 25 | 29.9 ms | 29.7 ms | 43.5 ms | 42.8 ms |

#### Analysis

**Both features contribute, but they behave differently:**

1. **Reactive audio (test A)** produces fewer presents (9 in 60 s) but each
   frame is expensive (~26.5 ms avg). The CPU is nearly 100% utilized per
   frame. It creates fewer but heavier render passes.

2. **Logo color cycling (test B)** produces more presents (12 in 60 s) and
   has a wider variance — most frames are ~20-23 ms but with periodic
   spikes to ~42 ms. It drives more frequent invalidation with occasional
   heavy frames.

3. **Both together (test C)** compounds the problem significantly: 25
   frames in 60 s with an average of ~30 ms and peaks above 42 ms. The
   frame count nearly triples versus either feature alone, confirming
   that the two features trigger independent invalidation cycles that
   stack.

**The compounding effect is the real problem.** Neither feature alone is
catastrophic, but together they create a cascading invalidation pattern
where each feature's property changes trigger re-renders that overlap with
the other's, roughly tripling the compositor workload.

**Logo color cycling has slightly more impact on peak latency** (42 ms
spikes in test B vs 32 ms max in test A) and drives higher invalidation
frequency, making it the higher-priority optimization target. However,
reactive audio's per-frame cost is also substantial.

### Next Steps — Optimization

Both features need optimization, prioritized by impact:

#### Priority 1: Logo color cycling

- **Investigate the brush/color update path.** Each color cycle step likely
  creates new `SolidColorBrush` instances or triggers layout — find the
  update code and check for unnecessary allocations.
- **Throttle update frequency.** If colors are cycling on every tick or
  timer callback, gate updates to 15-20 Hz max.
- **Use WPF `ColorAnimation` or `Storyboard`** instead of manual property
  sets — these run on the composition thread and avoid re-rendering the
  visual tree.

#### Priority 2: Reactive audio

- **Batch property changes.** If multiple visual properties update per
  audio callback (scale, opacity, color), batch them into a single
  dispatcher call to avoid multiple render passes.
- **Throttle the audio callback.** Audio data often arrives at 44.1 kHz
  sample rate — the visual update should be gated to at most 30 Hz.
- **Use `CompositionTarget.Rendering`** as the update driver instead of
  audio-event-driven updates, to align with the compositor's natural
  cadence.

#### Priority 3: Investigate invalidation overlap

- When both features are on, determine if they share any visual elements
  that get double-invalidated. If the logo visuals respond to both color
  cycling AND reactive audio, a single coordinated update path could
  eliminate the compounding effect.

---

## 🟣 Future — yt-dlp / DASH Integration for Reliable YouTube Streaming

### Problem Statement

Phosphor uses [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) to
resolve YouTube stream URLs, which gives us **progressive DASH streams** —
a single monolithic URL that returns the entire file as one HTTP response.
This works for sequential playback but has two well-documented failure modes:

1. **Forward seeks are unreliable.** The progressive container's seek index
   (cues for webm, moov for mp4) lives at the *end* of the file. VLC has no
   way to map a target time to a byte offset without first downloading the
   whole stream. The result: forward scrubbing into uncached content often
   wedges the VLC decoder on a non-keyframe (see `BackglassWindow.OnSeekRequested`
   for our detection-and-restart recovery).

2. **Tail-end CDN throttling.** googlevideo rate-limits the last few seconds
   of progressive streams. As VLC's read pointer reaches the throttled tail,
   the demuxer can briefly stall — observed as a ~3-4 second time-label
   regression and minor decode stutter near the end of uncached playback.
   See the workaround note in BackglassWindow's position-timer comments.

Both issues vanish when the video is cached as a remuxed local MKV (which
is exactly what the persistent / transient / preemptive caching paths do).
But streaming-only playback remains a degraded experience.

### Root Cause: Wrong Protocol

YouTube's *web player* doesn't use progressive streams. It uses **MPEG-DASH**
(Dynamic Adaptive Streaming over HTTP). Key differences:

| Feature | Progressive (what we use) | DASH (what the web player uses) |
|---------|---------------------------|---------------------------------|
| URL shape | Single URL → entire file | Manifest URL → many small `.m4s` segments (2-10s each) |
| Seek index | At end of file; unusable until full download | Explicit byte-range index in the manifest |
| Forward seek | Decoder must walk packets from start | Single HTTP request for target segment |
| Bitrate | Fixed at selection time | Adaptive — switches segments to match bandwidth |
| Resilience | Whole stream is one HTTP connection | Per-segment retries, no cascading failure |

DASH is built for seeking. Each segment starts with a keyframe and is
independently decodable. VLC's `dash` demuxer handles DASH manifests
natively — there's no client-side work beyond pointing it at the manifest URL.

### Why YoutubeExplode Doesn't Expose DASH

YouTube's DASH manifest URLs require **signature decryption** — they're
returned by the player config endpoint with a `s=` parameter that has to
be transformed by interpreting a JavaScript function YouTube embeds in
the player. YouTube changes this function frequently (sometimes weekly)
specifically to break scrapers. Maintaining a signature decryptor is a
full-time chase. YoutubeExplode focuses on progressive streams (which
don't need signature decryption) and has historically declined to take
on DASH for this reason.

[yt-dlp](https://github.com/yt-dlp/yt-dlp) does exactly the maintenance
work YoutubeExplode avoids. It has a team of contributors continuously
updating signature handling, format selectors, and per-platform quirks.
That's why most media players (Kodi, mpv front-ends, JellyfinPlayer,
PinkVD) shell out to yt-dlp for YouTube resolution rather than rolling
their own.

### Proposed Architecture

**Drop yt-dlp.exe into `dependencies/`** (same pattern as `ffmpeg.exe`).
It's ~30 MB, a single Windows binary with Python embedded — no Python
install required on the user's machine.

**New service:** `Phosphor/Services/YtDlpResolver.cs`

```csharp
public class YtDlpResolver
{
    private static readonly string YtDlpPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");

    public static bool IsAvailable => File.Exists(YtDlpPath);

    /// <summary>
    /// Resolves a DASH manifest URL for the given YouTube video.
    /// Returns null if yt-dlp is unavailable or the resolution fails.
    /// </summary>
    public static async Task<string?> ResolveDashManifestAsync(
        string videoId, VideoQualityPreference quality, CancellationToken ct)
    {
        if (!IsAvailable) return null;

        string formatSelector = quality switch
        {
            VideoQualityPreference.Low    => "bestvideo[height<=480]+bestaudio/best",
            VideoQualityPreference.Medium => "bestvideo[height<=720]+bestaudio/best",
            VideoQualityPreference.High   => "bestvideo[height<=1080]+bestaudio/best",
            _ => "bestvideo+bestaudio/best"
        };

        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            Arguments = $"-g --format \"{formatSelector}\" " +
                        $"--no-warnings --no-playlist " +
                        $"https://www.youtube.com/watch?v={videoId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0) return null;

        // -g returns one URL per line: first video, then audio. For DASH playback
        // VLC handles the manifest URL directly. For separate video+audio streams
        // we'd need to feed them as primary + slave like the current YoutubeExplode path.
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : null;
    }
}
```

**Integration point** in `BackglassWindow.OnPlayRequested`, around the
existing YoutubeExplode `GetManifestAsync` call:

```csharp
// Try yt-dlp first if available — gives us DASH URLs with proper seek indices.
// Falls back to YoutubeExplode (progressive streams) if yt-dlp is missing or fails.
string? dashUrl = await YtDlpResolver.ResolveDashManifestAsync(videoId, quality, ct);
if (dashUrl != null)
{
    var media = new Media(_libVLC, new Uri(dashUrl));
    ApplyNetworkOptions(media, vm);
    _lastMuxedStreamUrl = dashUrl;
    _mediaPlayer.Play(media);
}
else
{
    // existing YoutubeExplode path …
}
```

**Settings UI** (Cache tab, alongside `AllowTransientCaching`):
- `EnableYtDlpStreams` checkbox (default: auto-detect based on `IsAvailable`).
- A "Check for yt-dlp updates" button that runs `yt-dlp.exe -U` (yt-dlp
  self-updates with this flag) and reports the version.

### Cost / Benefit Analysis

| Concern | Cost / Risk |
|---------|-------------|
| Binary size | +30 MB in `dependencies/`; bumps installer/clone size noticeably |
| Per-play latency | +200-500 ms for yt-dlp invocation before `Play()` |
| Maintenance | **Primary concern.** Bundled yt-dlp version goes stale; YT may break it after a few months. See "Update strategy" below |
| Process spawning | Some AV / sandboxing setups flag yt-dlp.exe as suspicious (it's a Python interpreter scraping a website). Possible support burden |
| Code complexity | New resolver service + integration branch in OnPlayRequested + settings UI ≈ 150-250 lines |
| Privacy | yt-dlp may make additional metadata requests to youtube.com that YoutubeExplode doesn't |

### Benefits

- **Seek anywhere, anytime** on uncached YouTube content with ~200-500 ms
  re-buffer per seek (single segment fetch). No more "scrub failed → restart
  from beginning" recovery path.
- **No end-of-clip stutter** — DASH segments aren't subject to the same
  tail-throttling as progressive streams.
- **Better quality selection.** yt-dlp exposes the full format ladder
  (including AV1 and Opus tracks YoutubeExplode hides).
- **Future-proofs against YoutubeExplode breakage.** When YT changes
  something fundamental, yt-dlp typically lands a fix within days; YoutubeExplode
  releases lag by weeks-to-months.

### Update Strategy (The Hard Part)

yt-dlp ships releases multiple times per week. The bundled binary will go
stale eventually. Options, in order of operator effort:

1. **Ship it, never update.** Works until YouTube makes a breaking change
   (typically 3-6 months from any given snapshot). On break, all yt-dlp
   resolutions fail; we fall back to YoutubeExplode. User sees degraded
   scrubbing but playback still works. **Lowest effort, highest staleness risk.**

2. **`yt-dlp -U` button in settings.** yt-dlp can self-update its own
   binary by downloading from its GitHub releases. We expose a button;
   the user runs it when they hit issues. **Moderate effort; puts burden
   on user to know to update.**

3. **Auto-update on startup.** Background-check GitHub releases API for a
   newer version once per day; download to a temp location, replace
   `yt-dlp.exe` if Phosphor is restarted. **Highest effort; introduces
   network dependency at startup and need for elevated permissions to
   replace the binary in `Program Files`-style installs.**

4. **Don't ship a binary; require user to install.** Detect yt-dlp in
   `PATH` or a configured location; if missing, disable the feature with
   a settings-tab hint. Many advanced users already have yt-dlp installed.
   **Sidesteps bundling entirely; raises onboarding friction for casual users.**

Recommended path: **option 1 + option 2** — ship a known-good version,
provide a manual update button, and fall back gracefully when yt-dlp fails.
Add option 3 later if breakage frequency justifies the complexity.

### When Not to Do This

- If transient + preemptive caching covers the common usage patterns
  (which it does for jukebox-style sequential play with occasional
  scrubbing), the binary size and maintenance load aren't worth it.
- If users primarily play short cached clips, DASH adds nothing.
- If the team wants to avoid the "shelling out to a third-party scraper
  for a core feature" support burden.

### When to Reconsider

- If complaints about the "seek failed — restarted" recovery path become
  frequent.
- If long-form content (concerts, full albums on YouTube) becomes a major
  use case and the tail-throttling stutter is noticeable.
- If YoutubeExplode hits a prolonged outage from a YouTube breakage.

### References

- yt-dlp GitHub: https://github.com/yt-dlp/yt-dlp
- yt-dlp Windows release: https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe
- Current seek recovery code: `Phosphor/Windows/BackglassWindow.xaml.cs`, `OnSeekRequested`
- Current caching infrastructure to reuse the muxed-output pattern:
  `Phosphor/Caching/VideoCache.cs`, `MuxWithFfmpegAsync`


