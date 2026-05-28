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
