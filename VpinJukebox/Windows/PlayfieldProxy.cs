using System.Windows.Threading;

namespace VpinJukebox;

/// <summary>
/// Thread-safe proxy for <see cref="PlayfieldWindow"/> when it runs on its own
/// STA thread.  Every method dispatches to the playfield's <see cref="Dispatcher"/>
/// so callers on the main UI thread don't need to worry about cross-thread access.
/// </summary>
public sealed class PlayfieldProxy
{
    private readonly PlayfieldWindow _window;
    private readonly Dispatcher _dispatcher;

    public PlayfieldProxy(PlayfieldWindow window)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
    }

    /// <summary>Direct access — only use from the playfield's own thread or for
    /// operations that are explicitly thread-safe (e.g. WireDimIdleEvents which
    /// already dispatches back).</summary>
    public PlayfieldWindow Window => _window;

    public Dispatcher Dispatcher => _dispatcher;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public void Show() => _dispatcher.BeginInvoke(() => _window.Show());
    public void Hide() => _dispatcher.BeginInvoke(() => _window.Hide());
    public void Close() => _dispatcher.BeginInvoke(() => _window.Close());

    // ── Layout ─────────────────────────────────────────────────────────

    public void ApplyLayout(WindowLayout layout)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _window.ApplyLayout(layout);
        });
    }

    public void SaveLayout(WindowLayout layout)
    {
        // Must be synchronous — caller needs the layout populated before saving
        _dispatcher.Invoke(() => _window.SaveLayout(layout));
    }

    public void SetResizable(bool resizable) =>
        _dispatcher.BeginInvoke(() => _window.SetResizable(resizable));

    public void ForceHideExpandButton() =>
        _dispatcher.BeginInvoke(() => _window.ForceHideExpandButton());

    public void ResetPosition(double left, double top, double width, double height) =>
        _dispatcher.BeginInvoke(() => _window.ResetPosition(left, top, width, height));

    public bool CheckWindowPositionOnStartup
    {
        set => _dispatcher.BeginInvoke(() => _window.CheckWindowPositionOnStartup = value);
    }

    // ── Settings ───────────────────────────────────────────────────────

    public void SetAppSettings(AppSettings settings) =>
        _dispatcher.BeginInvoke(() => _window.SetAppSettings(settings));

    public void SetScreensaverSettings(double intensity, double speed) =>
        _dispatcher.BeginInvoke(() => _window.SetScreensaverSettings(intensity, speed));

    public void SetBrightnessBoost(double boost) =>
        _dispatcher.BeginInvoke(() => _window.SetBrightnessBoost(boost));

    public void SetBlobPattern(BlobPattern pattern) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobPattern(pattern));

    public void SetPulseDominantBlobs(bool enabled) =>
        _dispatcher.BeginInvoke(() => _window.SetPulseDominantBlobs(enabled));

    /// <summary>
    /// Invokes an action on the playfield thread with access to the active ProjectMRenderer.
    /// The callback receives null if ProjectM is not the current pattern.
    /// </summary>
    public void WithProjectMRenderer(Action<ProjectMRenderer?> action) =>
        _dispatcher.BeginInvoke(() => action(_window.GetProjectMRenderer()));

    /// <summary>
    /// Synchronously retrieves the active ProjectMRenderer (or null) from the playfield thread.
    /// </summary>
    public ProjectMRenderer? GetProjectMRenderer() =>
        _dispatcher.Invoke(() => _window.GetProjectMRenderer());

    public void RestartMandelbrot() =>
        _dispatcher.BeginInvoke(() => _window.RestartMandelbrot());

    public void RestartProjectM() =>
        _dispatcher.BeginInvoke(() => _window.RestartProjectM());

    public void OnSongChanged() =>
        _dispatcher.BeginInvoke(() => _window.OnSongChanged());

    public void SetBlobCount(int count) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobCount(count));

    public void SetRotation(int degrees) =>
        _dispatcher.BeginInvoke(() => _window.SetRotation(degrees));

    public void SetStaticImage(string? path) =>
        _dispatcher.BeginInvoke(() => _window.SetStaticImage(path));

    public void SetVideoPath(string? path) =>
        _dispatcher.BeginInvoke(() => _window.SetVideoPath(path));

    public void SetMode(PlayfieldMode mode) =>
        _dispatcher.BeginInvoke(() => _window.SetMode(mode));

    public void SetOledSleepDefeat(int intervalSeconds, int durationSeconds = 5, int intensityPercent = 80) =>
        _dispatcher.BeginInvoke(() => _window.SetOledSleepDefeat(intervalSeconds, durationSeconds, intensityPercent));

    public void SetReactiveAudio(AudioReactiveService? service) =>
        _dispatcher.BeginInvoke(() => _window.SetReactiveAudio(service));

    // ── Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the predominant blob color band changes.
    /// Fires on the playfield's dispatcher thread — callers must marshal if needed.
    /// </summary>
    public event Action<ColorAnalysis>? BlobColorBandChanged
    {
        add => _dispatcher.BeginInvoke(() => _window.BlobColorBandChanged += value);
        remove => _dispatcher.BeginInvoke(() => _window.BlobColorBandChanged -= value);
    }

    // ── Cursor ──────────────────────────────────────────────────────────

    public void HideCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None);

    public void ShowCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = null);

    // ── Swap / Fade ────────────────────────────────────────────────────

    public void FadeToBlack(double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.FadeToBlack(durationSeconds, onCompleted));

    public void FadeFromBlack(double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.FadeFromBlack(durationSeconds, onCompleted));

    /// <summary>
    /// Applies a blur effect to the playfield window to mask swap transitions.
    /// </summary>
    public void ApplySwapBlur(double radius = 30) =>
        _dispatcher.BeginInvoke(() => _window.ApplySwapBlur(radius));

    /// <summary>
    /// Animates a blur effect onto the playfield window.
    /// </summary>
    public void AnimateApplyBlur(double targetRadius, double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.AnimateApplyBlur(targetRadius, durationSeconds, onCompleted));

    /// <summary>
    /// Animates the blur away from the playfield window.
    /// </summary>
    public void AnimateRemoveBlur(double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.AnimateRemoveBlur(durationSeconds, onCompleted));

    /// <summary>
    /// Reads the playfield window's current bounds (synchronously on its thread).
    /// </summary>
    public (double Left, double Top, double Width, double Height) GetBounds() =>
        _dispatcher.Invoke(() => (_window.Left, _window.Top, _window.Width, _window.Height));

    /// <summary>
    /// Sets the playfield window's bounds.
    /// </summary>
    public void SetBounds(double left, double top, double width, double height) =>
        _dispatcher.BeginInvoke(() =>
        {
            _window.Left = left;
            _window.Top = top;
            _window.Width = width;
            _window.Height = height;
        });

    // ── Shutdown ────────────────────────────────────────────────────────

    /// <summary>
    /// Shuts down the playfield's dedicated dispatcher, ending its thread.
    /// Call after Close().
    /// </summary>
    public void ShutdownDispatcher() =>
        _dispatcher.BeginInvoke(() => _dispatcher.InvokeShutdown());
}
