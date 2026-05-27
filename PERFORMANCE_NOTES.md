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

### `BlobPatternBase.ApplyAudioReactive` — direct scale assignment
- Audio tick fires at ~60 Hz; `ReactiveSpeedMs` defaults to 120 ms. Each tick
  was creating 2 `DoubleAnimation` objects per blob that were replaced 16 ms
  later — ~7/8 of frames were wasted.
- Replaced with direct `ScaleTransform.ScaleX/Y = scale` assignment. Clears
  any in-flight animation once via `HasAnimatedProperties` guard.
- `FerrofluidClusterPattern` calls `base.ApplyAudioReactive` → inherits fix.

### `FractalBoxPattern.ApplyAudioReactive` — ease + transform lookup
- Replaced per-call `new QuadraticEase` with the base class's static frozen
  `_reactiveEase` (changed from `private` to `protected`).
- Direct `ScaleX/Y` assignment instead of per-blob `BeginAnimation`.
- `ScaleTransform` ref cached on `BlobState.CachedScaleTransform` (new field)
  to avoid scanning `TransformGroup.Children` every tick.
- Canvas-level blur animation kept as `BeginAnimation` (one per canvas, not
  per blob — acceptable).

---

## 🔴 High-impact, still open

### 1. `BounceSimulator.FlashBlob` — DispatcherTimer per collision
Every blob-blob collision allocates a `DispatcherTimer`. Busy frames can fire
several at once; each is a `DependencyObject` and a dispatcher queue insertion.

**Fix idea:** add a `FlashUntilTicks` field to `BlobState`; let the existing
`OnRendering` tick restore opacity when expired.

### 2. `LightCycleSimulator.CheckTrailCollision` — O(cycles × segments)
Each alive cycle checks every segment of every cycle per frame. Segments grow
unbounded between deaths. Dominant CPU cost in this pattern at long lifetimes.

**Fix idea:** spatial hash, or — since segments are strictly H or V — split
into two lists keyed by row/column bucket and short-circuit.

---

## 🟠 Medium-impact

### 5. `MatrixBlobPattern.PulseDominantColor` — animation count
Up to 400 simultaneous `ColorAnimationUsingKeyFrames` per pulse, each with a
`Completed` lambda. Pulses can stack if bands change rapidly.

**Fix idea:** decay manually in the per-tick loop using a `PulseAmount` field
on `TrailChar`, eliminating WPF storyboards entirely.

### 6. `MatrixBlobPattern.PickNonOverlappingX` — LINQ + lambdas
```csharp
Enumerable.Range(0, bandCount).OrderBy(_ => _rng.Next()).ToList();
```
Allocates iterator + list + N delegates per call. Fires on every column
respawn.

**Fix idea:** in-place Fisher–Yates on a reused `int[]`.

### 7. `OrbitalBlobPattern` / `Fractal` / `LavaLamp` / `Random` / `FractalBox`
   — `_blobs.IndexOf(blob)` on every animation completion
O(n) lookup. Fires every 10–25 s per blob, so low frequency, but trivially
fixable.

**Fix idea:** stash the index on `FrameworkElement.Tag` or use a
`Dictionary<FrameworkElement, int>`.

### 8. Easing allocations across patterns
`RandomBlobPattern`, `OrbitalBlobPattern`, etc. allocate fresh
`SineEase`/`CubicEase` per retarget.

**Fix idea:** one static frozen instance per ease, shared.

### 9. `MatrixBlobPattern` uses `DispatcherTimer` @ 33 ms
Not vsync-aligned; can drift/judder under load. Every other simulator uses
`CompositionTarget.Rendering`.

**Fix idea:** switch to `CompositionTarget.Rendering` + `Stopwatch` for `dt`
(matches `BounceSimulator`/`LightCycleSimulator`).

### 10. `BlobPatternBase.CreateBlobs` — per-blob `BitmapCache(0.5)`
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
