using System.Windows;
using System.Windows.Interop;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace VpinJukebox;

/// <summary>
/// Base class for all jukebox windows. Provides dev-mode dragging/resizing 
/// and an "Expand to Monitor" toggle.
/// </summary>
public class JukeboxWindow : Window
{
    private WindowLayout? _layout;
    private bool _isExpanded;
    private bool _resizable = true;

    public void SetResizable(bool resizable)
    {
        _resizable = resizable;
        if (resizable && !_isExpanded)
            ResizeMode = ResizeMode.CanResize;
        else
            ResizeMode = ResizeMode.NoResize;
    }

    public JukeboxWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize; // Win32 hook handles hit-testing
        Background = System.Windows.Media.Brushes.Black;
        ShowInTaskbar = true;
        ShowActivated = false;
        BorderThickness = new Thickness(0);

        // Set app icon for taskbar / window
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));

        Activated += (_, _) => UpdateExpandButtonVisibility(true);
        Deactivated += (_, _) => UpdateExpandButtonVisibility(false);

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Strip all chrome styles
            var style = GetWindowLong(handle, GWL_STYLE);
            var newStyle = style & ~WS_CAPTION;
            if (!_resizable)
                newStyle &= ~WS_THICKFRAME;
            SetWindowLong(handle, GWL_STYLE, newStyle);
            // Remove the extended window border style too
            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle & ~WS_EX_CLIENTEDGE & ~WS_EX_WINDOWEDGE);
            SetWindowPos(handle, nint.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
        };
    }

    private const int RESIZE_BORDER = 6;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_NCCALCSIZE)
        {
            // Zero non-client area
            handled = true;
            return nint.Zero;
        }

        if (msg == WM_NCHITTEST && !_isExpanded && _resizable)
        {
            // Provide resize handles within the client area
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

            var handle = new WindowInteropHelper(this).Handle;
            GetWindowRect(handle, out RECT rect);

            if (y < rect.Top + RESIZE_BORDER)
            {
                if (x < rect.Left + RESIZE_BORDER) { handled = true; return HTTOPLEFT; }
                if (x > rect.Right - RESIZE_BORDER) { handled = true; return HTTOPRIGHT; }
                handled = true; return HTTOP;
            }
            if (y > rect.Bottom - RESIZE_BORDER)
            {
                if (x < rect.Left + RESIZE_BORDER) { handled = true; return HTBOTTOMLEFT; }
                if (x > rect.Right - RESIZE_BORDER) { handled = true; return HTBOTTOMRIGHT; }
                handled = true; return HTBOTTOM;
            }
            if (x < rect.Left + RESIZE_BORDER) { handled = true; return HTLEFT; }
            if (x > rect.Right - RESIZE_BORDER) { handled = true; return HTRIGHT; }
        }

        return nint.Zero;
    }

    // Win32 interop for removing window chrome
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_CLIENTEDGE = 0x00000200;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const nint HTCAPTION = 2;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const nint HTLEFT = 10;
    private const nint HTRIGHT = 11;
    private const nint HTTOP = 12;
    private const nint HTTOPLEFT = 13;
    private const nint HTTOPRIGHT = 14;
    private const nint HTBOTTOM = 15;
    private const nint HTBOTTOMLEFT = 16;
    private const nint HTBOTTOMRIGHT = 17;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    protected static extern int GetWindowLong(nint hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    protected static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>
    /// Initiate a window drag via Win32. Works even when WinForms airspace
    /// blocks WPF mouse events (e.g. LibVLCSharp VideoView).
    /// </summary>
    public void BeginDragMove()
    {
        if (_isExpanded) return;
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    /// <summary>
    /// Apply a saved layout and show the window.
    /// </summary>
    public bool CheckWindowPositionOnStartup { get; set; } = true;

    public void ApplyLayout(WindowLayout layout)
    {
        _layout = layout;
        _isExpanded = layout.IsExpanded;

        // Always position at saved location first (needed for correct monitor detection)
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = layout.Left;
        Top = layout.Top;
        Width = layout.Width;
        Height = layout.Height;

        if (CheckWindowPositionOnStartup)
            EnsureVisibleOnScreen();

        if (_isExpanded)
        {
            // Defer expand until the window is loaded so the HWND exists
            // on the correct monitor and PresentationSource is available
            _isExpanded = false;
            void OnLoadedExpand(object s, RoutedEventArgs a)
            {
                Loaded -= OnLoadedExpand;
                ExpandToCurrentMonitor();
            }
            Loaded += OnLoadedExpand;
        }
    }

    /// <summary>
    /// Ensures the window is visible on at least one connected monitor.
    /// If the window is off-screen (e.g. a monitor was disconnected), it is
    /// re-centered on the primary screen.
    /// </summary>
    protected void EnsureVisibleOnScreen()
    {
        const int minVisible = 50;

        // Screen.WorkingArea returns physical pixels, but WPF Left/Top/Width/Height
        // are in device-independent units (DIPs). Convert screen bounds to DIPs so
        // the intersection check is correct on high-DPI or mixed-DPI setups.
        // Use the primary screen DPI via Win32; PresentationSource is not yet available.
        using var g = System.Drawing.Graphics.FromHwnd(nint.Zero);
        double dpiScale = g.DpiX / 96.0;

        var rect = new System.Drawing.Rectangle((int)Left, (int)Top, (int)Width, (int)Height);
        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var wa = screen.WorkingArea;
            var waDip = new System.Drawing.Rectangle(
                (int)(wa.Left / dpiScale),
                (int)(wa.Top / dpiScale),
                (int)(wa.Width / dpiScale),
                (int)(wa.Height / dpiScale));

            var intersection = System.Drawing.Rectangle.Intersect(rect, waDip);
            if (intersection.Width >= minVisible && intersection.Height >= minVisible)
                return;
        }

        var primary = WinFormsScreen.PrimaryScreen?.WorkingArea
                      ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        Left = primary.Left / dpiScale + (primary.Width / dpiScale - Width) / 2;
        Top = primary.Top / dpiScale + (primary.Height / dpiScale - Height) / 2;
    }

    /// <summary>
    /// Save current bounds back to the layout object.
    /// </summary>
    public void SaveLayout(WindowLayout layout)
    {
        layout.IsExpanded = _isExpanded;
        if (!_isExpanded)
        {
            layout.Left = Left;
            layout.Top = Top;
            layout.Width = Width;
            layout.Height = Height;
        }
    }

    /// <summary>
    /// Toggle between dev-size and full-monitor.
    /// </summary>
    public void ToggleExpand()
    {
        if (_isExpanded)
        {
            // Restore to saved dev-mode size
            _isExpanded = false;
            if (_resizable)
                ResizeMode = ResizeMode.CanResize;
            if (_layout != null)
            {
                Left = _layout.Left;
                Top = _layout.Top;
                Width = _layout.Width;
                Height = _layout.Height;
            }
            UpdateExpandButtonVisibility(IsActive);
        }
        else
        {
            // Save current position before expanding
            if (_layout != null)
            {
                _layout.Left = Left;
                _layout.Top = Top;
                _layout.Width = Width;
                _layout.Height = Height;
            }
            ExpandToCurrentMonitor();
            UpdateExpandButtonVisibility(IsActive);
        }
    }

    private void ExpandToCurrentMonitor()
    {
        _isExpanded = true;
        ResizeMode = ResizeMode.NoResize;

        // Find which monitor this window is currently on
        var handle = new WindowInteropHelper(this).Handle;
        var screen = WinFormsScreen.FromHandle(handle);
        var bounds = screen.Bounds;

        // Convert physical pixels to WPF device-independent units
        var source = PresentationSource.FromVisual(this);
        double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        Left = bounds.Left * dpiScaleX;
        Top = bounds.Top * dpiScaleY;
        Width = bounds.Width * dpiScaleX;
        Height = bounds.Height * dpiScaleY;
    }

    protected virtual void UpdateExpandButtonVisibility(bool isActive)
    {
        var btn = (FindName("ExpandButton") ?? FindName("FullscreenButton")) as UIElement;
        if (btn == null) return;
        btn.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Force-hide the expand button. Called by the DMD window when it
    /// receives focus so that satellite windows don't show stale buttons.
    /// </summary>
    public virtual void ForceHideExpandButton()
    {
        UpdateExpandButtonVisibility(false);
    }

    /// <summary>
    /// Reset this window to a default position on the primary screen.
    /// </summary>
    public void ResetPosition(double left, double top, double width, double height)
    {
        _isExpanded = false;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (_layout != null)
        {
            _layout.Left = left;
            _layout.Top = top;
            _layout.Width = width;
            _layout.Height = height;
            _layout.IsExpanded = false;
        }
        UpdateExpandButtonVisibility(IsActive);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_isExpanded && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* mouse button released before DragMove could start */ }
        }
    }

    private AppSettings? _appSettings;

    public void SetAppSettings(AppSettings settings)
    {
        _appSettings = settings;
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F11)
            ToggleExpand();

        var bindings = _appSettings?.KeyBindings ?? new KeyBindings();
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (bindings.TryGetAction(key, out var action) && action == JukeboxAction.ExitApp)
        {
            e.Handled = true;
            // Application.Current.MainWindow throws if accessed from a non-main thread,
            // so dispatch the entire close operation to the application dispatcher.
            Application.Current.Dispatcher.BeginInvoke(() => Application.Current.MainWindow?.Close());
        }
    }
}
