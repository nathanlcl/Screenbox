// Screenbox.VideoView — WGL_NV_DX_interop 后端（SPEC §D3 路线 A）。
// 原理：隐藏 message-only HWND + WGL 兼容上下文（渲染线程持有并 current）→
// wglDXOpenDeviceNV(ID3D11Device) 建立 D3D/GL 互操作设备 → wglDXRegisterObjectNV 把
// swapchain backbuffer 注册为 GL renderbuffer 并挂到 FBO → mpv_render_context_render
// 直渲（零拷贝）。渲染时必须 wglDXLockObjectsNV/wglDXUnlockObjectsNV 成对包裹。
// 参考实现：Richasy/mpv-winui（MIT）src/Mpv.UI/Common/{RenderContext,FrameBuffer}.cs。
// 线程约束：本类全部方法（含 TryCreate/Dispose）只能在 MpvRenderHost 的渲染线程调用。

using System;
using System.Runtime.InteropServices;
using Screenbox.Controls.Interop;
using Screenbox.Mpv;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Screenbox.Controls;

internal sealed unsafe class WglDxInteropBackend : IRenderBackend
{
    private const string WindowClassName = "ScreenboxMpvGlRenderWindow";

    private static ushort s_windowClassAtom;
    private static readonly object s_windowClassLock = new();

    private WglProcs _wgl;
    private nint _hwnd;
    private nint _hdc;
    private nint _hglrc;
    private nint _glDevice;    // wglDXOpenDeviceNV 返回的互操作设备句柄
    private uint _fbo;
    private uint _renderbuffer;
    private nint _dxObject;    // wglDXRegisterObjectNV 返回的互操作对象句柄
    private int _width;
    private int _height;
    private bool _disposed;

    private WglDxInteropBackend()
    {
    }

    /// <summary>
    /// 尝试创建 WGL 互操作后端；任一步骤失败（无扩展/上下文创建失败等）返回 null，
    /// 调用方应回退 <see cref="SoftwareRenderBackend"/>。必须在渲染线程调用。
    /// </summary>
    internal static WglDxInteropBackend? TryCreate(ID3D11Device* device)
    {
        if (device == null)
            return null;

        WglDxInteropBackend backend = new();
        try
        {
            backend.Initialize(device);
            return backend;
        }
        catch
        {
            backend.Dispose();
            return null;
        }
    }

    private void Initialize(ID3D11Device* device)
    {
        // 1. 隐藏 message-only HWND + DC + 像素格式 + WGL 兼容上下文
        EnsureWindowClassRegistered();
        nint instance = WglNative.GetModuleHandleW(null); // 当前进程 HINSTANCE

        fixed (char* pClass = WindowClassName)
        {
            _hwnd = WglNative.CreateWindowExW(
                0, pClass, null, 0, 0, 0, 0, 0,
                WglNative.HwndMessage, 0, instance, null);
        }

        if (_hwnd == 0)
            throw new InvalidOperationException("CreateWindowExW(message-only) 失败。");

        _hdc = WglNative.GetDC(_hwnd);
        if (_hdc == 0)
            throw new InvalidOperationException("GetDC 失败。");

        WglNative.PixelFormatDescriptor pfd = WglNative.PixelFormatDescriptor.CreateDefault();
        int pixelFormat = WglNative.ChoosePixelFormat(_hdc, &pfd);
        if (pixelFormat == 0 || WglNative.SetPixelFormat(_hdc, pixelFormat, &pfd) == 0)
            throw new InvalidOperationException("Choose/SetPixelFormat 失败。");

        _hglrc = WglNative.wglCreateContext(_hdc);
        if (_hglrc == 0 || WglNative.wglMakeCurrent(_hdc, _hglrc) == 0)
            throw new InvalidOperationException("wglCreateContext/wglMakeCurrent 失败。");

        // 2. 检查 WGL_NV_DX_interop 扩展并解析函数表
        if (!WglProcs.TryLoad(out _wgl))
            throw new InvalidOperationException("WGL/GL 函数解析失败。");

        if (_wgl.GetExtensionsStringARB == null || !HasDxInteropExtension())
            throw new InvalidOperationException("驱动不支持 WGL_NV_DX_interop。");

        // 3. 绑定到我们已有的 D3D11 device（VideoView 创建，句柄生命周期由 VideoView 保证）
        _glDevice = _wgl.DXOpenDeviceNV(device);
        if (_glDevice == 0)
            throw new InvalidOperationException("wglDXOpenDeviceNV 失败。");

        // 4. FBO 只建一次；颜色附件在 Attach 时随 backbuffer 尺寸重建
        uint fbo;
        _wgl.GlGenFramebuffers(1, &fbo);
        if (fbo == 0)
            throw new InvalidOperationException("glGenFramebuffers 失败。");
        _fbo = fbo;
    }

    private bool HasDxInteropExtension()
    {
        byte* extensions = _wgl.GetExtensionsStringARB(_hdc);
        if (extensions == null)
            return false;
        string text = Marshal.PtrToStringAnsi((IntPtr)extensions) ?? string.Empty;
        return text.Contains("WGL_NV_DX_interop", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public MpvRenderContext CreateRenderContext(MpvHandle handle)
    {
        // MpvRenderContext.GetProcAddress 为静态属性（见 MpvRenderContext 注释）：
        // 必须在 CreateOpenGL 之前设置，WGL 上下文已在本线程 current。
        MpvRenderContext.GetProcAddress = WglNative.ResolveGlProc;
        return MpvRenderContext.CreateOpenGL(handle);
    }

    /// <inheritdoc/>
    public void Attach(IDXGISwapChain1* swapChain, int pixelWidth, int pixelHeight)
    {
        Detach();

        // backbuffer GetBuffer(0) → 注册为 GL renderbuffer → 挂到 FBO 颜色附件
        // Silk.NET GetBuffer 泛型重载为 out 参数（故不能用 using 声明，显式 try/finally 释放）。
        Silk.NET.Core.Native.ComPtr<ID3D11Texture2D> backBuffer;
        int hr = swapChain->GetBuffer(0, out backBuffer);
        try
        {
            if (hr < 0 || backBuffer.Handle == null)
                throw new InvalidOperationException($"IDXGISwapChain::GetBuffer 失败 (0x{hr:X8})。");

            // wglDXRegisterObjectNV 内部持有 D3D 资源引用；GetBuffer 取得的引用随
            // ComPtr 作用域结束立即归还，否则 ResizeBuffers 会因未释放的引用而失败。
            uint renderbuffer = 0;
            _wgl.GlGenRenderbuffers(1, &renderbuffer);
            if (renderbuffer == 0)
                throw new InvalidOperationException("glGenRenderbuffers 失败。");

            nint dxObject = _wgl.DXRegisterObjectNV(
                _glDevice, backBuffer.Handle, renderbuffer, WglNative.GlRenderbuffer, WglNative.WglAccessReadWriteNv);
            if (dxObject == 0)
            {
                _wgl.GlDeleteRenderbuffers(1, &renderbuffer);
                throw new InvalidOperationException("wglDXRegisterObjectNV 失败。");
            }

            _renderbuffer = renderbuffer;
            _dxObject = dxObject;
            _width = pixelWidth;
            _height = pixelHeight;

            _wgl.GlBindFramebuffer(WglNative.GlFramebuffer, _fbo);
            _wgl.GlFramebufferRenderbuffer(
                WglNative.GlFramebuffer, WglNative.GlColorAttachment0, WglNative.GlRenderbuffer, renderbuffer);
            uint status = _wgl.GlCheckFramebufferStatus(WglNative.GlFramebuffer);
            _wgl.GlBindFramebuffer(WglNative.GlFramebuffer, 0);
            if (status != WglNative.GlFramebufferComplete)
                throw new InvalidOperationException($"FBO 不完整 (0x{status:X4})。");
        }
        finally
        {
            backBuffer.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Detach()
    {
        if (_dxObject != 0)
        {
            // 对象必须处于未锁定状态（本类锁只在 RenderFrame 内成对持有，这里必然未锁）。
            _wgl.DXUnregisterObjectNV(_glDevice, _dxObject);
            _dxObject = 0;
        }

        if (_renderbuffer != 0)
        {
            uint renderbuffer = _renderbuffer;
            _wgl.GlDeleteRenderbuffers(1, &renderbuffer);
            _renderbuffer = 0;
        }

        _width = _height = 0;
    }

    /// <inheritdoc/>
    public void RenderFrame(MpvRenderContext context)
    {
        if (_dxObject == 0)
            return;

        nint dxObject = _dxObject;
        if (_wgl.DXLockObjectsNV(_glDevice, 1, &dxObject) == 0)
            return; // 锁定失败本帧放弃，下一帧重试（不得在未持锁时调用 GL 渲染）

        try
        {
            context.RenderOpenGL((int)_fbo, _width, _height);
        }
        finally
        {
            _wgl.DXUnlockObjectsNV(_glDevice, 1, &dxObject); // lock/unlock 严格成对
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 以下全部在渲染线程执行，WGL 上下文此刻仍 current（先拆 GL 侧再销毁上下文）。
        if (_glDevice != 0)
        {
            Detach();
            if (_fbo != 0)
            {
                uint fbo = _fbo;
                _wgl.GlDeleteFramebuffers(1, &fbo);
                _fbo = 0;
            }

            _wgl.DXCloseDeviceNV(_glDevice);
            _glDevice = 0;
        }

        if (_hglrc != 0)
        {
            WglNative.wglMakeCurrent(0, 0);
            WglNative.wglDeleteContext(_hglrc);
            _hglrc = 0;
        }

        if (_hdc != 0)
        {
            WglNative.ReleaseDC(_hwnd, _hdc);
            _hdc = 0;
        }

        if (_hwnd != 0)
        {
            WglNative.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private static void EnsureWindowClassRegistered()
    {
        if (s_windowClassAtom != 0)
            return;

        lock (s_windowClassLock)
        {
            if (s_windowClassAtom != 0)
                return;

            fixed (char* pClass = WindowClassName)
            {
                WglNative.WndClassExW wc = new()
                {
                    Size = (uint)sizeof(WglNative.WndClassExW),
                    Style = 0,
                    WndProc = &WglNative.WindowProc,
                    ClsExtra = 0,
                    WndExtra = 0,
                    Instance = WglNative.GetModuleHandleW(null),
                    Icon = 0,
                    Cursor = 0,
                    Background = 0,
                    MenuName = null,
                    ClassName = pClass,
                    IconSm = 0,
                };
                ushort atom = WglNative.RegisterClassExW(&wc);
                if (atom == 0)
                    throw new InvalidOperationException("RegisterClassExW 失败。");
                s_windowClassAtom = atom;
            }
        }
    }
}
