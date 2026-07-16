namespace Phosphor;

/// <summary>
/// A visual pattern whose animation/render loop can be temporarily suspended while it is
/// hidden (e.g. a jukebox video covers the backglass idle blobs), then resumed seamlessly
/// from the exact state it was paused in — no teardown, no "fly-in" rebuild.
///
/// The primary motivation is CPU/GPU cost: self-rendering patterns (Game of Life, ProjectM,
/// Mandelbrot, Matrix, …) drive continuous <c>CompositionTarget.Rendering</c> / timer loops
/// that keep burning cycles even when nothing is visible. Pause detaches that loop while
/// leaving all visual state (blobs, positions, simulator state, GPU context) in place, so
/// Resume simply re-attaches and carries on.
///
/// <see cref="BlobPatternBase"/> provides a virtual default that delegates to the pattern's
/// existing <c>StopMotion()</c> / <c>StartMotion()</c>, which is correct for every
/// continuous-loop pattern. Patterns with special needs (e.g. freezing WPF storyboard clocks,
/// or releasing/reacquiring external resources) can override <see cref="Pause"/> /
/// <see cref="Resume"/>.
/// </summary>
public interface IPausable
{
    /// <summary>True while the pattern is paused (loop suspended, state preserved).</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Suspend the pattern's animation/render loop to free CPU/GPU while hidden, leaving all
    /// visual state intact so <see cref="Resume"/> continues seamlessly. Idempotent.
    /// </summary>
    void Pause();

    /// <summary>
    /// Restart the pattern's animation/render loop from its current (frozen) state.
    /// Idempotent; a no-op if the pattern was never paused.
    /// </summary>
    void Resume();
}
