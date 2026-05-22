using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;

namespace Phosphor;

/// <summary>
/// Thread-safe proxy for <see cref="BackglassWindow"/> when it runs on its own
/// STA thread.  Every method dispatches to the backglass's <see cref="Dispatcher"/>
/// so callers on the main UI thread don't need to worry about cross-thread access.
/// </summary>
public sealed class BackglassProxy
{
    private readonly BackglassWindow _window;
    private readonly Dispatcher _dispatcher;

    public BackglassProxy(BackglassWindow window)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
    }

    /// <summary>Direct access — only use from the backglass's own thread or for
    /// operations that are explicitly thread-safe (e.g. event wiring that already
    /// dispatches back).</summary>
    public BackglassWindow Window => _window;

    public Dispatcher Dispatcher => _dispatcher;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public void Show() => _dispatcher.BeginInvoke(() => _window.Show());
    public void Hide() => _dispatcher.BeginInvoke(() => _window.Hide());
    public void Close() => _dispatcher.BeginInvoke(() => _window.Close());

    /// <summary>
    /// Shows the window briefly off-screen to initialize the visual tree and media player,
    /// then hides it and restores the saved layout. All done synchronously on the backglass thread.
    /// </summary>
    public void InitializeHidden(WindowLayout layout)
    {
        _dispatcher.Invoke(() =>
        {
            // Save current position/size
            var savedLeft = _window.Left;
            var savedTop = _window.Top;
            var savedWidth = _window.Width;
            var savedHeight = _window.Height;

            // Move off-screen and shrink to minimize any flash
            _window.Left = -10000;
            _window.Top = -10000;
            _window.Width = 200;
            _window.Height = 200;

            // Show to trigger visual tree initialization
            _window.Show();

            // Hide immediately
            _window.Hide();

            // Restore original dimensions
            _window.Left = savedLeft;
            _window.Top = savedTop;
            _window.Width = savedWidth;
            _window.Height = savedHeight;
        });
    }

    // ── Layout ─────────────────────────────────────────────────────────

    public void ApplyLayout(WindowLayout layout) =>
        _dispatcher.BeginInvoke(() => _window.ApplyLayout(layout));

    public void SaveLayout(WindowLayout layout) =>
        _dispatcher.Invoke(() => _window.SaveLayout(layout));

    public void SetResizable(bool resizable) =>
        _dispatcher.BeginInvoke(() => _window.SetResizable(resizable));

    public void ForceHideExpandButton() =>
        _dispatcher.BeginInvoke(() => _window.ForceHideExpandButton());

    public void ResetPosition(double left, double top, double width, double height) =>
        _dispatcher.BeginInvoke(() => _window.ResetPosition(left, top, width, height));

    /// <summary>
    /// Reads the backglass window's current bounds (synchronously on its thread).
    /// </summary>
    public (double Left, double Top, double Width, double Height) GetBounds() =>
        _dispatcher.Invoke(() => (_window.Left, _window.Top, _window.Width, _window.Height));

    /// <summary>
    /// Sets the backglass window's bounds.
    /// </summary>
    public void SetBounds(double left, double top, double width, double height) =>
        _dispatcher.BeginInvoke(() =>
        {
            _window.Left = left;
            _window.Top = top;
            _window.Width = width;
            _window.Height = height;
        });

    /// <summary>
    /// Animates a blur effect onto the backglass window.
    /// </summary>
    public void AnimateApplyBlur(double targetRadius, double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.AnimateApplyBlur(targetRadius, durationSeconds, onCompleted));

    /// <summary>
    /// Animates the blur away from the backglass window.
    /// </summary>
    public void AnimateRemoveBlur(double durationSeconds, Action? onCompleted = null) =>
        _dispatcher.BeginInvoke(() => _window.AnimateRemoveBlur(durationSeconds, onCompleted));

    public bool CheckWindowPositionOnStartup
    {
        set => _dispatcher.BeginInvoke(() => _window.CheckWindowPositionOnStartup = value);
    }

    // ── Settings ───────────────────────────────────────────────────────

    public void SetAppSettings(AppSettings settings) =>
        _dispatcher.BeginInvoke(() => _window.SetAppSettings(settings));

    public void AttachViewModel(JukeboxViewModel vm) =>
        _dispatcher.BeginInvoke(() => _window.AttachViewModel(vm));

    /// <summary>
    /// Sets the DataContext on the backglass thread.
    /// </summary>
    public void SetDataContext(object viewModel) =>
        _dispatcher.BeginInvoke(() => _window.DataContext = viewModel);

    public void SetScreensaverSettings(double intensity, double speed) =>
        _dispatcher.BeginInvoke(() => _window.SetScreensaverSettings(intensity, speed));

    public void SetBrightnessBoost(double boost) =>
        _dispatcher.BeginInvoke(() => _window.SetBrightnessBoost(boost));

    public void SetBlobPattern(BlobPattern pattern) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobPattern(pattern));

    public void RestartMandelbrot() =>
        _dispatcher.BeginInvoke(() => _window.RestartMandelbrot());

    public void RestartProjectM() =>
        _dispatcher.BeginInvoke(() => _window.RestartProjectM());

    public void ApplyProjectMTuning() =>
        _dispatcher.BeginInvoke(() => _window.ApplyProjectMTuning());

    public void OnSongChanged() =>
        _dispatcher.BeginInvoke(() => _window.OnSongChanged());

    public void SetBlobCount(int count) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobCount(count));

    public void SetBlobSizeOffset(int offset) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobSizeOffset(offset));

    public void SetShowVideoInfo(bool show) =>
        _dispatcher.BeginInvoke(() => _window.SetShowVideoInfo(show));

    public void SetLogoText(string text) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoText(text));

    public void SetLogoSpin(bool spin) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoSpin(spin));

    public void SetLogoRings(LogoRingsMode mode) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoRings(mode));

    public void SetLogoRingsBrightness(int percent) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoRingsBrightness(percent));

    public void SetLogoDim(bool enabled, int opacityPercent, int timeoutSeconds) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoDim(enabled, opacityPercent, timeoutSeconds));

    public void SetLogoMorphColor(LogoColorMode mode) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoMorphColor(mode));

    public void MorphLogoToColor(RoygbivColor color) =>
        _dispatcher.BeginInvoke(() => _window.MorphLogoToColor(color));

    public void SetAudioOnly(bool audioOnly) =>
        _dispatcher.BeginInvoke(() => _window.SetAudioOnly(audioOnly));

    public void SetReactiveAudio(AudioReactiveService? service) =>
        _dispatcher.BeginInvoke(() => _window.SetReactiveAudio(service));

    // ── Cursor ──────────────────────────────────────────────────────────

    public void HideCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None);

    public void ShowCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = null);

    // ── Events (subscribe from any thread — handlers fire on backglass thread) ──

    /// <summary>Wires the event on the backglass thread to avoid cross-thread access.</summary>
    public event Action? PlaybackStarted
    {
        add { var h = value; _dispatcher.BeginInvoke(() => _window.PlaybackStarted += h); }
        remove { var h = value; _dispatcher.BeginInvoke(() => _window.PlaybackStarted -= h); }
    }

    public event Action<string>? VideoInfoChanged
    {
        add { var h = value; _dispatcher.BeginInvoke(() => _window.VideoInfoChanged += h); }
        remove { var h = value; _dispatcher.BeginInvoke(() => _window.VideoInfoChanged -= h); }
    }

    public event Action<WpfColor, WpfColor>? LogoColorsMorphed
    {
        add { var h = value; _dispatcher.BeginInvoke(() => _window.LogoColorsMorphed += h); }
        remove { var h = value; _dispatcher.BeginInvoke(() => _window.LogoColorsMorphed -= h); }
    }

    public event Action? LogoColorsReset
    {
        add { var h = value; _dispatcher.BeginInvoke(() => _window.LogoColorsReset += h); }
        remove { var h = value; _dispatcher.BeginInvoke(() => _window.LogoColorsReset -= h); }
    }

    // ── Shutdown ────────────────────────────────────────────────────────

    public void ShutdownDispatcher() =>
        _dispatcher.BeginInvoke(() => _dispatcher.InvokeShutdown());
}
