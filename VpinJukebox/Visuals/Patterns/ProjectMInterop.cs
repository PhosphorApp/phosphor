using System.Runtime.InteropServices;

namespace VpinJukebox;

/// <summary>
/// P/Invoke declarations for libprojectM-4, its playlist companion library,
/// and the Win32/WGL functions needed to create a hidden OpenGL rendering context.
/// </summary>
internal static class ProjectMInterop
{
    // ── Win32 / WGL ──────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    [DllImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern IntPtr wglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string proc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WglCreateContextAttribsARBDelegate(IntPtr hDC, IntPtr hShareContext, int[] attribList);

    // WGL_CONTEXT_* constants for wglCreateContextAttribsARB
    public const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    public const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    [DllImport("opengl32.dll")]
    public static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    public static extern void glReadPixels(int x, int y, int width, int height,
        uint format, uint type, IntPtr data);

    [DllImport("opengl32.dll")]
    public static extern uint glGetError();

    // ── GLEW ─────────────────────────────────────────────────

    [DllImport("glew32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint glewInit();

    public const uint GLEW_OK = 0;

    // GL constants
    public const uint GL_BGRA = 0x80E1;
    public const uint GL_RGBA = 0x1908;
    public const uint GL_UNSIGNED_BYTE = 0x1401;

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift,
            cGreenBits, cGreenShift, cBlueBits, cBlueShift,
            cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits,
            cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType,
            bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    public const uint PFD_SUPPORT_OPENGL = 0x00000020;
    public const uint PFD_DOUBLEBUFFER = 0x00000001;

    // ── libprojectM C API ────────────────────────────────────────

    private const string ProjectMLib = "projectM-4";

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_create();

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_destroy(IntPtr instance);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_window_size(IntPtr instance, uint width, uint height);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_opengl_render_frame(IntPtr instance);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_pcm_add_float(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPArray)] float[] samples,
        uint count,
        uint channels);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_preset_duration(IntPtr instance, double seconds);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_soft_cut_duration(IntPtr instance, double seconds);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_hard_cut_enabled(IntPtr instance,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_mesh_size(IntPtr instance, uint width, uint height);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_beat_sensitivity(IntPtr instance, float sensitivity);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float projectm_get_beat_sensitivity(IntPtr instance);

    [DllImport(ProjectMLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "projectm_set_texture_search_paths")]
    private static extern void projectm_set_texture_search_paths_native(
        IntPtr instance,
        IntPtr[] paths,
        uint count);

    /// <summary>
    /// Wrapper that manually marshals string[] to UTF-8 IntPtr[] for the native call,
    /// since LPArray+LPUTF8Str is not a supported marshalling combination.
    /// </summary>
    public static void projectm_set_texture_search_paths(IntPtr instance, string[] paths, uint count)
    {
        var ptrs = new IntPtr[paths.Length];
        try
        {
            for (int i = 0; i < paths.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(paths[i]);
            projectm_set_texture_search_paths_native(instance, ptrs, count);
        }
        finally
        {
            foreach (var ptr in ptrs)
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ── Playlist API (projectM-4-playlist.dll) ───────────────────

    private const string PlaylistLib = "projectM-4-playlist";

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_playlist_create(IntPtr projectmInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_destroy(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_add_path(IntPtr playlistInstance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.Bool)] bool recurse,
        [MarshalAs(UnmanagedType.Bool)] bool allowDuplicates);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_shuffle(IntPtr playlistInstance,
        [MarshalAs(UnmanagedType.Bool)] bool shuffle);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_play_next(IntPtr playlistInstance,
        [MarshalAs(UnmanagedType.Bool)] bool hardCut);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_size(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_preset_switched_event_callback(
        IntPtr playlistInstance, ProjectMPresetSwitchedCallback? callback, IntPtr userData);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool projectm_playlist_get_preset_switched(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint projectm_playlist_get_position(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr projectm_playlist_item(IntPtr playlistInstance, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ProjectMPresetSwitchedCallback(
        [MarshalAs(UnmanagedType.Bool)] bool isHardCut, uint index, IntPtr userData);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_clear(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_position(IntPtr playlistInstance,
        uint index, [MarshalAs(UnmanagedType.Bool)] bool hardCut);
}
