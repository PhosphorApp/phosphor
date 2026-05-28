using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace Phosphor;

/// <summary>
/// Conway's Game of Life visualization rendered to a <see cref="WriteableBitmap"/>.
/// Each living cell carries a color; offspring inherit a blend of their parents.
/// Audio beats inject new cells inversely proportional to population density.
/// Dying cells fade out over several generations; rapidly reproducing areas brighten.
/// </summary>
public sealed class GameOfLifePattern : BlobPatternBase
{
    /// <summary>Cell size in pixels (each cell is a square block). Default 5.</summary>
    public static int CellSize { get; set; } = 5;

    /// <summary>Simulation tick interval in milliseconds. Default 100.</summary>
    public static int TickIntervalMs { get; set; } = 100;

    /// <summary>Number of generations a dying cell takes to fully fade out. Default 6.</summary>
    public static int FadeGenerations { get; set; } = 6;

    /// <summary>Brightness boost added to newly born cells (0–100). Default 60.</summary>
    public static int HeatBoost { get; set; } = 60;

    /// <summary>Injection density (1–10). 5 = default, lower = sparser, higher = more crowded.</summary>
    public static int Density { get; set; } = 5;

    /// <summary>Old age pruning mode: 0 = Off, 1 = Low (60s), 2 = Medium (30s), 3 = High (15s).</summary>
    public static int OldAgePruning { get; set; }

    /// <summary>Fraction of grid edge to keep clear when placing seed clusters (0.0–0.5).</summary>
    private const double PlacementMargin = 0.05;

    public override BlobPattern PatternType => BlobPattern.GameOfLife;
    public override bool ManagesOwnColors => true;

    private WriteableBitmap? _bitmap;
    private Image? _image;
    private DispatcherTimer? _timer;

    private int _gridW, _gridH;

    // Per-cell state: color RGB (0 = dead), age (generations alive), fade (countdown when dying)
    private uint[] _colorCurrent = [];   // packed BGRA — 0 means dead
    private uint[] _colorNext = [];
    private ushort[] _age = [];            // how many generations this cell has been alive
    private byte[] _fade = [];           // remaining fade-out ticks (>0 = recently died, still rendering)
    private uint[] _fadeColor = [];      // color at moment of death, for fade rendering

    // Audio state
    private bool _pendingBeat;
    private float _lastLevel;
    private float _bassAccumulator;  // builds up from sustained bass to trigger injection

    // Pulse state — remaining frames of brightness boost from PulseDominantColor
    private int _pulseFramesRemaining;
    private RoygbivColor _pulseBand;
    private const int PulseFrameCount = 6;
    private const uint PulseBrightnessBoost = 80;

    // Generation counter for periodic injection when audio is off
    private int _generationCount;
    private bool _audioReactiveActive;

    public GameOfLifePattern(BlobPatternConfig config) : base(config) { }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        if (!_canvas.IsLoaded || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            void DeferredEnter(object? s, RoutedEventArgs e)
            {
                _canvas.Loaded -= DeferredEnter;
                if (_disposed) { onComplete(); return; }
                _canvas.Dispatcher.BeginInvoke(new Action(() => Enter(onComplete)),
                    DispatcherPriority.Loaded);
            }
            _canvas.Loaded += DeferredEnter;
            return;
        }

        CreateBlobs();

        if (_image == null) { onComplete(); return; }

        // Render the first frame so the fade-in isn't blank
        RenderFrame();

        // Fade in
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

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        int cellSize = Math.Max(1, CellSize);
        _gridW = Math.Max(1, (int)(w / cellSize));
        _gridH = Math.Max(1, (int)(h / cellSize));

        int totalCells = _gridW * _gridH;
        _colorCurrent = new uint[totalCells];
        _colorNext = new uint[totalCells];
        _age = new ushort[totalCells];
        _fade = new byte[totalCells];
        _fadeColor = new uint[totalCells];

        _bitmap = new WriteableBitmap(_gridW, _gridH, 96, 96, PixelFormats.Bgra32, null);

        _image = new Image
        {
            Width = w,
            Height = h,
            Source = _bitmap,
            Stretch = Stretch.Fill,
            Opacity = 0,
        };

        // Nearest-neighbor scaling to keep the blocky cell look
        RenderOptions.SetBitmapScalingMode(_image!, BitmapScalingMode.NearestNeighbor);

        // Dummy brush/grad so base class indexing doesn't crash
        _brushes.Add(new SolidColorBrush(Colors.Black));
        _gradBrushes.Add(new RadialGradientBrush());

        _canvas.Children.Add(_image!);
        _blobs.Add(_image!);

        SeedGrid();
    }

    private void SeedGrid()
    {
        int count = Math.Max(1, _blobCount);
        int marginX = Math.Max(2, (int)(_gridW * PlacementMargin));
        int marginY = Math.Max(2, (int)(_gridH * PlacementMargin));

        for (int s = 0; s < count; s++)
        {
            double hue = _rng.NextDouble() * 360.0;
            var color = HslToColor(hue, 0.9, 0.6);
            uint packed = PackColor(color);

            int cx = _rng.Next(marginX, _gridW - marginX);
            int cy = _rng.Next(marginY, _gridH - marginY);

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (_rng.NextDouble() < 0.5) continue;
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < _gridW && y >= 0 && y < _gridH)
                {
                    int idx = y * _gridW + x;
                    _colorCurrent[idx] = packed;
                    _age[idx] = 1;
                }
            }
        }
    }

    protected override void StartMotion()
    {
        RenderFrame();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(16, TickIntervalMs)),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void StopMotion()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _generationCount++;

        // Inject new cells on beat
        if (_pendingBeat)
        {
            _pendingBeat = false;
            InjectCells();
        }
        else if (!_audioReactiveActive)
        {
            // When audio reactive is off, periodically inject cells to keep
            // the simulation alive. Interval scales with tick rate so it's
            // roughly every ~10 seconds regardless of TickIntervalMs.
            int injectEvery = Math.Max(10, 8000 / Math.Max(16, TickIntervalMs));
            if (_generationCount % injectEvery == 0)
                InjectCells();
        }

        StepSimulation();
        RenderFrame();
    }

    private void StepSimulation()
    {
        int w = _gridW, h = _gridH;
        Array.Clear(_colorNext);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            int neighbors = 0;
            uint rSum = 0, gSum = 0, bSum = 0;

            // Count live neighbors and accumulate colors
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;

                // Wrap around edges for seamless borders
                if (nx < 0) nx = w - 1; else if (nx >= w) nx = 0;
                if (ny < 0) ny = h - 1; else if (ny >= h) ny = 0;

                uint nc = _colorCurrent[ny * w + nx];
                if (nc != 0)
                {
                    neighbors++;
                    rSum += (nc >> 16) & 0xFF;
                    gSum += (nc >> 8) & 0xFF;
                    bSum += nc & 0xFF;
                }
            }

            bool alive = _colorCurrent[idx] != 0;

            if (alive)
            {
                if (neighbors == 2 || neighbors == 3)
                {
                    // Survives — keep its color, increment age
                    _colorNext[idx] = _colorCurrent[idx];
                    _age[idx] = (ushort)Math.Min(ushort.MaxValue, _age[idx] + 1);
                    _fade[idx] = 0;

                    // Old age pruning: kill cells that have been alive too long
                    if (OldAgePruning > 0)
                    {
                        int pruneSeconds = OldAgePruning switch { 3 => 15, 2 => 30, _ => 60 };
                        int pruneGens = pruneSeconds * 1000 / Math.Max(1, TickIntervalMs);
                        if (_age[idx] >= pruneGens)
                        {
                            _colorNext[idx] = 0;
                            _fade[idx] = (byte)Math.Clamp(FadeGenerations, 1, 255);
                            _fadeColor[idx] = _colorCurrent[idx];
                            _age[idx] = 0;
                        }
                    }
                }
                else
                {
                    // Dies — start fade
                    _colorNext[idx] = 0;
                    _fade[idx] = (byte)Math.Clamp(FadeGenerations, 1, 255);
                    _fadeColor[idx] = _colorCurrent[idx];
                    _age[idx] = 0;
                }
            }
            else
            {
                if (neighbors == 3)
                {
                    // Birth — color is average of parents
                    uint r = rSum / 3, g = gSum / 3, b = bSum / 3;
                    _colorNext[idx] = 0xFF000000 | (r << 16) | (g << 8) | b;
                    _age[idx] = 1;
                    _fade[idx] = 0;
                }
                else
                {
                    // Still dead — tick fade if active
                    _colorNext[idx] = 0;
                    if (_fade[idx] > 0) _fade[idx]--;
                    // age stays 0
                }
            }
        }

        // Swap buffers
        (_colorCurrent, _colorNext) = (_colorNext, _colorCurrent);
    }

    private void RenderFrame()
    {
        if (_bitmap == null) return;

        bool pulsing = _pulseFramesRemaining > 0;
        if (pulsing) _pulseFramesRemaining--;

        // Accumulate average color of living cells for dominant band detection
        long totalR = 0, totalG = 0, totalB = 0;
        int aliveCount = 0;

        _bitmap.Lock();
        try
        {
            unsafe
            {
                uint* pixels = (uint*)_bitmap.BackBuffer;
                int w = _gridW, h = _gridH;

                for (int i = 0; i < w * h; i++)
                {
                    uint c = _colorCurrent[i];
                    if (c != 0)
                    {
                        uint cr = (c >> 16) & 0xFF;
                        uint cg = (c >> 8) & 0xFF;
                        uint cb = c & 0xFF;
                        totalR += cr;
                        totalG += cg;
                        totalB += cb;
                        aliveCount++;

                        // Apply heat boost for young cells + pulse boost for matching band
                        uint boost = _age[i] <= 2 ? (uint)Math.Clamp(HeatBoost, 0, 255) : 0u;
                        if (pulsing && CellMatchesBand(cr, cg, cb, _pulseBand))
                            boost += PulseBrightnessBoost;

                        if (boost > 0)
                        {
                            uint r = Math.Min(255, cr + boost);
                            uint g = Math.Min(255, cg + boost);
                            uint b = Math.Min(255, cb + boost);
                            pixels[i] = 0xFF000000 | (r << 16) | (g << 8) | b;
                        }
                        else
                        {
                            pixels[i] = c;
                        }
                    }
                    else if (_fade[i] > 0)
                    {
                        uint fc = _fadeColor[i];
                        int fadeMax = Math.Max(1, FadeGenerations);
                        uint alpha = (uint)(255 * _fade[i] / fadeMax);
                        uint r = (fc >> 16) & 0xFF;
                        uint g = (fc >> 8) & 0xFF;
                        uint b = fc & 0xFF;
                        r = r * alpha / 255;
                        g = g * alpha / 255;
                        b = b * alpha / 255;
                        pixels[i] = (alpha << 24) | (r << 16) | (g << 8) | b;
                    }
                    else
                    {
                        pixels[i] = 0;
                    }
                }
            }

            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _gridW, _gridH));
        }
        finally
        {
            _bitmap.Unlock();
        }

        // Update the dummy brush with the average living cell color so the
        // color cycling timer can detect dominant band changes for DOF.
        if (aliveCount > 0 && _brushes.Count > 0)
        {
            byte avgR = (byte)(totalR / aliveCount);
            byte avgG = (byte)(totalG / aliveCount);
            byte avgB = (byte)(totalB / aliveCount);
            _brushes[0].Color = Color.FromRgb(avgR, avgG, avgB);
        }
    }

    private void InjectCells()
    {
        // Count current population
        int totalCells = _gridW * _gridH;
        int alive = 0;
        for (int i = 0; i < totalCells; i++)
            if (_colorCurrent[i] != 0) alive++;

        double density = (double)alive / totalCells;

        // Inject more cells when population is low, fewer when crowded
        double densityFactor = Density / 5.0;
        int clustersToAdd = (int)Math.Max(1, (density switch
        {
            < 0.04 => 8,
            < 0.08 => 6,
            < 0.12 => 4,
            < 0.16 => 3,
            < 0.20 => 2,
            < 0.24 => 1,
            _ => 1,
        }) * densityFactor);

        // Use a hue based on current time for variety
        double baseHue = (Environment.TickCount64 / 50.0) % 360.0;

        int marginX = Math.Max(2, (int)(_gridW * PlacementMargin));
        int marginY = Math.Max(2, (int)(_gridH * PlacementMargin));

        for (int s = 0; s < clustersToAdd; s++)
        {
            double hue = (baseHue + _rng.NextDouble() * 60.0 - 30.0) % 360.0;
            if (hue < 0) hue += 360.0;
            var color = HslToColor(hue, 0.9, 0.6);
            uint packed = PackColor(color);

            int cx = _rng.Next(marginX, _gridW - marginX);
            int cy = _rng.Next(marginY, _gridH - marginY);

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (_rng.NextDouble() < 0.4) continue;
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < _gridW && y >= 0 && y < _gridH)
                {
                    int idx = y * _gridW + x;
                    _colorCurrent[idx] = packed;
                    _age[idx] = 1;
                    _fade[idx] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Pulses all living cells brighter for a few frames when the dominant color band changes.
    /// </summary>
    public override void PulseDominantColor(RoygbivColor band)
    {
        if (_disposed) return;
        _pulseBand = band;
        _pulseFramesRemaining = PulseFrameCount;
    }

    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed) return;

        _audioReactiveActive = true;
        _lastLevel = data.Level;
        if (data.IsBeat)
            _pendingBeat = true;

        // Accumulate bass energy so quieter music still drives injection.
        // A sharp beat resets the accumulator; otherwise sustained bass
        // gradually fills it until it trips the threshold.
        if (data.IsBeat)
            _bassAccumulator = 0f;
        else
            _bassAccumulator += data.Bass * 0.08f;

        if (_bassAccumulator >= 1.0f)
        {
            _pendingBeat = true;
            _bassAccumulator = 0f;
        }

        // Gentle opacity modulation — Game of Life is a full-screen bitmap so
        // we keep it near full brightness with only a subtle bass-driven pulse.
        if (_image != null)
            _image.Opacity = Math.Clamp(0.85 + data.Bass * 0.15, 0.8, 1.0);
    }

    public override void ResetAudioReactive(double baseIntensity)
    {
        _audioReactiveActive = false;
        if (_image != null)
            _image.Opacity = 1.0;
    }

    public override void Exit(Action onComplete)
    {
        StopMotion();
        base.Exit(onComplete);
    }

    public override void Dispose()
    {
        StopMotion();
        _bitmap = null;
        _image = null;
        base.Dispose();
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static uint PackColor(Color c) => 0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>
    /// Fast check whether a cell's RGB color falls in the given ROYGBIV band.
    /// Uses integer-only max/min/delta to compute an approximate hue sector.
    /// </summary>
    private static bool CellMatchesBand(uint r, uint g, uint b, RoygbivColor band)
    {
        int ri = (int)r, gi = (int)g, bi = (int)b;
        int max = Math.Max(ri, Math.Max(gi, bi));
        int min = Math.Min(ri, Math.Min(gi, bi));
        int delta = max - min;

        // Low saturation → White band
        if (delta < 20 && max > 150)
            return band == RoygbivColor.White;
        if (delta < 5)
            return band == RoygbivColor.White;

        // Compute hue (0–360) using integer math
        int hue;
        if (max == ri)
            hue = 60 * (gi - bi) / delta;
        else if (max == gi)
            hue = 60 * (bi - ri) / delta + 120;
        else
            hue = 60 * (ri - gi) / delta + 240;
        if (hue < 0) hue += 360;

        var cellBand = (uint)hue switch
        {
            < 30 => RoygbivColor.Red,
            < 60 => RoygbivColor.Orange,
            < 90 => RoygbivColor.Yellow,
            < 180 => RoygbivColor.Green,
            < 210 => RoygbivColor.Blue,
            < 270 => RoygbivColor.Indigo,
            < 330 => RoygbivColor.Violet,
            _ => RoygbivColor.Red,
        };
        return cellBand == band;
    }

    private static Color HslToColor(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
