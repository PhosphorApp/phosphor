using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Phosphor;

/// <summary>
/// Base class for all jukebox windows. Provides dev-mode dragging/resizing 
/// and an "Expand to Monitor" toggle.
/// </summary>
public class JukeboxWindow : Window
{
    /// <summary>
    /// Shared epoch for logo spin animations so all windows stay in phase
    /// regardless of when their animation starts.
    /// </summary>
    protected static readonly DateTime SpinEpoch = DateTime.UtcNow;
    protected const double SpinDurationSeconds = 60.0;

    private WindowLayout? _layout;
    private bool _isExpanded;
    private bool _resizable = true;
    private int _lastRefreshRate;
    private string _lastMonitorDevice = string.Empty;

    public void SetResizable(bool resizable)
    {
        _resizable = resizable;
        if (resizable && !_isExpanded)
            ResizeMode = ResizeMode.CanResize;
        else
            ResizeMode = ResizeMode.NoResize;

        // Changing ResizeMode makes WPF issue a frame change, which lets DWM
        // briefly re-evaluate the default frame (visible as a border flash).
        // Re-strip the chrome styles and re-suppress the DWM frame so the
        // toggle stays seamless.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            var style = GetWindowLong(handle, GWL_STYLE) & ~WS_CAPTION;
            if (!resizable)
                style &= ~WS_THICKFRAME;
            else
                style |= WS_THICKFRAME;
            SetWindowLong(handle, GWL_STYLE, style);
            SetWindowPos(handle, nint.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            SuppressDwmFrame(handle);
        }
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

            // Tell DWM not to composite a default (light) frame for the
            // reserved WS_THICKFRAME sizing border. Without this, resizable
            // borderless windows flash a ~5-8px white border on first launch
            // until a repaint/resize occurs. Zero margins = no glass frame.
            SuppressDwmFrame(handle);

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

        if (msg == WM_NCPAINT)
        {
            // Swallow non-client painting entirely. Even with the DWM frame
            // suppressed, activation causes USER32 to repaint the classic
            // non-client frame (drawn white), which is what reappears when the
            // window is clicked/focused. Returning 0 without calling DefWindowProc
            // prevents that frame from being drawn.
            handled = true;
            return nint.Zero;
        }

        if (msg == WM_NCACTIVATE)
        {
            // On activation state changes Windows repaints the non-client frame.
            // Pass lParam = -1 to tell DefWindowProc not to repaint the frame,
            // while still returning TRUE so activation proceeds normally.
            handled = true;
            return DefWindowProc(hwnd, msg, wParam, new nint(-1));
        }

        if (msg == WM_ERASEBKGND)
        {
            // Paint the entire window surface black the instant the HWND
            // becomes visible. This prevents a white flash in the reserved
            // sizing-border area before WPF composites its first (black) frame
            // — the primary cause of the "white border on first launch" artifact.
            var hdc = wParam;
            if (hdc != nint.Zero && GetClientRect(hwnd, out RECT client))
            {
                var brush = GetStockObject(BLACK_BRUSH);
                FillRect(hdc, ref client, brush);
            }
            handled = true;
            return (nint)1;
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
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_NCPAINT = 0x0085;
    private const int WM_NCACTIVATE = 0x0086;
    private const nint HTCAPTION = 2;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int BLACK_BRUSH = 4;

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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, int msg, nint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    // DWM window attributes
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_BORDER_COLOR = 34;              // Win11 (build 22000+)
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;  // Win11 (build 22000+)
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // Win10 1809+ / Win11
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;       // suppress the border entirely

    /// <summary>
    /// Suppresses the DWM-composited window frame for a custom-chrome window.
    /// Extending the frame with zero margins tells DWM not to draw a default
    /// (light/glass) border in the reserved WS_THICKFRAME sizing area, which
    /// is otherwise visible as a white border flash on first launch. Also
    /// disables non-client rendering and the open/close transition animations
    /// that can briefly reveal the default frame on launch/shutdown/toggle,
    /// and (on Windows 11) removes the accent border and rounded-corner
    /// rendering that shows up as lingering white corner brackets / edge lines.
    /// </summary>
    private static void SuppressDwmFrame(nint handle)
    {
        try
        {
            var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
            DwmExtendFrameIntoClientArea(handle, ref margins);

            int ncrp = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(handle, DWMWA_NCRENDERING_POLICY, ref ncrp, sizeof(int));

            int disableTransitions = 1;
            DwmSetWindowAttribute(handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));

            // Windows 11: remove the 1px accent border and square off the
            // corners. The anti-aliased rounded corners composited against the
            // frame are what appear as the white L-shaped brackets. These
            // attributes are silently ignored on Windows 10 and earlier.
            int noRound = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));

            int borderColor = unchecked((int)DWMWA_COLOR_NONE);
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // Force DWM to use the dark frame palette for this window so any
            // residual frame the compositor draws (e.g. the first frame before
            // suppression takes effect) is dark rather than white, regardless
            // of the user's current theme. Ignored on Windows 10 < 1809.
            int darkMode = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch (DllNotFoundException) { /* DWM unavailable (very old OS) */ }
    }

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
    /// Begins a Win32 move OR resize based on where inside the client area the user
    /// pressed. Used to forward mouse-down from a WinForms-hosted LibVLC VideoView
    /// (whose airspace HWND swallows WPF mouse input), so windows can still be moved
    /// and resized while a video is on screen. <paramref name="clientX"/>/<paramref name="clientY"/>
    /// are relative to the client area (top-left = 0,0). Interior presses move the
    /// window; presses within <see cref="RESIZE_BORDER"/> of an edge start a resize in
    /// that direction (only when resizable and not expanded).
    /// </summary>
    public void BeginDragOrResizeFromChild(int clientX, int clientY)
    {
        if (_isExpanded) return;
        var handle = new WindowInteropHelper(this).Handle;

        nint ht = ComputeChildHitTest(handle, clientX, clientY);
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, ht, 0);
    }

    /// <summary>
    /// Returns the appropriate resize/move cursor for a point inside the client area,
    /// or null when the default (arrow) cursor should be used. Lets a hosted VideoView
    /// show sizing cursors near the edges while resizable.
    /// </summary>
    public System.Windows.Forms.Cursor GetChildResizeCursor(int clientX, int clientY)
    {
        if (_isExpanded || !_resizable)
            return System.Windows.Forms.Cursors.Default;

        var handle = new WindowInteropHelper(this).Handle;
        return ComputeChildHitTest(handle, clientX, clientY) switch
        {
            HTTOPLEFT or HTBOTTOMRIGHT => System.Windows.Forms.Cursors.SizeNWSE,
            HTTOPRIGHT or HTBOTTOMLEFT => System.Windows.Forms.Cursors.SizeNESW,
            HTLEFT or HTRIGHT => System.Windows.Forms.Cursors.SizeWE,
            HTTOP or HTBOTTOM => System.Windows.Forms.Cursors.SizeNS,
            _ => System.Windows.Forms.Cursors.Default,
        };
    }

    /// <summary>
    /// Maps a client-area point to a Win32 hit-test code: an edge/corner HT* when
    /// resizable and within the resize border, otherwise HTCAPTION (move).
    /// </summary>
    private nint ComputeChildHitTest(nint handle, int clientX, int clientY)
    {
        if (!_resizable || !GetClientRect(handle, out RECT client))
            return HTCAPTION;

        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        bool left = clientX < RESIZE_BORDER;
        bool right = clientX > w - RESIZE_BORDER;
        bool top = clientY < RESIZE_BORDER;
        bool bottom = clientY > h - RESIZE_BORDER;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCAPTION;
    }

    /// <summary>
    /// Apply a saved layout and show the window.
    /// </summary>
    public bool CheckWindowPositionOnStartup { get; set; } = true;

    /// <summary>
    /// Raised once after the window has reached its final startup size
    /// (either the saved layout or after expanding to the monitor).
    /// </summary>
    public event Action? LayoutSettled;

    /// <summary>True after <see cref="LayoutSettled"/> has fired.</summary>
    public bool IsLayoutSettled { get; private set; }

    private void RaiseLayoutSettled()
    {
        IsLayoutSettled = true;
        DetectRefreshRate();
        LayoutSettled?.Invoke();
    }

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
                RaiseLayoutSettled();
            }
            Loaded += OnLoadedExpand;
        }
        else
        {
            // Not expanding — settled once Loaded fires
            void OnLoadedSettled(object s, RoutedEventArgs a)
            {
                Loaded -= OnLoadedSettled;
                RaiseLayoutSettled();
            }
            Loaded += OnLoadedSettled;
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
        // Find which monitor this window is currently on
        var handle = new WindowInteropHelper(this).Handle;
        ExpandToScreen(WinFormsScreen.FromHandle(handle));
    }

    private void ExpandToScreen(WinFormsScreen screen)
    {
        _isExpanded = true;
        ResizeMode = ResizeMode.NoResize;

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

    /// <summary>
    /// Moves the window to the next monitor (round-robin) and expands it fullscreen there.
    /// Automates sending the viewer to a second display / TV. No-op with a single monitor.
    /// </summary>
    public void MoveToNextDisplay()
    {
        var screens = WinFormsScreen.AllScreens;
        if (screens.Length < 2) return;

        // Save the current windowed position before we leave it (first move only).
        if (!_isExpanded && _layout != null)
        {
            _layout.Left = Left;
            _layout.Top = Top;
            _layout.Width = Width;
            _layout.Height = Height;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var current = WinFormsScreen.FromHandle(handle);
        int index = Array.IndexOf(screens, current);
        if (index < 0) index = 0;
        var next = screens[(index + 1) % screens.Length];

        ExpandToScreen(next);
        UpdateExpandButtonVisibility(IsActive);
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

    /// <summary>
    /// The current monitor refresh rate in Hz, or 0 if unknown.
    /// </summary>
    public int RefreshRateHz => _lastRefreshRate;

    /// <summary>
    /// Detects the refresh rate of the monitor this window currently occupies
    /// and logs changes. Safe to call at any time; failures are swallowed.
    /// </summary>
    private void DetectRefreshRate()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero) return;

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(monitor, ref info)) return;

            var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, ref dm)) return;

            var hz = (int)dm.dmDisplayFrequency;
            var device = info.szDevice.TrimEnd('\0');

            if (hz != _lastRefreshRate || device != _lastMonitorDevice)
            {
                _lastRefreshRate = hz;
                _lastMonitorDevice = device;
                DebugLog.Log($"{GetType().Name}: monitor {device} running at {hz} Hz");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log($"{GetType().Name}: failed to detect refresh rate – {ex.Message}");
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsLayoutSettled)
            DetectRefreshRate();
    }

    #region Refresh-rate P/Invoke

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT2 rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT2 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion;
        public ushort dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    #endregion

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

    /// <summary>Current settings, if applied. Exposed to derived windows for visibility gating.</summary>
    protected AppSettings? AppSettings => _appSettings;

    /// <summary>
    /// True when the "move viewer to next display" controls should be offered: the user opted in
    /// via <see cref="AppSettings.ShowMoveViewerButtons"/> and at least two displays are connected
    /// (the move is a no-op with a single monitor).
    /// </summary>
    protected bool MoveViewerButtonsAllowed =>
        _appSettings?.ShowMoveViewerButtons == true && WinFormsScreen.AllScreens.Length >= 2;

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
