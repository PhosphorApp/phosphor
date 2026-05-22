using System.Runtime.InteropServices;

namespace Phosphor;

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

    [DllImport("opengl32.dll")]
    public static extern void glFlush();

    [DllImport("opengl32.dll")]
    public static extern void glFinish();

    [DllImport("opengl32.dll")]
    public static extern void glClearColor(float r, float g, float b, float a);

    [DllImport("opengl32.dll")]
    public static extern void glClear(uint mask);

    [DllImport("opengl32.dll")]
    public static extern void glColorMask(byte r, byte g, byte b, byte a);

    // ── GLEW ─────────────────────────────────────────────────

    [DllImport("glew32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint glewInit();

    public const uint GLEW_OK = 0;

    // GL constants
    public const uint GL_BGRA = 0x80E1;
    public const uint GL_RGBA = 0x1908;
    public const int GL_RGBA8 = 0x8058;
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
    public static extern bool projectm_playlist_remove_preset(IntPtr playlistInstance, uint index);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_clear(IntPtr playlistInstance);

    [DllImport(PlaylistLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_position(IntPtr playlistInstance,
        uint index, [MarshalAs(UnmanagedType.Bool)] bool hardCut);

    // ── OpenGL FBO / texture functions (resolved via GLEW after glewInit) ────

    [DllImport("opengl32.dll")]
    public static extern void glGenTextures(int n, out uint textures);

    [DllImport("opengl32.dll")]
    public static extern void glDeleteTextures(int n, ref uint textures);

    [DllImport("opengl32.dll")]
    public static extern void glBindTexture(uint target, uint texture);

    [DllImport("opengl32.dll")]
    public static extern void glTexImage2D(uint target, int level, int internalformat,
        int width, int height, int border, uint format, uint type, IntPtr data);

    public const uint GL_TEXTURE_2D = 0x0DE1;
    public const uint GL_FRAMEBUFFER = 0x8D40;
    public const uint GL_READ_FRAMEBUFFER = 0x8CA8;
    public const uint GL_DRAW_FRAMEBUFFER = 0x8CA9;
    public const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    public const uint GL_NEAREST = 0x2600;
    public const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    public const uint GL_RENDERBUFFER = 0x8D41;
    public const uint GL_DEPTH24_STENCIL8 = 0x88F0;
    public const uint GL_DEPTH_STENCIL_ATTACHMENT = 0x821A;
    public const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    // GL extension function pointer delegates (resolved at runtime via wglGetProcAddress)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlGenFramebuffersDelegate(int n, out uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlDeleteFramebuffersDelegate(int n, ref uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlBindFramebufferDelegate(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlFramebufferTexture2DDelegate(uint target, uint attachment, uint textarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlGenRenderbuffersDelegate(int n, out uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlDeleteRenderbuffersDelegate(int n, ref uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlBindRenderbufferDelegate(uint target, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlRenderbufferStorageDelegate(uint target, uint internalformat, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlFramebufferRenderbufferDelegate(uint target, uint attachment, uint renderbuffertarget, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint GlCheckFramebufferStatusDelegate(uint target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlBlitFramebufferDelegate(int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter);

    // ── PBO (Pixel Buffer Object) ────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlGenBuffersDelegate(int n, out uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlDeleteBuffersDelegate(int n, ref uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlBindBufferDelegate(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void GlBufferDataDelegate(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr GlMapBufferDelegate(uint target, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool GlUnmapBufferDelegate(uint target);

    public const uint GL_PIXEL_PACK_BUFFER = 0x88EB;
    public const uint GL_STREAM_READ = 0x88E1;
    public const uint GL_READ_ONLY = 0x88B8;

    // ── WGL_NV_DX_interop ────────────────────────────────────────
    // Despite the "NV" name, these are widely supported on AMD and Intel GPUs too.

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WglDXOpenDeviceNVDelegate(IntPtr dxDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool WglDXCloseDeviceNVDelegate(IntPtr hDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WglDXRegisterObjectNVDelegate(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool WglDXUnregisterObjectNVDelegate(IntPtr hDevice, IntPtr hObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool WglDXLockObjectsNVDelegate(IntPtr hDevice, int count, IntPtr[] hObjects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool WglDXUnlockObjectsNVDelegate(IntPtr hDevice, int count, IntPtr[] hObjects);

    public const uint WGL_ACCESS_READ_ONLY_NV = 0x0000;
    public const uint WGL_ACCESS_READ_WRITE_NV = 0x0001;
    public const uint WGL_ACCESS_WRITE_DISCARD_NV = 0x0002;

    /// <summary>
    /// Attempts to resolve a WGL extension function pointer. Returns null if not available.
    /// </summary>
    public static T? GetWglProc<T>(string name) where T : Delegate
    {
        IntPtr ptr = wglGetProcAddress(name);
        if (ptr == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>
    /// Resolves an OpenGL extension function pointer. Returns null if not available.
    /// </summary>
    public static T? GetGlProc<T>(string name) where T : Delegate
    {
        IntPtr ptr = wglGetProcAddress(name);
        if (ptr == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
