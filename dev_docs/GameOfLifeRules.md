# Game of Life — Alternate Rules Engine Findings

## Status

Currently **disabled**. Brian's Brain and Star Wars are implemented in
`GameOfLifePattern.cs` (and the persisted `GameOfLifeRulesEngine` setting still
exists in `AppSettings`), but:

- `DmdWindow.xaml.cs` force-sets `GameOfLifePattern.Rules = Conway` in both
  settings-apply sites and ignores the persisted value.
- The "Rules" combo box in `SettingsWindow.xaml` is `Visibility="Collapsed"`.

To re-enable for further experimentation, restore the original two
`GameOfLifePattern.Rules = (RulesEngine)Math.Clamp(...)` lines in
`DmdWindow.xaml.cs` and remove the `Visibility="Collapsed"` on the Rules
`StackPanel` in `SettingsWindow.xaml`.

## Goal

Add alternate cellular-automaton rule engines (Brian's Brain, Star Wars / "Starry
Night") alongside Conway, selectable from the settings UI, so the Game of Life
visualization could rotate through different rule sets.

## Implementation summary

Two-phase refactor of `Phosphor/Visuals/Patterns/GameOfLifePattern.cs`:

- **Phase 1 — Plumbing.** Introduced a `RulesEngine` enum, a `Rules` static
  property, and an `EraBandedHueSpeed` control. Gated `AntiStagnation` to
  Conway only (the other rules can't form the still-lifes / period-2
  oscillators it targets). Persisted both new fields via `AppSettings`,
  `DmdWindow.xaml.cs`, and `SettingsWindow.xaml(.cs)`.
- **Phase 2 — Rule engines.** Implemented Brian's Brain (B2/S/refractory) and
  Star Wars (B2/S345/with refractory) as EraBanded-only rules sharing a new
  generic bitboard step (`StepRowBitboardMultiState`) using birth/survive
  masks. Conway continues to use its existing dedicated bitboard path
  (`StepRowBitboard`) and is the only rule that also supports the scalar
  Genetic-color path.

## What we tried (and why each failed)

These are B2-birth rules: any cell with exactly two live neighbors is born.
On a bounded grid this turns out to be unforgiving for ambient use.

1. **Reactive injection (Conway-style).** Beat-driven and periodic injection
   immediately saturated the field — every injected cluster instantly births
   a wall of new cells.
2. **Density-gated injection.** Skipping injection when density was already
   high helped, but the field still saturated quickly from the initial seed.
3. **Tiny structured seeds (dominoes / trominoes).** Two- and three-cell
   ignition shapes fire once or twice and burn out within seconds. A single
   isolated cell dies immediately (no live neighbors).
4. **Auto-reset cycle.** Added `MaybeResetB2()` / `SoftResetB2()` to detect
   sustained saturation, push live cells into the white-tint fade trail, and
   reseed. Worked mechanically but felt artificial — the visual became
   "bloom → flash-clear → bloom" on a fixed cadence rather than something
   organic. Removed.
5. **No-wrap edges + no reactive additions.** Replaced toroidal wrap with
   dead-edge boundary handling in `StepRowBitboardMultiState` (via a new
   `wrapEdges` parameter; Conway still wraps). Removed all reactive
   injection for B2 rules. Used a small one-time random-density patch
   (~17×17 at ~35%) as the canonical bootstrap. Added a `SeedB2Fresh`
   burnout safety net to reseed when the field empties.

   Result: the patch still expands in spreading wavefronts and fills the
   visible area before the no-wrap edges can thin it out. This is just what
   these rules do on any bounded grid — random soup always saturates, and
   structured "spaceship" patterns eventually crash into the walls and
   detonate into the same expanding mess.

## Conclusion

Brian's Brain and Star Wars are fun to **watch start** but don't have a
stable middle state on a screen-sized bounded grid. They lack the mix of
still-lifes, oscillators, and slow-moving spaceships that makes Conway
work as a long-running ambient visualization.

Options if we revisit this:

1. **Accept the fill, manage the fade.** Let it spread to saturation, then
   no-wrap edges + cell aging will naturally thin it out over ~30–60s into
   a sparse dying field. Reads as "explosion → fireworks aftermath → dark
   → reseed." Probably the most authentic presentation of these rules.
2. **Tiny sparse seeds, accept fragility.** Drop the patch to ~7×7 at
   ~20% and accept that some seeds die in seconds and others wander for a
   while. Looks inconsistent run-to-run.
3. **Different rule family.** Generations-style rules (Bloomerang, Bombers)
   or larger-neighborhood rules like SmoothLife are designed for sustained
   self-organizing behavior and would be better suited to an ambient
   visual. Bigger lift — neither shares Conway's 2-state bitboard model.

For now, Conway is the only rule that ships.

## Code that stays in the tree

The B2 implementation is left in place for future work:

- `GameOfLifePattern.RulesEngine` enum and `Rules` property.
- `StepRowBitboardMultiState(...)` with `wrapEdges` parameter.
- `PlantB2Seed(...)` (random-density patch) and `SeedB2Fresh()`
  burnout-reseed.
- B2 branches in `SeedGrid()` and `OnTick()`.
- `AppSettings.GameOfLifeRulesEngine` and `GameOfLifeEraBandedHueSpeed`.
- `SettingsWindow` load/save wiring and `UpdateGameOfLifeRulesVisibility()`.

The only things hiding the feature from users are the forced Conway
assignment in `DmdWindow.xaml.cs` and the collapsed Rules `StackPanel` in
`SettingsWindow.xaml`.
