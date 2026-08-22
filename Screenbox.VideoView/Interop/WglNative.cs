// Screenbox.VideoView — WGL_NV_DX_interop 后端所需的 Win32/GDI/WGL/OpenGL 最小 P/Invoke 集。
// 约定（AOT + DisableRuntimeMarshalling）：
// - Win32/GDI32/OpenGL32 一律 LibraryImport，仅 blittable 参数，字符串以固定 char*/byte* 传递；
// - WGL 扩展函数与 GL ≥1.2 函数经 wglGetProcAddress 解析为 unmanaged[Stdcall] 函数指针
//   （WGL/GL 全部为 WINAPI=Stdcall 调用约定，x86 下栈平衡依赖该约定，不可写错）。

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Screenbox.Controls.Interop;

internal static unsafe partial class WglNative
{
    // ---- GL 常量 ----
    internal const uint GlRenderbuffer = 0x8D41;
    internal const uint GlFramebuffer = 0x8D40;
    internal const uint GlColorAttachment0 = 0x8CE0;
    internal const uint GlFramebufferComplete = 0x8CD5;

    // ---- WGL_NV_DX_interop 常量 ----
    internal const uint WglAccessReadWriteNv = 0x0001;

    // ---- PIXELFORMATDESCRIPTOR 标志 ----
    private const uint PfdDoublebuffer = 0x00000001;
    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpengl = 0x00000020;

    /// <summary>HWND_MESSAGE = (HWND)-3，message-only 窗口父句柄。</summary>
    internal static readonly nint HwndMessage = new(-3);

    private static nint s_opengl32Module;

    /// <summary>PIXELFORMATDESCRIPTOR（gdi32，Sequential 自然对齐即 40 字节，与 wingdi.h 一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelFormatDescriptor
    {
        public ushort Size;       // 0
        public ushort Version;    // 2
        public uint Flags;        // 4
        public byte PixelType;    // 8  (PFD_TYPE_RGBA = 0)
        public byte ColorBits;    // 9
        public byte RedBits;      // 10
        public byte RedShift;     // 11
        public byte GreenBits;    // 12
        public byte GreenShift;   // 13
        public byte BlueBits;     // 14
        public byte BlueShift;    // 15
        public byte AlphaBits;    // 16
        public byte AlphaShift;   // 17
        public byte AccumBits;    // 18
        public byte AccumRedBits; // 19
        public byte AccumGreenBits; // 20
        public byte AccumBlueBits;  // 21
        public byte AccumAlphaBits; // 22
        public byte DepthBits;    // 23
        public byte StencilBits;  // 24
        public byte AuxBuffers;   // 25
        public byte LayerType;    // 26 (PFD_MAIN_PLANE = 0)
        public byte Reserved;     // 27
        public uint LayerMask;    // 28
        public uint VisibleMask;  // 32
        public uint DamageMask;   // 36
        // 共 40 字节

        internal static PixelFormatDescriptor CreateDefault() => new()
        {
            Size = 40,
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpengl | PfdDoublebuffer,
            PixelType = 0,
            ColorBits = 32,
            DepthBits = 24,
            StencilBits = 8,
            LayerType = 0,
        };
    }

    /// <summary>WNDCLASSEXW。lpfnWndProc 为 Stdcall 函数指针字段（UCO 静态方法地址）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassExW
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint IconSm;
    }

    // ---- kernel32 ----

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetModuleHandleW(char* lpModuleName);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetProcAddress(nint hModule, byte* lpProcName);

    // ---- user32 ----

    [LibraryImport("user32.dll")]
    internal static partial ushort RegisterClassExW(WndClassExW* lpwcx);

    [LibraryImport("user32.dll")]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, void* lpParam);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint hWnd, nint hDC);

    // ---- gdi32 ----

    [LibraryImport("gdi32.dll")]
    internal static partial int ChoosePixelFormat(nint hdc, PixelFormatDescriptor* ppfd);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetPixelFormat(nint hdc, int format, PixelFormatDescriptor* ppfd);

    // ---- opengl32（仅 1.1 导出与 WGL 入口，其余经 wglGetProcAddress） ----

    [LibraryImport("opengl32.dll")]
    internal static partial nint wglCreateContext(nint hdc);

    [LibraryImport("opengl32.dll")]
    internal static partial int wglMakeCurrent(nint hdc, nint hglrc);

    [LibraryImport("opengl32.dll")]
    internal static partial int wglDeleteContext(nint hglrc);

    [LibraryImport("opengl32.dll")]
    internal static partial nint wglGetProcAddress(byte* lpszProc);

    /// <summary>
    /// mpv 的 get_proc_address 解析器：先 wglGetProcAddress，失败回退 opengl32.dll 的
    /// GetProcAddress（GL 1.1 函数 wglGetProcAddress 解析不到）。函数名均为 ASCII。
    /// </summary>
    internal static nint ResolveGlProc(string name)
    {
        Span<byte> buffer = stackalloc byte[128];
        int length = WriteAscii(buffer, name);
        if (length < 0)
            return 0;

        fixed (byte* pName = buffer)
        {
            nint address = wglGetProcAddress(pName);
            // MSDN：wglGetProcAddress 失败时可能返回 NULL 或 1/2/3/-1。
            if (address is 0 or 1 or 2 or 3 or -1)
            {
                nint module = GetOpenGl32Module();
                address = module != 0 ? GetProcAddress(module, pName) : 0;
            }

            return address;
        }
    }

    /// <summary>把 ASCII 名称写入缓冲区并 NUL 终止；超长返回 -1。</summary>
    private static int WriteAscii(Span<byte> buffer, string name)
    {
        if (name.Length + 1 > buffer.Length)
            return -1;
        for (int i = 0; i < name.Length; i++)
            buffer[i] = (byte)name[i]; // GL/WGL 函数名纯 ASCII
        buffer[name.Length] = 0;
        return name.Length;
    }

    private static nint GetOpenGl32Module()
    {
        if (s_opengl32Module == 0)
        {
            const string moduleName = "opengl32.dll";
            fixed (char* pName = moduleName)
                s_opengl32Module = GetModuleHandleW(pName); // 进程已加载，取模块句柄不增引用
        }

        return s_opengl32Module;
    }

    /// <summary>message-only 窗口的 WndProc：一律转 DefWindowProcW。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static nint WindowProc(nint hWnd, uint msg, nuint wParam, nint lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);
}

/// <summary>经 wglGetProcAddress 解析出的 WGL_NV_DX_interop / GL FBO 函数指针表（全部 Stdcall）。</summary>
internal unsafe struct WglProcs
{
    // wglGetExtensionsStringARB(HDC) → const char*
    internal delegate* unmanaged[Stdcall]<nint, byte*> GetExtensionsStringARB;

    // HANDLE wglDXOpenDeviceNV(void* dxDevice)
    internal delegate* unmanaged[Stdcall]<void*, nint> DXOpenDeviceNV;

    // BOOL wglDXCloseDeviceNV(HANDLE hDevice)
    internal delegate* unmanaged[Stdcall]<nint, int> DXCloseDeviceNV;

    // HANDLE wglDXRegisterObjectNV(HANDLE hDevice, void* dxObject, GLuint name, GLenum type, GLenum access)
    internal delegate* unmanaged[Stdcall]<nint, void*, uint, uint, uint, nint> DXRegisterObjectNV;

    // BOOL wglDXUnregisterObjectNV(HANDLE hDevice, HANDLE hObject)
    internal delegate* unmanaged[Stdcall]<nint, nint, int> DXUnregisterObjectNV;

    // BOOL wglDXLockObjectsNV(HANDLE hDevice, GLint count, HANDLE* hObjects)
    internal delegate* unmanaged[Stdcall]<nint, int, nint*, int> DXLockObjectsNV;

    // BOOL wglDXUnlockObjectsNV(HANDLE hDevice, GLint count, HANDLE* hObjects)
    internal delegate* unmanaged[Stdcall]<nint, int, nint*, int> DXUnlockObjectsNV;

    // void glGenFramebuffers(GLsizei n, GLuint* ids)
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GlGenFramebuffers;

    // void glDeleteFramebuffers(GLsizei n, const GLuint* ids)
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GlDeleteFramebuffers;

    // void glBindFramebuffer(GLenum target, GLuint framebuffer)
    internal delegate* unmanaged[Stdcall]<uint, uint, void> GlBindFramebuffer;

    // void glFramebufferRenderbuffer(GLenum target, GLenum attachment, GLenum renderbuffertarget, GLuint renderbuffer)
    internal delegate* unmanaged[Stdcall]<uint, uint, uint, uint, void> GlFramebufferRenderbuffer;

    // GLenum glCheckFramebufferStatus(GLenum target)
    internal delegate* unmanaged[Stdcall]<uint, uint> GlCheckFramebufferStatus;

    // void glGenRenderbuffers(GLsizei n, GLuint* ids)
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GlGenRenderbuffers;

    // void glDeleteRenderbuffers(GLsizei n, const GLuint* ids)
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GlDeleteRenderbuffers;

    /// <summary>解析全部函数指针，任一缺失返回 false。</summary>
    internal static bool TryLoad(out WglProcs procs)
    {
        procs = new WglProcs
        {
            GetExtensionsStringARB = (delegate* unmanaged[Stdcall]<nint, byte*>)WglNative.ResolveGlProc("wglGetExtensionsStringARB"),
            DXOpenDeviceNV = (delegate* unmanaged[Stdcall]<void*, nint>)WglNative.ResolveGlProc("wglDXOpenDeviceNV"),
            DXCloseDeviceNV = (delegate* unmanaged[Stdcall]<nint, int>)WglNative.ResolveGlProc("wglDXCloseDeviceNV"),
            DXRegisterObjectNV = (delegate* unmanaged[Stdcall]<nint, void*, uint, uint, uint, nint>)WglNative.ResolveGlProc("wglDXRegisterObjectNV"),
            DXUnregisterObjectNV = (delegate* unmanaged[Stdcall]<nint, nint, int>)WglNative.ResolveGlProc("wglDXUnregisterObjectNV"),
            DXLockObjectsNV = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WglNative.ResolveGlProc("wglDXLockObjectsNV"),
            DXUnlockObjectsNV = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WglNative.ResolveGlProc("wglDXUnlockObjectsNV"),
            GlGenFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)WglNative.ResolveGlProc("glGenFramebuffers"),
            GlDeleteFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)WglNative.ResolveGlProc("glDeleteFramebuffers"),
            GlBindFramebuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)WglNative.ResolveGlProc("glBindFramebuffer"),
            GlFramebufferRenderbuffer = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, void>)WglNative.ResolveGlProc("glFramebufferRenderbuffer"),
            GlCheckFramebufferStatus = (delegate* unmanaged[Stdcall]<uint, uint>)WglNative.ResolveGlProc("glCheckFramebufferStatus"),
            GlGenRenderbuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)WglNative.ResolveGlProc("glGenRenderbuffers"),
            GlDeleteRenderbuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)WglNative.ResolveGlProc("glDeleteRenderbuffers"),
        };

        return procs.DXOpenDeviceNV != null
            && procs.DXCloseDeviceNV != null
            && procs.DXRegisterObjectNV != null
            && procs.DXUnregisterObjectNV != null
            && procs.DXLockObjectsNV != null
            && procs.DXUnlockObjectsNV != null
            && procs.GlGenFramebuffers != null
            && procs.GlDeleteFramebuffers != null
            && procs.GlBindFramebuffer != null
            && procs.GlFramebufferRenderbuffer != null
            && procs.GlCheckFramebufferStatus != null
            && procs.GlGenRenderbuffers != null
            && procs.GlDeleteRenderbuffers != null;
    }
}
