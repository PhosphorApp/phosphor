using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;

namespace Phosphor;

/// <summary>
/// Thread-safe proxy for <see cref="TopperWindow"/> when it runs on its own
/// STA thread.  Every method dispatches to the topper's <see cref="Dispatcher"/>
/// so callers on the main UI thread don't need to worry about cross-thread access.
/// </summary>
public sealed class TopperProxy
{
    private readonly TopperWindow _window;
    private readonly Dispatcher _dispatcher;

    public TopperProxy(TopperWindow window)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
    }

    /// <summary>Direct access — only use from the topper's own thread or for
    /// operations that are explicitly thread-safe.</summary>
    public TopperWindow Window => _window;

    public Dispatcher Dispatcher => _dispatcher;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public void Show() => _dispatcher.BeginInvoke(() => _window.Show());
    public void Hide() => _dispatcher.BeginInvoke(() => _window.Hide());
    public void Close() => _dispatcher.BeginInvoke(() => _window.Close());

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

    public bool CheckWindowPositionOnStartup
    {
        set => _dispatcher.BeginInvoke(() => _window.CheckWindowPositionOnStartup = value);
    }

    // ── Settings ───────────────────────────────────────────────────────

    public void SetAppSettings(AppSettings settings) =>
        _dispatcher.BeginInvoke(() => _window.SetAppSettings(settings));

    public void SetScreensaverSettings(double intensity, double speed) =>
        _dispatcher.BeginInvoke(() => _window.SetScreensaverSettings(intensity, speed));

    public void SetBlobPattern(BlobPattern pattern) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobPattern(pattern));

    public void SetBlobCount(int count) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobCount(count));

    public void SetBlobSizeOffset(int offset) =>
        _dispatcher.BeginInvoke(() => _window.SetBlobSizeOffset(offset));

    public void SetReactiveAudio(AudioReactiveService? service) =>
        _dispatcher.BeginInvoke(() => _window.SetReactiveAudio(service));

    public void SetDistortion(double distortion) =>
        _dispatcher.BeginInvoke(() => _window.SetDistortion(distortion));

    // ── Logo ───────────────────────────────────────────────────────────

    public void SetLogoSpin(bool spin) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoSpin(spin));

    public void SetLogoRings(LogoRingsMode mode) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoRings(mode));

    public void SetLogoRingsBrightness(int percent) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoRingsBrightness(percent));

    public void SetLogoBrightness(int percent) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoBrightness(percent));

    public void SetLogoText(string text) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoText(text));

    public void SetLogoMorphColor(LogoColorMode mode) =>
        _dispatcher.BeginInvoke(() => _window.SetLogoMorphColor(mode));

    public void MorphLogoToColor(RoygbivColor color) =>
        _dispatcher.BeginInvoke(() => _window.MorphLogoToColor(color));

    public void ApplyMorphColors(WpfColor titleColor, WpfColor recordColor) =>
        _dispatcher.BeginInvoke(() => _window.ApplyMorphColors(titleColor, recordColor));

    public void ApplyResetColors() =>
        _dispatcher.BeginInvoke(() => _window.ApplyResetColors());

    // ── Pattern restarts ───────────────────────────────────────────────

    public void RestartMandelbrot() =>
        _dispatcher.BeginInvoke(() => _window.RestartMandelbrot());

    public void RestartProjectM() =>
        _dispatcher.BeginInvoke(() => _window.RestartProjectM());

    public void RestartGameOfLife() =>
        _dispatcher.BeginInvoke(() => _window.RestartGameOfLife());

    public void RestartGravity() =>
        _dispatcher.BeginInvoke(() => _window.RestartGravity());

    public void RestartClock() =>
        _dispatcher.BeginInvoke(() => _window.RestartClock());

    public void ApplyProjectMTuning() =>
        _dispatcher.BeginInvoke(() => _window.ApplyProjectMTuning());

    public void OnSongChanged() =>
        _dispatcher.BeginInvoke(() => _window.OnSongChanged());

    // ── Cursor ──────────────────────────────────────────────────────────

    public void HideCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None);

    public void ShowCursor() =>
        _dispatcher.BeginInvoke(() => System.Windows.Input.Mouse.OverrideCursor = null);

    // ── Diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Synchronously retrieves window info for diagnostics (HWND, size, visibility).
    /// </summary>
    public (nint Hwnd, double Width, double Height)? GetWindowInfo() =>
        _dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible) return ((nint, double, double)?)null;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            return (hwnd, _window.ActualWidth, _window.ActualHeight);
        });

    // ── Shutdown ────────────────────────────────────────────────────────

    /// <summary>
    /// Shuts down the topper's dedicated dispatcher, ending its thread.
    /// Call after Close().
    /// </summary>
    public void ShutdownDispatcher() =>
        _dispatcher.BeginInvoke(() => _dispatcher.InvokeShutdown());
}
