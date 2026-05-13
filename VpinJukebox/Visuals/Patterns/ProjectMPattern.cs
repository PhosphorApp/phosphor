using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Image = System.Windows.Controls.Image;

namespace VpinJukebox;

/// <summary>
/// projectM Milkdrop visualization pattern. Renders music-reactive visuals
/// using the native libprojectM library via OpenGL, displayed as a single
/// <see cref="Image"/> element — same architecture as <see cref="MandelbrotPattern"/>.
/// </summary>
public sealed class ProjectMPattern : BlobPatternBase
{
    private ProjectMRenderer? _renderer;
    private Image? _image;
    private int _pixelWidth;
    private int _pixelHeight;

    private readonly Stopwatch _stopwatch = new();
    private long _lastFrameTicks;
    private bool _rendering;
    private Action? _deferredOnComplete;

    public override BlobPattern PatternType => BlobPattern.ProjectM;

    /// <summary>
    /// Provides access to the underlying renderer for preset browsing.
    /// </summary>
    public ProjectMRenderer? Renderer => _renderer;

    /// <summary>
    /// Applies tuning-only settings (duration, sensitivity, mesh, etc.) to the
    /// running renderer without a full restart.
    /// </summary>
    public void ApplyTuningSettings() => _renderer?.ApplyTuningSettings();

    public ProjectMPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;

        _pixelWidth = Math.Max(1, (int)(w * ProjectMRenderer.RenderScale));
        _pixelHeight = Math.Max(1, (int)(h * ProjectMRenderer.RenderScale));

        _renderer = new ProjectMRenderer();
        ImageSource imageSource;

        if (_renderer.Initialize(_pixelWidth, _pixelHeight) && _renderer.ImageSource != null)
        {
            imageSource = _renderer.ImageSource;
            Log($"projectM renderer initialized ({_pixelWidth}x{_pixelHeight})");
            _renderer.BlackPresetDetected += OnBlackPresetDetected;
        }
        else
        {
            Log("projectM renderer FAILED to initialize — falling back to placeholder");
            _renderer.Dispose();
            _renderer = null;
            var placeholder = new System.Windows.Media.Imaging.WriteableBitmap(
                1, 1, 96, 96, PixelFormats.Bgra32, null);
            imageSource = placeholder;
        }

        _image = new Image
        {
            Width = w,
            Height = h,
            Source = imageSource,
            Stretch = Stretch.Fill,
            Opacity = 0,
        };

        _brushes.Add(new SolidColorBrush(Colors.Black));
        _gradBrushes.Add(new RadialGradientBrush());

        _canvas.Children.Add(_image);
        _blobs.Add(_image);
    }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        // Defer initialization until the canvas has real dimensions
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1)
        {
            _deferredOnComplete = onComplete;
            _canvas.LayoutUpdated += OnDeferredLayout;
            return;
        }

        // If the owning window hasn't settled yet (e.g. still expanding to monitor),
        // wait for LayoutSettled so we initialize at the final resolution.
        var window = Window.GetWindow(_canvas);
        if (window is JukeboxWindow jw && !jw.IsLayoutSettled)
        {
            _deferredOnComplete = onComplete;
            void OnSettled()
            {
                jw.LayoutSettled -= OnSettled;
                var cb = _deferredOnComplete;
                _deferredOnComplete = null;
                if (cb != null && !_disposed)
                    EnterCore(cb);
            }
            jw.LayoutSettled += OnSettled;
            return;
        }

        EnterCore(onComplete);
    }

    private void OnDeferredLayout(object? sender, EventArgs e)
    {
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1)
            return;

        _canvas.LayoutUpdated -= OnDeferredLayout;

        // Now wait for the window to finish settling (expand, etc.)
        var window = Window.GetWindow(_canvas);
        if (window is JukeboxWindow jw && !jw.IsLayoutSettled)
        {
            void OnSettled()
            {
                jw.LayoutSettled -= OnSettled;
                var cb = _deferredOnComplete;
                _deferredOnComplete = null;
                if (cb != null && !_disposed)
                    EnterCore(cb);
            }
            jw.LayoutSettled += OnSettled;
            return;
        }

        var callback = _deferredOnComplete;
        _deferredOnComplete = null;
        if (callback != null && !_disposed)
            EnterCore(callback);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _renderer == null || !_renderer.IsAvailable || _image == null)
            return;

        int w = Math.Max(1, (int)(e.NewSize.Width * ProjectMRenderer.RenderScale));
        int h = Math.Max(1, (int)(e.NewSize.Height * ProjectMRenderer.RenderScale));
        if (w == _pixelWidth && h == _pixelHeight)
            return;

        _pixelWidth = w;
        _pixelHeight = h;
        if (_renderer.Resize(_pixelWidth, _pixelHeight))
        {
            _image.Width = e.NewSize.Width;
            _image.Height = e.NewSize.Height;
            _image.Source = _renderer.ImageSource;
        }
    }

    private void EnterCore(Action onComplete)
    {
        CreateBlobs();

        // Listen for runtime size changes (drag-resize, F11 toggle) so we
        // can resize the renderer in-place instead of recreating it.
        _canvas.SizeChanged += OnCanvasSizeChanged;

        if (_image == null) { onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { onComplete(); return; }

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(1.0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeIn.Completed += (_, _) =>
        {
            if (_disposed) { onComplete(); return; }
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _image.Opacity = 1.0;
            StartMotion();
            onComplete();
        };
        _image.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public override void Exit(Action onComplete)
    {
        _canvas.LayoutUpdated -= OnDeferredLayout;
        _canvas.SizeChanged -= OnCanvasSizeChanged;
        _deferredOnComplete = null;
        StopMotion();

        if (_image == null || _blobs.Count == 0)
        {
            CleanupCanvas();
            onComplete();
            return;
        }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { CleanupCanvas(); onComplete(); return; }

        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(0.8),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            try
            {
                CleanupCanvas();
            }
            catch (Exception ex)
            {
                Log($"Exit CleanupCanvas failed: {ex.Message}");
            }
            onComplete();
        };
        _image.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    protected override void StartMotion()
    {
        if (_renderer == null || !_renderer.IsAvailable) return;
        _stopwatch.Restart();
        _lastFrameTicks = _stopwatch.ElapsedTicks;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void StopMotion()
    {
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_rendering || _disposed) return;

        var renderer = _renderer;
        if (renderer == null || !renderer.IsAvailable) return;

        try
        {
            long nowTicks = _stopwatch.ElapsedTicks;
            double dt = (double)(nowTicks - _lastFrameTicks) / Stopwatch.Frequency;
            _lastFrameTicks = nowTicks;
            if (dt <= 0) return;

            // Feed raw PCM audio from the shared buffer in AudioReactiveService
            var pcm = AudioReactiveService.ConsumeRawPcm();
            if (pcm != null)
                renderer.AddPcmData(pcm, 2);

            renderer.RenderFrame();

            // If the renderer disabled itself (native fault), stop gracefully
            if (!renderer.IsAvailable)
            {
                Log("Renderer became unavailable after native fault — stopping");
                StopMotion();
            }
        }
        catch (Exception ex)
        {
            Log($"OnRendering exception: {ex.Message}");
        }
    }

    /// <summary>
    /// projectM does its own beat detection from raw PCM, so the processed
    /// bass/treble/beat data isn't used for rendering. We keep this as a no-op
    /// so the base class contract is satisfied.
    /// </summary>
    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        // Intentionally empty — projectM handles its own audio analysis.
    }

    /// <summary>
    /// Called when the renderer confirms a preset has rendered black on two consecutive checks.
    /// Logs to the monitor log and optionally moves the preset to Deactivated, then skips.
    /// </summary>
    private void OnBlackPresetDetected(string presetFullPath)
    {
        try
        {
            var renderer = _renderer;
            if (renderer == null || _disposed) return;

            // Mode 2: move preset to Deactivated folder
            if (ProjectMRenderer.PresetMonitorMode >= 2 && System.IO.File.Exists(presetFullPath))
            {
                var presetPath = ProjectMRenderer.PresetPath;
                if (!string.IsNullOrEmpty(presetPath))
                {
                    var deactivatedRoot = System.IO.Path.Combine(presetPath, "Deactivated");
                    var relativePath = System.IO.Path.GetRelativePath(presetPath, presetFullPath);
                    var destPath = System.IO.Path.Combine(deactivatedRoot, relativePath);
                    var destDir = System.IO.Path.GetDirectoryName(destPath);

                    try
                    {
                        if (destDir != null) System.IO.Directory.CreateDirectory(destDir);
                        System.IO.File.Move(presetFullPath, destPath, overwrite: false);
                        Log($"Moved black preset to Deactivated: {relativePath}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to move black preset: {ex.Message}");
                    }
                }

                // Remove from in-memory playlist so shuffle can't revisit it
                if (renderer.IsAvailable)
                    renderer.RemoveCurrentPresetFromPlaylist();
            }

            // Skip to next preset
            if (renderer.IsAvailable)
                renderer.PlayNext();
        }
        catch (Exception ex)
        {
            Log($"OnBlackPresetDetected error: {ex.Message}");
        }
    }

    protected override void CleanupCanvas()
    {
        StopMotion();
        _renderer?.Dispose();
        _renderer = null;
        if (_image != null)
        {
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _canvas.Children.Remove(_image);
            _image.Source = null;
            _image = null;
        }
        _blobs.Clear();
        _brushes.Clear();
        _gradBrushes.Clear();
        _states.Clear();
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _canvas.SizeChanged -= OnCanvasSizeChanged;
            StopMotion();
            CleanupCanvas();
        }
        catch (Exception ex)
        {
            Log($"Dispose failed: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        var msg = $"[ProjectM] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        DebugLog.Log("ProjectM", message);
    }
}
