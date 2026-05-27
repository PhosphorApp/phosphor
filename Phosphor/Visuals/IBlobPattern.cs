namespace Phosphor;

/// <summary>
/// Encapsulates a blob visualization pattern with smooth entry/exit transitions.
/// Each pattern owns its blobs, brushes, and any pattern-specific state (simulators,
/// transforms, etc.). The canvas is empty before Enter and after Exit completes.
/// </summary>
public interface IBlobPattern : IDisposable
{
    /// <summary>The concrete pattern type.</summary>
    BlobPattern PatternType { get; }

    /// <summary>
    /// When true, the pattern manages its own brush colors (e.g. Matrix rain).
    /// The playfield should skip external colour cycling but still detect the
    /// dominant band from the brush values the pattern sets.
    /// </summary>
    bool ManagesOwnColors => false;

    /// <summary>The visual elements owned by this pattern (available after Enter begins).</summary>
    IReadOnlyList<System.Windows.FrameworkElement> Blobs { get; }

    /// <summary>The color brushes for each blob (indexed to match Blobs).</summary>
    IReadOnlyList<System.Windows.Media.SolidColorBrush> Brushes { get; }

    /// <summary>The gradient brushes for each blob (indexed to match Blobs).</summary>
    IReadOnlyList<System.Windows.Media.RadialGradientBrush> GradientBrushes { get; }

    /// <summary>
    /// Create blobs on the canvas and animate them into their starting positions.
    /// The canvas should be empty when this is called.
    /// </summary>
    void Enter(Action onComplete);

    /// <summary>
    /// Smoothly animate blobs off-screen, then remove them from the canvas and clean up
    /// all pattern-specific state. The canvas will be empty when onComplete fires.
    /// </summary>
    void Exit(Action onComplete);

    /// <summary>
    /// Apply audio-reactive effects (scale, opacity, blur, etc.) to the pattern's elements.
    /// Called each audio tick from the window's dispatcher thread.
    /// </summary>
    /// <param name="data">Current audio analysis data.</param>
    /// <param name="baseIntensity">The window's base blob intensity / opacity.</param>
    /// <param name="reactiveSpeedMs">Animation duration in milliseconds for reactive effects.</param>
    void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs);

    /// <summary>
    /// Reset all audio-reactive visual state (scale, blur, opacity) back to defaults.
    /// Called when reactive audio is disabled for this window.
    /// </summary>
    void ResetAudioReactive(double baseIntensity);

    /// <summary>
    /// Pulse visual elements whose current color matches the given dominant ROYGBIV band.
    /// </summary>
    void PulseDominantColor(RoygbivColor band);
}
