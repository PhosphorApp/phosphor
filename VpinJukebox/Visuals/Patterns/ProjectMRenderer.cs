using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D9;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static VpinJukebox.ProjectMInterop;

namespace VpinJukebox;

/// <summary>
/// Manages a hidden OpenGL context, a projectM instance, and either a
/// zero-copy D3D9/OpenGL shared surface (<see cref="D3DImage"/>) or a
/// fallback pixel-readback path (<see cref="WriteableBitmap"/>) for WPF display.
/// </summary>
public sealed class ProjectMRenderer : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hdc;
    private IntPtr _hglrc;
    private IntPtr _projectM;
    private IntPtr _playlist;
    private int _width;
    private int _height;
    private volatile bool _disposed;
    private volatile bool _initialized;
    private bool _isBrowsing;

    // ── Shared surface (zero-copy) path ──────────────────────────
    private bool _useSharedSurface;

    // D3D11 device + shared texture (render target for OpenGL via WGL interop)
    private ID3D11Device? _d3d11Device;
    private ID3D11Texture2D? _d3d11Texture;

    // D3D9Ex device + surface opened from the same DXGI shared handle (for WPF D3DImage)
    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3dDevice;
    private IDirect3DTexture9? _d3dTexture;
    private IDirect3DSurface9? _d3dSurface;

    private D3DImage? _d3dImage;
    private IntPtr _wglDxDevice;
    private IntPtr _wglDxObject;
    private uint _glTexture;
    private uint _glFbo;
    private uint _glDepthRb;

    // WGL_NV_DX_interop function pointers
    private WglDXOpenDeviceNVDelegate? _wglDXOpenDeviceNV;
    private WglDXCloseDeviceNVDelegate? _wglDXCloseDeviceNV;
    private WglDXRegisterObjectNVDelegate? _wglDXRegisterObjectNV;
    private WglDXUnregisterObjectNVDelegate? _wglDXUnregisterObjectNV;
    private WglDXLockObjectsNVDelegate? _wglDXLockObjectsNV;
    private WglDXUnlockObjectsNVDelegate? _wglDXUnlockObjectsNV;

    // GL extension function pointers
    private GlGenFramebuffersDelegate? _glGenFramebuffers;
    private GlDeleteFramebuffersDelegate? _glDeleteFramebuffers;
    private GlBindFramebufferDelegate? _glBindFramebuffer;
    private GlFramebufferTexture2DDelegate? _glFramebufferTexture2D;
    private GlGenRenderbuffersDelegate? _glGenRenderbuffers;
    private GlDeleteRenderbuffersDelegate? _glDeleteRenderbuffers;
    private GlBindRenderbufferDelegate? _glBindRenderbuffer;
    private GlRenderbufferStorageDelegate? _glRenderbufferStorage;
    private GlFramebufferRenderbufferDelegate? _glFramebufferRenderbuffer;
    private GlCheckFramebufferStatusDelegate? _glCheckFramebufferStatus;
    private GlBlitFramebufferDelegate? _glBlitFramebuffer;

    // ── Fallback (readback) path ─────────────────────────────────
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private byte[]? _flippedBuffer;

    /// <summary>
    /// Serializes all native projectM/OpenGL calls to prevent concurrent access
    /// (e.g. preset browsing while CompositionTarget.Rendering fires).
    /// </summary>
    private readonly object _nativeLock = new();

    public ImageSource? ImageSource => _useSharedSurface ? (ImageSource?)_d3dImage : _bitmap;
    public bool IsAvailable => _initialized && !_disposed;

    /// <summary>True when the zero-copy D3D9/GL shared surface path is active.</summary>
    public bool IsUsingSharedSurface => _useSharedSurface;

    /// <summary>Gets the total number of presets in the playlist.</summary>
    public uint PresetCount => _playlist != IntPtr.Zero ? projectm_playlist_size(_playlist) : 0;

    /// <summary>Gets the currently active preset index.</summary>
    public uint CurrentPresetIndex => _playlist != IntPtr.Zero ? projectm_playlist_get_position(_playlist) : 0;

    /// <summary>
    /// Path to a folder containing .milk preset files.
    /// </summary>
    public static string PresetPath { get; set; } = "";

    /// <summary>
    /// Optional path to the Milkdrop texture pack folder.
    /// </summary>
    public static string TexturePath { get; set; } = "";

    /// <summary>Seconds between automatic preset transitions. Default 30.</summary>
    public static double PresetDuration { get; set; } = 30.0;

    /// <summary>Seconds for the soft-cut crossfade between presets. Default 3.</summary>
    public static double SoftCutDuration { get; set; } = 3.0;

    /// <summary>Enable beat-driven hard cuts between presets. Default true.</summary>
    public static bool HardCutEnabled { get; set; } = true;

    /// <summary>Mesh quality (higher = smoother waves, more GPU). 32–128 typical. Default 48.</summary>
    public static uint MeshSize { get; set; } = 48;

    /// <summary>Render scale relative to canvas size (0.25–1.0). Default 0.5.</summary>
    public static double RenderScale { get; set; } = 0.5;

    /// <summary>Beat sensitivity multiplier (0.0–3.0). Higher = more reactive visuals. Default 1.0.</summary>
    public static float BeatSensitivity { get; set; } = 1.0f;

    /// <summary>
    /// When set, only these subfolder names (relative to PresetPath) will be loaded.
    /// If empty or null, all subfolders are loaded.
    /// </summary>
    public static List<string>? EnabledFolders { get; set; }

    // prevent GC collection of the callback delegate
    private ProjectMInterop.ProjectMPresetSwitchedCallback? _presetSwitchedCallback;

    /// <summary>
    /// Seconds to wait after a hard-cut preset switch before sampling the dominant color.
    /// For soft cuts, <see cref="SoftCutDuration"/> is added automatically.
    /// </summary>
    public static double ColorSampleDelaySeconds { get; set; } = 1.0;

    private long _colorSampleTargetTick = -1;
    private readonly System.Diagnostics.Stopwatch _colorSampleWatch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Raised when the dominant color band is determined after a preset switch.
    /// Fires on the render thread (UI thread).
    /// </summary>
    public event Action<ColorAnalysis>? ColorBandChanged;

    /// <summary>
    /// Preset monitor mode: 0 = off, 1 = log black presets, 2 = log and move to Deactivated.
    /// </summary>
    public static int PresetMonitorMode { get; set; }

    /// <summary>
    /// When true, renders via the software readback path. When false, attempts the
    /// zero-copy D3D9/GL shared-surface path (faster but renders black on some
    /// NVIDIA + WPF D3DImage configurations).
    /// </summary>
    public static bool SoftwareRender { get; set; } = true;

    /// <summary>
    /// Raised when a preset is confirmed black after multiple consecutive samples.
    /// The string parameter is the full file path of the preset.
    /// </summary>
    public event Action<string>? BlackPresetDetected;

    // Black-frame detection tuning
    /// <summary>Number of consecutive black-frame samples required to confirm a preset is black.</summary>
    public static int BlackCheckRequiredHits { get; set; } = 5;
    /// <summary>Seconds between each black-frame recheck after the initial detection.</summary>
    public static double BlackCheckIntervalSeconds { get; set; } = 2.5;

    /// <summary>Top percentile (0-100) of brightest pixels to average for black-frame detection.</summary>
    public static double BlackCheckPercentile { get; set; } = 5.0;

    /// <summary>Luminance threshold below which a frame is considered black.</summary>
    public static double BlackCheckLuminanceThreshold { get; set; } = 4.0;

    /// <summary>When true, saves a PNG snapshot of each confirmed black frame for diagnostics.</summary>
    public static bool SaveBlackFrame { get; set; }

    /// <summary>When true, saves a PNG snapshot of each color-sampled frame for diagnostics.</summary>
    public static bool SaveColorSampleFrame { get; set; }

    // Black-frame detection state
    private long _blackCheckTargetTick = -1;
    private int _blackCheckHitCount;
    private string? _currentPresetFullPath;

    public bool Initialize(int pixelWidth, int pixelHeight)
    {
        try
        {
            _width = pixelWidth;
            _height = pixelHeight;

            Log("Creating hidden window for OpenGL context...");

            // Create a tiny hidden window to host the OpenGL context
            // WS_EX_NOACTIVATE prevents this window from stealing focus during GL operations
            const int WS_EX_NOACTIVATE = 0x08000000;
            _hwnd = CreateWindowExW(WS_EX_NOACTIVATE, "STATIC", "ProjectMGL", 0,
                0, 0, 4, 4, IntPtr.Zero, IntPtr.Zero,
                Marshal.GetHINSTANCE(typeof(ProjectMRenderer).Module), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Log($"FAILED: CreateWindowEx error {Marshal.GetLastWin32Error()}");
                return false;
            }

            _hdc = GetDC(_hwnd);
            if (_hdc == IntPtr.Zero)
            {
                Log("FAILED: GetDC returned null");
                return false;
            }

            var pfd = new PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
                nVersion = 1,
                dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
                iPixelType = 0, // PFD_TYPE_RGBA
                cColorBits = 32,
                cDepthBits = 24,
                cStencilBits = 8,
            };

            int pixelFormat = ChoosePixelFormat(_hdc, ref pfd);
            if (pixelFormat == 0 || !SetPixelFormat(_hdc, pixelFormat, ref pfd))
            {
                Log($"FAILED: PixelFormat error {Marshal.GetLastWin32Error()}");
                return false;
            }

            _hglrc = wglCreateContext(_hdc);
            if (_hglrc == IntPtr.Zero)
            {
                Log("FAILED: wglCreateContext returned null");
                return false;
            }

            if (!wglMakeCurrent(_hdc, _hglrc))
            {
                Log("FAILED: wglMakeCurrent failed");
                return false;
            }

            // Upgrade to a modern OpenGL 3.3 core profile context (required by projectM 4)
            var wglCreateContextAttribsPtr = wglGetProcAddress("wglCreateContextAttribsARB");
            if (wglCreateContextAttribsPtr != IntPtr.Zero)
            {
                var wglCreateContextAttribsARB = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARBDelegate>(wglCreateContextAttribsPtr);
                int[] attribs = {
                    WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                    WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                    WGL_CONTEXT_PROFILE_MASK_ARB, WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                    0
                };
                IntPtr modernContext = wglCreateContextAttribsARB(_hdc, IntPtr.Zero, attribs);
                if (modernContext != IntPtr.Zero)
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(_hglrc);
                    _hglrc = modernContext;
                    if (!wglMakeCurrent(_hdc, _hglrc))
                    {
                        Log("FAILED: wglMakeCurrent failed for modern context");
                        return false;
                    }
                    Log("Upgraded to OpenGL 3.3 core profile context");
                }
                else
                {
                    Log("WARNING: wglCreateContextAttribsARB returned null, using legacy context");
                }
            }
            else
            {
                Log("WARNING: wglCreateContextAttribsARB not available");
            }

            Log("OpenGL context created successfully");

            // Initialize GLEW so that modern GL function pointers are available
            uint glewErr = glewInit();
            if (glewErr != GLEW_OK)
            {
                Log($"FAILED: glewInit returned error {glewErr}");
                return false;
            }
            Log("GLEW initialized successfully");

            glViewport(0, 0, _width, _height);

            // Resolve GL extension function pointers needed for FBO rendering
            _glGenFramebuffers = GetGlProc<GlGenFramebuffersDelegate>("glGenFramebuffers");
            _glDeleteFramebuffers = GetGlProc<GlDeleteFramebuffersDelegate>("glDeleteFramebuffers");
            _glBindFramebuffer = GetGlProc<GlBindFramebufferDelegate>("glBindFramebuffer");
            _glFramebufferTexture2D = GetGlProc<GlFramebufferTexture2DDelegate>("glFramebufferTexture2D");
            _glGenRenderbuffers = GetGlProc<GlGenRenderbuffersDelegate>("glGenRenderbuffers");
            _glDeleteRenderbuffers = GetGlProc<GlDeleteRenderbuffersDelegate>("glDeleteRenderbuffers");
            _glBindRenderbuffer = GetGlProc<GlBindRenderbufferDelegate>("glBindRenderbuffer");
            _glRenderbufferStorage = GetGlProc<GlRenderbufferStorageDelegate>("glRenderbufferStorage");
            _glFramebufferRenderbuffer = GetGlProc<GlFramebufferRenderbufferDelegate>("glFramebufferRenderbuffer");
            _glCheckFramebufferStatus = GetGlProc<GlCheckFramebufferStatusDelegate>("glCheckFramebufferStatus");
            _glBlitFramebuffer = GetGlProc<GlBlitFramebufferDelegate>("glBlitFramebuffer");

            // Try to set up D3D9/GL shared surface for zero-copy WPF display.
            // Opt-in only: the shared-surface path works on some systems but renders
            // black on others due to WPF D3DImage / D3D9Ex cross-device interop issues.
            // Controlled by the ProjectMSoftwareRender setting (default true = use fallback).
            if (!SoftwareRender)
            {
                try
                {
                    _useSharedSurface = TryInitSharedSurface();
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
                {
                    Log($"Shared surface unavailable (missing dependency: {ex.Message}) — using fallback readback path");
                    _useSharedSurface = false;
                }
            }
            else
            {
                Log("Software render mode enabled (ProjectMSoftwareRender=true) — using fallback readback path");
                _useSharedSurface = false;
            }

            // Create projectM instance
            _projectM = projectm_create();
            if (_projectM == IntPtr.Zero)
            {
                Log("FAILED: projectm_create returned null");
                return false;
            }

            projectm_set_window_size(_projectM, (uint)_width, (uint)_height);
            projectm_set_preset_duration(_projectM, PresetDuration);
            projectm_set_soft_cut_duration(_projectM, SoftCutDuration);
            projectm_set_hard_cut_enabled(_projectM, HardCutEnabled);
            projectm_set_mesh_size(_projectM, MeshSize, MeshSize);
            projectm_set_beat_sensitivity(_projectM, BeatSensitivity);

            // Set texture search path if provided
            if (!string.IsNullOrEmpty(TexturePath) && System.IO.Directory.Exists(TexturePath))
            {
                var paths = new[] { TexturePath };
                projectm_set_texture_search_paths(_projectM, paths, 1);
                Log($"Texture path set: {TexturePath}");
            }

            Log($"projectM instance created ({_width}x{_height})");

            // Load presets via playlist API
            if (!string.IsNullOrEmpty(PresetPath) && System.IO.Directory.Exists(PresetPath))
            {
                _playlist = projectm_playlist_create(_projectM);

                uint count = 0;
                if (EnabledFolders != null && EnabledFolders.Count > 0)
                {
                    // Load only selected subfolders
                    foreach (var folder in EnabledFolders)
                    {
                        var fullPath = System.IO.Path.Combine(PresetPath, folder);
                        if (System.IO.Directory.Exists(fullPath))
                        {
                            uint added = projectm_playlist_add_path(_playlist, fullPath, true, false);
                            count += added;
                            Log($"  Folder '{folder}': {added} presets");
                        }
                        else
                        {
                            Log($"  Folder '{folder}': not found, skipping");
                        }
                    }
                }
                else
                {
                    // Load all presets recursively, excluding special folders
                    foreach (var dir in System.IO.Directory.GetDirectories(PresetPath))
                    {
                        if (IsExcludedFolder(System.IO.Path.GetFileName(dir)))
                            continue;
                        count += projectm_playlist_add_path(_playlist, dir, true, false);
                    }
                    // Also load any .milk files directly in the root
                    count += projectm_playlist_add_path(_playlist, PresetPath, false, false);
                }

                projectm_playlist_set_shuffle(_playlist, true);
                Log($"Loaded {count} presets from {PresetPath}");

                // Register preset-switch callback for debug logging
                _presetSwitchedCallback = OnPresetSwitched;
                projectm_playlist_set_preset_switched_event_callback(_playlist, _presetSwitchedCallback, IntPtr.Zero);

                if (count > 0)
                    projectm_playlist_play_next(_playlist, false);
            }
            else
            {
                Log($"WARNING: Preset path not found or empty: '{PresetPath}'");
            }

            // Fallback: WriteableBitmap + glReadPixels path
            if (!_useSharedSurface)
            {
                _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            }

            // Pixel buffers for color/black-frame analysis (needed regardless of surface mode)
            _pixelBuffer = new byte[_width * _height * 4];
            _flippedBuffer = new byte[_width * _height * 4];

            _initialized = true;
            var renderPath = _useSharedSurface ? "SHARED SURFACE (zero-copy D3D9/GL)" : "FALLBACK (WriteableBitmap + glReadPixels)";
            Log($"Initialization complete — render path: {renderPath} ({_width}x{_height})");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Log($"FAILED: Native DLL not found: {ex.Message}");
            Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Log($"FAILED with exception: {ex.Message}");
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Attempts to create a D3D11 device, a DXGI-shared texture, open it in D3D9Ex
    /// for WPF D3DImage, and register it with OpenGL via WGL_NV_DX_interop for
    /// zero-copy rendering. Returns true on success; on failure the renderer
    /// falls back to WriteableBitmap. Must be called after the GL context and GLEW are initialized.
    /// </summary>
    private bool TryInitSharedSurface()
    {
        try
        {
            // 1. Check for WGL_NV_DX_interop extension
            _wglDXOpenDeviceNV = GetWglProc<WglDXOpenDeviceNVDelegate>("wglDXOpenDeviceNV");
            _wglDXCloseDeviceNV = GetWglProc<WglDXCloseDeviceNVDelegate>("wglDXCloseDeviceNV");
            _wglDXRegisterObjectNV = GetWglProc<WglDXRegisterObjectNVDelegate>("wglDXRegisterObjectNV");
            _wglDXUnregisterObjectNV = GetWglProc<WglDXUnregisterObjectNVDelegate>("wglDXUnregisterObjectNV");
            _wglDXLockObjectsNV = GetWglProc<WglDXLockObjectsNVDelegate>("wglDXLockObjectsNV");
            _wglDXUnlockObjectsNV = GetWglProc<WglDXUnlockObjectsNVDelegate>("wglDXUnlockObjectsNV");

            if (_wglDXOpenDeviceNV == null || _wglDXCloseDeviceNV == null ||
                _wglDXRegisterObjectNV == null || _wglDXUnregisterObjectNV == null ||
                _wglDXLockObjectsNV == null || _wglDXUnlockObjectsNV == null)
            {
                Log("WGL_NV_DX_interop not available — using fallback readback path");
                return false;
            }

            // 2. Check for required GL FBO functions
            if (_glGenFramebuffers == null || _glBindFramebuffer == null ||
                _glFramebufferTexture2D == null || _glCheckFramebufferStatus == null ||
                _glGenRenderbuffers == null || _glBindRenderbuffer == null ||
                _glRenderbufferStorage == null || _glFramebufferRenderbuffer == null ||
                _glBlitFramebuffer == null)
            {
                Log("GL framebuffer extensions not available — using fallback readback path");
                return false;
            }

            // 3. Create D3D11 device (BgraSupport required for DXGI shared-handle textures)
            Log("Creating D3D11 device for shared surface...");
            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out _d3d11Device,
                out _,
                out _).CheckError();
            Log($"D3D11 device created (feature level: {_d3d11Device!.FeatureLevel})");

            // 4. Create D3D9Ex device (WPF D3DImage requires a D3D9 surface)
            Log("Creating D3D9Ex device...");
            _d3d9 = D3D9.Direct3DCreate9Ex();
            var presentParams = new Vortice.Direct3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                PresentationInterval = PresentInterval.Immediate,
                BackBufferFormat = Vortice.Direct3D9.Format.Unknown,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferCount = 1,
            };

            _d3dDevice = _d3d9.CreateDeviceEx(
                0,
                Vortice.Direct3D9.DeviceType.Hardware,
                _hwnd,
                Vortice.Direct3D9.CreateFlags.HardwareVertexProcessing | Vortice.Direct3D9.CreateFlags.Multithreaded | Vortice.Direct3D9.CreateFlags.FpuPreserve,
                presentParams);

            // 5. Register D3D11 device with WGL (WGL_NV_DX_interop2 supports D3D10/11)
            _wglDxDevice = _wglDXOpenDeviceNV(_d3d11Device.NativePointer);
            if (_wglDxDevice == IntPtr.Zero)
            {
                Log($"wglDXOpenDeviceNV failed for D3D11 device (Win32 error: {Marshal.GetLastWin32Error()}) — using fallback readback path");
                CleanupSharedSurface();
                return false;
            }
            Log($"wglDXOpenDeviceNV OK (D3D11): device=0x{_wglDxDevice:X}");

            // 6. Create the shared texture and GL resources
            if (!CreateSharedTextureResources())
            {
                CleanupSharedSurface();
                return false;
            }

            // 7. Create WPF D3DImage — uses the D3D9 surface backed by DXGI shared memory
            _d3dImage = new D3DImage();
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3dSurface!.NativePointer, true);
            _d3dImage.Unlock();

            Log($"Shared GPU surface initialized — zero-copy D3D11/GL interop active ({_width}x{_height})");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Shared surface init failed: {ex.Message} — using fallback readback path");
            CleanupSharedSurface();
            return false;
        }
    }

    /// <summary>
    /// Creates the D3D11 shared texture, opens it in D3D9Ex, registers with GL,
    /// and creates the FBO with depth/stencil for projectM rendering.
    /// Called during init and resize.
    /// </summary>
    private bool CreateSharedTextureResources()
    {
        if (_d3d11Device == null || _d3dDevice == null || _wglDxDevice == IntPtr.Zero)
            return false;

        Log($"Creating shared texture resources ({_width}x{_height})...");

        // 1. Create D3D11 render-target texture with DXGI shared handle
        var texDesc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.Shared,
        };
        _d3d11Texture = _d3d11Device.CreateTexture2D(texDesc);
        Log($"  D3D11 texture created: 0x{_d3d11Texture.NativePointer:X}");

        // 2. Get DXGI shared handle from D3D11 texture
        using var dxgiResource = _d3d11Texture.QueryInterface<IDXGIResource>();
        IntPtr sharedHandle = dxgiResource.SharedHandle;
        if (sharedHandle == IntPtr.Zero)
        {
            Log("  DXGI shared handle is null — cannot share with D3D9");
            return false;
        }
        Log($"  DXGI shared handle: 0x{sharedHandle:X}");

        // 3. Open the shared handle in D3D9Ex as a texture (same GPU memory)
        _d3dTexture = _d3dDevice.CreateTexture(
            (uint)_width, (uint)_height, 1,
            Vortice.Direct3D9.Usage.RenderTarget,
            Vortice.Direct3D9.Format.A8R8G8B8,
            Pool.Default,
            ref sharedHandle);
        _d3dSurface = _d3dTexture.GetSurfaceLevel(0);
        Log($"  D3D9 texture opened from shared handle: surface=0x{_d3dSurface.NativePointer:X}");

        // 4. Create GL texture and register the D3D11 texture with WGL_NV_DX_interop
        glGenTextures(1, out _glTexture);
        Log($"  GL texture created: id={_glTexture}");

        // Register the D3D11 texture with OpenGL
        _wglDxObject = _wglDXRegisterObjectNV!(
            _wglDxDevice,
            _d3d11Texture.NativePointer,
            _glTexture,
            GL_TEXTURE_2D,
            WGL_ACCESS_WRITE_DISCARD_NV);

        if (_wglDxObject == IntPtr.Zero)
        {
            uint glErr = glGetError();
            Log($"  wglDXRegisterObjectNV FAILED (glError: 0x{glErr:X}, Win32: {Marshal.GetLastWin32Error()})");
            return false;
        }
        Log($"  wglDXRegisterObjectNV OK: handle=0x{_wglDxObject:X}");

        // 5. Create FBO and attach the shared texture as color attachment
        _glGenFramebuffers!(1, out _glFbo);
        _glBindFramebuffer!(GL_FRAMEBUFFER, _glFbo);
        Log($"  FBO created: id={_glFbo}");

        // Lock for GL access to attach
        _wglDXLockObjectsNV!(_wglDxDevice, 1, [_wglDxObject]);
        _glFramebufferTexture2D!(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _glTexture, 0);

        // Create depth/stencil renderbuffer (projectM needs depth/stencil)
        _glGenRenderbuffers!(1, out _glDepthRb);
        _glBindRenderbuffer!(GL_RENDERBUFFER, _glDepthRb);
        _glRenderbufferStorage!(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, _width, _height);
        _glFramebufferRenderbuffer!(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, _glDepthRb);

        var status = _glCheckFramebufferStatus!(GL_FRAMEBUFFER);
        _wglDXUnlockObjectsNV!(_wglDxDevice, 1, [_wglDxObject]);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            Log($"  GL framebuffer incomplete: 0x{status:X}");
            return false;
        }
        Log($"  FBO complete — shared texture resources ready");

        _glBindFramebuffer!(GL_FRAMEBUFFER, 0);

        return true;
    }

    /// <summary>
    /// Tears down only the shared texture/FBO resources (not the devices or WGL device handle).
    /// Used during resize to recreate at new dimensions.
    /// </summary>
    private void DestroySharedTextureResources()
    {
        if (_wglDxObject != IntPtr.Zero && _wglDXUnregisterObjectNV != null)
        {
            _wglDXUnregisterObjectNV(_wglDxDevice, _wglDxObject);
            _wglDxObject = IntPtr.Zero;
        }

        if (_glFbo != 0 && _glDeleteFramebuffers != null)
        {
            _glDeleteFramebuffers(1, ref _glFbo);
            _glFbo = 0;
        }

        if (_glDepthRb != 0 && _glDeleteRenderbuffers != null)
        {
            _glDeleteRenderbuffers(1, ref _glDepthRb);
            _glDepthRb = 0;
        }

        if (_glTexture != 0)
        {
            glDeleteTextures(1, ref _glTexture);
            _glTexture = 0;
        }

        _d3dSurface?.Dispose();
        _d3dSurface = null;
        _d3dTexture?.Dispose();
        _d3dTexture = null;
        _d3d11Texture?.Dispose();
        _d3d11Texture = null;
    }

    /// <summary>
    /// Full cleanup of all shared surface resources including devices.
    /// </summary>
    private void CleanupSharedSurface()
    {
        try
        {
            DestroySharedTextureResources();

            if (_wglDxDevice != IntPtr.Zero && _wglDXCloseDeviceNV != null)
            {
                _wglDXCloseDeviceNV(_wglDxDevice);
                _wglDxDevice = IntPtr.Zero;
            }

            _d3dImage = null;
            _d3dDevice?.Dispose();
            _d3dDevice = null;
            _d3d9?.Dispose();
            _d3d9 = null;
            _d3d11Device?.Dispose();
            _d3d11Device = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            // Vortice assembly not loaded — nothing to clean up
        }
        _useSharedSurface = false;
    }

    /// <summary>
    /// Resizes the renderer to new dimensions without recreating the OpenGL
    /// context or reloading presets. Must be called on the UI thread.
    /// </summary>
    public bool Resize(int newWidth, int newHeight)
    {
        if (!_initialized || _disposed || _projectM == IntPtr.Zero)
            return false;
        if (newWidth < 1 || newHeight < 1)
            return false;
        if (newWidth == _width && newHeight == _height)
            return true;

        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _projectM == IntPtr.Zero)
                return false;

            _width = newWidth;
            _height = newHeight;

            wglMakeCurrent(_hdc, _hglrc);
            glViewport(0, 0, _width, _height);
            projectm_set_window_size(_projectM, (uint)_width, (uint)_height);

            if (_useSharedSurface)
            {
                try
                {
                    // Recreate shared texture resources at new size
                    DestroySharedTextureResources();
                    if (CreateSharedTextureResources() && _d3dSurface != null)
                    {
                        _d3dImage!.Lock();
                        _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3dSurface.NativePointer, true);
                        _d3dImage.Unlock();
                    }
                    else
                    {
                        // Fall back to readback path
                        Log("Shared surface resize failed — falling back to readback");
                        CleanupSharedSurface();
                        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException)
                {
                    Log($"Shared surface resize failed (missing dependency: {ex.Message}) — falling back to readback");
                    CleanupSharedSurface();
                    _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
                }
            }
            else
            {
                _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            }

            _pixelBuffer = new byte[_width * _height * 4];
            _flippedBuffer = new byte[_width * _height * 4];

            Log($"Resized to {_width}x{_height}");
            return true;
        }
    }

    /// <summary>
    /// Applies tuning parameters (duration, sensitivity, mesh, etc.) to the
    /// running projectM instance without recreating the OpenGL context or
    /// reloading presets. Must be called on the UI/render thread.
    /// </summary>
    public void ApplyTuningSettings()
    {
        if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
            projectm_set_preset_duration(_projectM, _isBrowsing ? 999999.0 : PresetDuration);
            projectm_set_soft_cut_duration(_projectM, SoftCutDuration);
            projectm_set_hard_cut_enabled(_projectM, _isBrowsing ? false : HardCutEnabled);
            projectm_set_beat_sensitivity(_projectM, BeatSensitivity);
            projectm_set_mesh_size(_projectM, MeshSize, MeshSize);
            Log($"Tuning applied in-place: duration={PresetDuration}s, softCut={SoftCutDuration}s, hardCut={HardCutEnabled}, beat={BeatSensitivity}, mesh={MeshSize}");
        }
    }

    /// <summary>
    /// Switches to a specific preset by index.
    /// </summary>
    public void SetPreset(uint index, bool hardCut = true)
    {
        if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
            if (index >= PresetCount) return;
            projectm_playlist_set_position(_playlist, index, hardCut);
            Log($"Manually switched to preset [{index}]");
        }
    }

    /// <summary>
    /// Removes the currently playing preset from the playlist so it won't be selected again.
    /// </summary>
    public void RemoveCurrentPresetFromPlaylist()
    {
        if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
            uint index = projectm_playlist_get_position(_playlist);
            if (index < projectm_playlist_size(_playlist))
            {
                projectm_playlist_remove_preset(_playlist, index);
                Log($"Removed preset at index [{index}] from playlist");
            }
        }
    }

    /// <summary>
    /// Advances to the next preset in the playlist.
    /// </summary>
    public void PlayNext(bool hardCut = true)
    {
        if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
            projectm_playlist_play_next(_playlist, hardCut);
            Log("Skipped to next preset");
        }
    }

    /// <summary>
    /// Gets the display name of a preset at the given index (relative to PresetPath).
    /// </summary>
    public string? GetPresetName(uint index)
    {
        if (_playlist == IntPtr.Zero || index >= PresetCount) return null;
        IntPtr namePtr = projectm_playlist_item(_playlist, index);
        if (namePtr == IntPtr.Zero) return null;
        string path = Marshal.PtrToStringUTF8(namePtr) ?? "";
        if (!string.IsNullOrEmpty(PresetPath) && path.StartsWith(PresetPath, StringComparison.OrdinalIgnoreCase))
            path = path[PresetPath.Length..].TrimStart('\\', '/');
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Returns all preset names for building a browser UI.
    /// </summary>
    public List<string> GetAllPresetNames()
    {
        var names = new List<string>();
        uint count = PresetCount;
        for (uint i = 0; i < count; i++)
            names.Add(GetPresetName(i) ?? $"Preset {i}");
        return names;
    }

    /// <summary>
    /// Locks/unlocks the current preset by disabling auto-advance (useful for browsing).
    /// </summary>
    public void LockPreset(bool locked)
    {
        if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
            _isBrowsing = locked;
            projectm_set_preset_duration(_projectM, locked ? 999999.0 : PresetDuration);
            projectm_set_hard_cut_enabled(_projectM, locked ? false : HardCutEnabled);
        }
    }

    /// <summary>
    /// Previews a single preset file by temporarily replacing the playlist contents.
    /// Call <see cref="LockPreset"/> with <c>true</c> before previewing to prevent auto-advance.
    /// </summary>
    public void PreviewPreset(string presetFilePath)
    {
        if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
        if (!System.IO.File.Exists(presetFilePath)) return;

        var directory = System.IO.Path.GetDirectoryName(presetFilePath);
        if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory)) return;

        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;

            // Add the containing directory (non-recursive) so the target file is in the playlist
            projectm_playlist_clear(_playlist);
            projectm_playlist_add_path(_playlist, directory, false, false);

            // Find the target file's index in the playlist
            uint count = projectm_playlist_size(_playlist);
            uint targetIndex = 0;
            for (uint i = 0; i < count; i++)
            {
                IntPtr namePtr = projectm_playlist_item(_playlist, i);
                if (namePtr == IntPtr.Zero) continue;
                string? itemPath = Marshal.PtrToStringUTF8(namePtr);
                if (itemPath != null && itemPath.Equals(presetFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }

            if (count > 0)
            {
                projectm_playlist_set_position(_playlist, targetIndex, true);
                Log($"Previewing preset: {System.IO.Path.GetFileNameWithoutExtension(presetFilePath)}");
            }
        }
    }

    /// <summary>
    /// Reloads the full preset playlist from <see cref="PresetPath"/>.
    /// Call this after closing the preset browser to restore normal operation.
    /// </summary>
    public void ReloadPlaylist()
    {
        if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(PresetPath) || !System.IO.Directory.Exists(PresetPath)) return;

        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _playlist == IntPtr.Zero) return;

            projectm_playlist_clear(_playlist);

            uint count;
            if (EnabledFolders != null && EnabledFolders.Count > 0)
            {
                count = 0;
                foreach (var folder in EnabledFolders)
                {
                    var fullPath = System.IO.Path.Combine(PresetPath, folder);
                    if (System.IO.Directory.Exists(fullPath))
                        count += projectm_playlist_add_path(_playlist, fullPath, true, false);
                }
            }
            else
            {
                // Load all subfolders except excluded folders
                count = 0;
                foreach (var dir in System.IO.Directory.GetDirectories(PresetPath))
                {
                    if (IsExcludedFolder(System.IO.Path.GetFileName(dir)))
                        continue;
                    count += projectm_playlist_add_path(_playlist, dir, true, false);
                }
                count += projectm_playlist_add_path(_playlist, PresetPath, false, false);
            }

            projectm_playlist_set_shuffle(_playlist, true);
            Log($"Playlist reloaded: {count} presets");

            if (count > 0)
                projectm_playlist_play_next(_playlist, false);
        }
    }

    /// <summary>
    /// Feed raw audio PCM data to projectM for visualization.
    /// </summary>
    public void AddPcmData(float[] samples, uint channels)
    {
        if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_initialized || _disposed || _projectM == IntPtr.Zero) return;
            try
            {
                projectm_pcm_add_float(_projectM, samples, (uint)(samples.Length / channels), channels);
            }
            catch (Exception ex)
            {
                Log($"AddPcmData exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Render one frame. When using the shared GPU surface path, projectM renders
    /// directly into a D3D9-shared FBO — no pixel readback is needed for display.
    /// Readback via glReadPixels is only performed when color/black-frame analysis
    /// is pending (infrequent, timer-driven).
    /// Must be called on the UI thread.
    /// </summary>
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    public void RenderFrame()
    {
        if (!_initialized || _disposed || _projectM == IntPtr.Zero)
            return;

        if (_useSharedSurface)
        {
            if (_d3dImage == null) return;
        }
        else
        {
            if (_bitmap == null || _pixelBuffer == null || _flippedBuffer == null) return;
        }

        // If another native call (e.g. PreviewPreset) is in progress, skip this frame
        // rather than blocking the UI thread or risking concurrent native access.
        if (!Monitor.TryEnter(_nativeLock))
            return;

        try
        {
            // Re-check after acquiring lock — state may have changed
            if (!_initialized || _disposed || _projectM == IntPtr.Zero)
                return;

            // Ensure our GL context is current
            if (!wglMakeCurrent(_hdc, _hglrc))
            {
                Log($"wglMakeCurrent failed (error {Marshal.GetLastWin32Error()}) — disabling renderer");
                _initialized = false;
                return;
            }

            if (_useSharedSurface)
                RenderFrameSharedSurface();
            else
                RenderFrameFallback();
        }
        catch (Exception ex)
        {
            Log($"RenderFrame exception: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_nativeLock);
        }
    }

    private int _lockFailCount;
    private int _frameCount;

    /// <summary>
    /// Zero-copy render path: renders into the D3D9-shared FBO, then marks
    /// the D3DImage dirty. Only does glReadPixels when analysis is needed.
    /// </summary>
    private void RenderFrameSharedSurface()
    {
        // Lock the shared texture for OpenGL access
        if (!_wglDXLockObjectsNV!(_wglDxDevice, 1, [_wglDxObject]))
        {
            _lockFailCount++;
            if (_lockFailCount <= 5 || (_lockFailCount % 100) == 0)
            {
                uint glErr = glGetError();
                Log($"wglDXLockObjectsNV failed (count: {_lockFailCount}, glError: 0x{glErr:X}, Win32: {Marshal.GetLastWin32Error()})");
            }
            return;
        }

        try
        {
            // Bind our FBO so projectM renders into the shared D3D texture
            _glBindFramebuffer!(GL_FRAMEBUFFER, _glFbo);
            glViewport(0, 0, _width, _height);

            try
            {
                projectm_opengl_render_frame(_projectM);
            }
            catch (AccessViolationException ex)
            {
                Log($"FATAL: AccessViolationException in projectm_opengl_render_frame: {ex.Message}");
                _initialized = false;
                return;
            }

            // projectM renders with alpha=0, which causes WPF D3DImage to display
            // the texture as fully transparent (appears black). Force alpha to 1
            // by clearing only the alpha channel — RGB is preserved via colorMask.
            glColorMask(0, 0, 0, 1);
            glClearColor(0f, 0f, 0f, 1f);
            glClear(GL_COLOR_BUFFER_BIT);
            glColorMask(1, 1, 1, 1);

            _frameCount++;
            if (_frameCount <= 3)
            {
                uint glErr = glGetError();
                if (glErr != 0)
                    Log($"GL error after render frame #{_frameCount}: 0x{glErr:X}");
                else
                    Log($"Shared surface frame #{_frameCount} rendered OK");
            }

            // Determine if we need pixel readback for analysis this frame
            bool needReadback = (_colorSampleTargetTick >= 0 && _colorSampleWatch.ElapsedTicks >= _colorSampleTargetTick)
                || (_blackCheckTargetTick >= 0 && _colorSampleWatch.ElapsedTicks >= _blackCheckTargetTick);

            if (needReadback && _pixelBuffer != null && _flippedBuffer != null)
            {
                var handle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
                try
                {
                    glReadPixels(0, 0, _width, _height, GL_BGRA, GL_UNSIGNED_BYTE,
                        handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }

                // Flip for analysis snapshots
                int stride = _width * 4;
                for (int y = 0; y < _height; y++)
                    Buffer.BlockCopy(_pixelBuffer, (_height - 1 - y) * stride, _flippedBuffer, y * stride, stride);

                PerformColorAndBlackAnalysis();
            }

            // Unbind FBO
            _glBindFramebuffer!(GL_FRAMEBUFFER, 0);
        }
        finally
        {
            // Flush GL commands so the shared texture content is visible to D3D
            glFlush();

            // Unlock — hand back to D3D
            _wglDXUnlockObjectsNV!(_wglDxDevice, 1, [_wglDxObject]);
        }

        // GPU-side synchronization: D3D11 flush ensures all writes are committed
        // before WPF/D3D9 reads from the shared surface.
        _d3d11Device?.ImmediateContext.Flush();

        // Update D3DImage — the D3D9 surface is backed by the same DXGI shared memory
        // that the D3D11 texture writes to, so no copy is needed.
        try
        {
            if (!_d3dImage!.IsFrontBufferAvailable)
            {
                if (_frameCount <= 5 || (_frameCount % 300) == 0)
                    Log($"D3DImage.IsFrontBufferAvailable=false at frame {_frameCount} — display may be black");
                return;
            }
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3dSurface!.NativePointer, true);
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            _d3dImage.Unlock();
            if (_frameCount <= 3)
                Log($"  D3DImage updated (frame #{_frameCount}, frontBuffer={_d3dImage.IsFrontBufferAvailable}, pixelW={_d3dImage.PixelWidth}, pixelH={_d3dImage.PixelHeight})");
        }
        catch
        {
            try { _d3dImage!.Unlock(); } catch { }
        }
    }

    /// <summary>
    /// Fallback render path: renders to the default framebuffer, reads back
    /// all pixels via glReadPixels, and copies to a WriteableBitmap.
    /// </summary>
    private void RenderFrameFallback()
    {
        // Render — guard against AccessViolationException from corrupt native state
        try
        {
            projectm_opengl_render_frame(_projectM);
        }
        catch (AccessViolationException ex)
        {
            Log($"FATAL: AccessViolationException in projectm_opengl_render_frame: {ex.Message}");
            _initialized = false;
            return;
        }

        // Read pixels from the GL framebuffer
        var handle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
        try
        {
            glReadPixels(0, 0, _width, _height, GL_BGRA, GL_UNSIGNED_BYTE,
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        PerformColorAndBlackAnalysis();

        // OpenGL origin is bottom-left; WPF is top-left — flip vertically
        int stride = _width * 4;
        for (int y = 0; y < _height; y++)
        {
            Buffer.BlockCopy(_pixelBuffer!, (_height - 1 - y) * stride,
                _flippedBuffer!, y * stride, stride);
        }

        // Copy to WriteableBitmap
        try
        {
            _bitmap!.Lock();
            Marshal.Copy(_flippedBuffer!, 0, _bitmap.BackBuffer, _flippedBuffer!.Length);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            _bitmap.Unlock();
        }
        catch
        {
            try { _bitmap!.Unlock(); } catch { }
        }
    }

    /// <summary>
    /// Runs the color sampling and black-frame detection analysis on the current
    /// pixel buffer contents. Called from both render paths when readback is available.
    /// </summary>
    private void PerformColorAndBlackAnalysis()
    {
        // Sample dominant color after preset switch delay
        ColorAnalysis? colorAnalysis = null;
        if (_colorSampleTargetTick >= 0 && _colorSampleWatch.ElapsedTicks >= _colorSampleTargetTick)
        {
            _colorSampleTargetTick = -1;
            try
            {
                colorAnalysis = FrameColorAnalyzer.GetDominantColorBand(_pixelBuffer!, _width, _height, isBgra: true);
                Log($"Dominant color band: {colorAnalysis.Value.Color} (brightness: {colorAnalysis.Value.Brightness:F3}, luminance: {colorAnalysis.Value.TopAvgLuminance:F2})");
                ColorBandChanged?.Invoke(colorAnalysis.Value);

                if (SaveColorSampleFrame)
                {
                    byte[] snapshot = _flippedBuffer!.ToArray();
                    int w = _width, h = _height;
                    string colorName = colorAnalysis.Value.Color.ToString();
                    string presetRelative = _currentPresetFullPath ?? "unknown";
                    if (!string.IsNullOrEmpty(PresetPath) && presetRelative.StartsWith(PresetPath, StringComparison.OrdinalIgnoreCase))
                        presetRelative = presetRelative[PresetPath.Length..].TrimStart('\\', '/');
                    presetRelative = presetRelative.Replace('\\', '/').Replace('/', '_');
                    string fileName = $"{colorName}-{presetRelative}";
                    Task.Run(() => SaveFrameSnapshot(snapshot, w, h, fileName, "ColorSamples"));
                }
            }
            catch (Exception ex)
            {
                Log($"Color sampling failed: {ex.Message}");
            }
        }

        // Black-frame monitor
        if (_blackCheckTargetTick >= 0 && _colorSampleWatch.ElapsedTicks >= _blackCheckTargetTick)
        {
            _blackCheckTargetTick = -1;
            try
            {
                bool isBlack;
                double luminance;
                if (_blackCheckHitCount == 0 && colorAnalysis.HasValue)
                {
                    luminance = colorAnalysis.Value.TopAvgLuminance;
                    isBlack = luminance < BlackCheckLuminanceThreshold;
                }
                else
                {
                    isBlack = IsFrameBlack(_pixelBuffer!, out luminance);
                }

                if (isBlack)
                {
                    _blackCheckHitCount++;
                    if (_blackCheckHitCount >= BlackCheckRequiredHits)
                    {
                        var path = _currentPresetFullPath;
                        var relativeName = path ?? "unknown";
                        if (!string.IsNullOrEmpty(PresetPath) && relativeName.StartsWith(PresetPath, StringComparison.OrdinalIgnoreCase))
                            relativeName = relativeName[PresetPath.Length..].TrimStart('\\', '/');
                        relativeName = relativeName.Replace('\\', '/');

                        Log($"Black frame CONFIRMED ({_blackCheckHitCount}/{BlackCheckRequiredHits}) — preset: {relativeName} (luminance: {luminance:F2})");
                        ProjectMPresetMonitorLog.Add(relativeName, PresetMonitorMode >= 2 ? "black_moved" : "black_logged", luminance);

                        if (SaveBlackFrame)
                        {
                            byte[] snapshot = _flippedBuffer!.ToArray();
                            int w = _width, h = _height;
                            string name = relativeName;
                            Task.Run(() => SaveFrameSnapshot(snapshot, w, h, name, "BlackFrames"));
                        }

                        if (path != null)
                            BlackPresetDetected?.Invoke(path);
                    }
                    else
                    {
                        Log($"Black frame detected ({_blackCheckHitCount}/{BlackCheckRequiredHits}) — rechecking in {BlackCheckIntervalSeconds}s");
                        _blackCheckTargetTick = _colorSampleWatch.ElapsedTicks
                            + (long)(BlackCheckIntervalSeconds * System.Diagnostics.Stopwatch.Frequency);
                    }
                }
                else
                {
                    if (_blackCheckHitCount > 0)
                        Log($"Black frame streak broken ({_blackCheckHitCount}/{BlackCheckRequiredHits}) — preset is OK");
                    _blackCheckHitCount = 0;
                }
            }
            catch (Exception ex)
            {
                Log($"Black-frame check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Saves a PNG snapshot of a frame for diagnostics.
    /// Called on a background thread with a snapshot of the flipped BGRA pixel data.
    /// </summary>
    private static void SaveFrameSnapshot(byte[] pixels, int width, int height, string fileName, string subFolder)
    {
        try
        {
            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bmp.Lock();
            Marshal.Copy(pixels, 0, bmp.BackBuffer, pixels.Length);
            bmp.AddDirtyRect(new Int32Rect(0, 0, width, height));
            bmp.Unlock();
            bmp.Freeze();

            string safeName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", subFolder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{safeName}.png");

            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);

            Log($"Frame snapshot saved: {path}");
        }
        catch (Exception ex)
        {
            Log($"Failed to save frame snapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Analyzes the provided pixel buffer and returns true if the frame is
    /// effectively all black. Uses the average luminance of the brightest 5%
    /// of sampled pixels so that small visuals surrounded by black are not
    /// falsely flagged.
    /// The buffer must contain BGRA pixel data for the current frame dimensions.
    /// </summary>
    private bool IsFrameBlack(byte[] pixels, out double topAvgLuminance, int sampleStep = 4)
    {
        int sampleCount = ((_height - 1) / sampleStep + 1) * ((_width - 1) / sampleStep + 1);
        if (sampleCount == 0) { topAvgLuminance = 0; return true; }

        // Counting sort via 256 buckets — O(n) and avoids allocating a luminance array
        Span<int> counts = stackalloc int[256];

        for (int y = 0; y < _height; y += sampleStep)
        {
            int rowOffset = y * _width * 4;
            for (int x = 0; x < _width; x += sampleStep)
            {
                int i = rowOffset + x * 4;
                // BGRA order: B=[i], G=[i+1], R=[i+2]
                int lum = (pixels[i + 2] * 2 + pixels[i + 1] * 3 + pixels[i]) / 6;
                counts[lum]++;
            }
        }

        // Average the top percentile (brightest pixels) using the bucket counts
        int topCount = Math.Max(1, (int)(sampleCount * BlackCheckPercentile / 100.0));
        long topSum = 0;
        int remaining = topCount;
        for (int b = 255; b >= 0 && remaining > 0; b--)
        {
            int take = Math.Min(counts[b], remaining);
            topSum += (long)b * take;
            remaining -= take;
        }

        topAvgLuminance = (double)topSum / topCount;
        return topAvgLuminance < BlackCheckLuminanceThreshold;
    }

    /// <summary>
    /// Returns true if the given top-level folder name should be excluded from the playlist.
    /// </summary>
    private static bool IsExcludedFolder(string folderName)
    {
        return folderName.Equals("Deactivated", StringComparison.OrdinalIgnoreCase)
            || folderName.Equals("Transition", StringComparison.OrdinalIgnoreCase)
            || folderName.Equals("! Transition", StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string message)
    {
        var msg = $"[ProjectM] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        DebugLog.Log("ProjectM", message);
    }

    private void OnPresetSwitched(bool isHardCut, uint index, IntPtr userData)
    {
        try
        {
            string presetName = "unknown";
            string? fullPath = null;
            if (_playlist != IntPtr.Zero)
            {
                IntPtr namePtr = projectm_playlist_item(_playlist, index);
                if (namePtr != IntPtr.Zero)
                {
                    fullPath = Marshal.PtrToStringUTF8(namePtr);
                    presetName = fullPath ?? "unknown";
                    // Make path relative to the preset root for readable logging
                    if (!string.IsNullOrEmpty(PresetPath) && presetName.StartsWith(PresetPath, StringComparison.OrdinalIgnoreCase))
                        presetName = presetName[PresetPath.Length..].TrimStart('\\', '/');
                    presetName = presetName.Replace('\\', '/');
                }
            }

            _currentPresetFullPath = fullPath;

            var cutType = isHardCut ? "hard cut" : "soft cut";
            Log($"Preset switched ({cutType}) → [{index}] {presetName}");

            // Log automatic transitions (not manual preview) to the history file
            if (!_isBrowsing)
            {
                ProjectMPresetLog.Add(presetName, cutType);
                double delaySec = ColorSampleDelaySeconds
                    + (isHardCut ? 0.0 : SoftCutDuration);
                _colorSampleTargetTick = _colorSampleWatch.ElapsedTicks
                    + (long)(delaySec * System.Diagnostics.Stopwatch.Frequency);

                // Schedule black-frame check if monitor is enabled
                if (PresetMonitorMode > 0)
                {
                    _blackCheckHitCount = 0;
                    _blackCheckTargetTick = _colorSampleWatch.ElapsedTicks
                        + (long)(delaySec * System.Diagnostics.Stopwatch.Frequency);
                }
            }
        }
        catch
        {
            // Callback must not throw into native code
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialized = false;

        lock (_nativeLock)
        {
            try
            {
                if (_playlist != IntPtr.Zero)
                {
                    try { projectm_playlist_destroy(_playlist); } catch { }
                    _playlist = IntPtr.Zero;
                }

                if (_projectM != IntPtr.Zero)
                {
                    try { projectm_destroy(_projectM); } catch { }
                    _projectM = IntPtr.Zero;
                }

                // Clean up shared surface resources before destroying GL context
                CleanupSharedSurface();

                if (_hglrc != IntPtr.Zero)
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(_hglrc);
                    _hglrc = IntPtr.Zero;
                }

                if (_hdc != IntPtr.Zero && _hwnd != IntPtr.Zero)
                {
                    ReleaseDC(_hwnd, _hdc);
                    _hdc = IntPtr.Zero;
                }

                if (_hwnd != IntPtr.Zero)
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log($"Dispose exception: {ex.Message}");
            }
        }

        _bitmap = null;
        _d3dImage = null;
        _pixelBuffer = null;
        _flippedBuffer = null;
    }
}
