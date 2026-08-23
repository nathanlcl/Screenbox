// Screenbox.Mpv — mpv_render_context 封装（SPEC §6.2，OpenGL / SW 两种创建）。
// 线程亲和（render.h 硬约束）：所有 mpv_render_context_* 调用（含 free）必须在
// 创建实例的渲染线程上进行，且绝不能在 update callback 内调用 mpv API。
// 封装层在 Render/Update/ReportSwap 上以断言执行该约束。
//
// 与 SPEC 草案的偏差：GetProcAddress 为静态属性（草案写作实例属性）。原因：
// mpv_render_context_create 在创建时就需要 get_proc_address，实例属性存在
// 鸡生蛋问题；草案注释「OpenGL 创建前设置」只有在静态语义下成立。
// 用法：MpvRenderContext.GetProcAddress = wglResolver; var ctx = MpvRenderContext.CreateOpenGL(handle);

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Screenbox.Mpv.Interop;

namespace Screenbox.Mpv;

public sealed unsafe class MpvRenderContext : IDisposable
{
    private static Func<string, nint>? s_getProcAddress;

    private readonly int _ownerThreadId;
    private MpvRenderContextNative* _ctx;
    private GCHandle _selfHandle;
    private GCHandle _resolverHandle;
    private volatile bool _disposed;

    private MpvRenderContext(MpvRenderContextNative* ctx, GCHandle resolverHandle)
    {
        _ctx = ctx;
        _resolverHandle = resolverHandle;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _selfHandle = GCHandle.Alloc(this);
        MpvRenderNative.mpv_render_context_set_update_callback(
            ctx, &OnFrameReady, (void*)GCHandle.ToIntPtr(_selfHandle));
    }

    /// <summary>
    /// OpenGL 函数解析器（如 wglGetProcAddress 包装 + opengl32 GetProcAddress 回退）。
    /// 必须在 <see cref="CreateOpenGL"/> 之前设置；创建后解析器句柄随实例存活。
    /// </summary>
    public static Func<string, nint>? GetProcAddress
    {
        get => s_getProcAddress;
        set => s_getProcAddress = value;
    }

    /// <summary>调用方保证 GL 上下文已 current（MPV_RENDER_PARAM_ADVANCED_CONTROL=1）。</summary>
    public static MpvRenderContext CreateOpenGL(MpvHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Func<string, nint> resolver = GetProcAddress
            ?? throw new InvalidOperationException(
                "CreateOpenGL 前必须先设置 MpvRenderContext.GetProcAddress。");

        GCHandle resolverHandle = GCHandle.Alloc(resolver);
        try
        {
            MpvOpenGLInitParams glInit = new()
            {
                GetProcAddress = &GetProcAddressThunk,
                GetProcAddressCtx = (void*)GCHandle.ToIntPtr(resolverHandle),
            };
            int advancedControl = 1;
            MpvRenderContextNative* ctx;
            ReadOnlySpan<byte> apiType = "opengl"u8;
            fixed (byte* apiTypePtr = apiType)
            {
                MpvRenderParam* p = stackalloc MpvRenderParam[4];
                p[0] = new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = apiTypePtr };
                p[1] = new MpvRenderParam { Type = MpvRenderParamType.OpenGLInitParams, Data = &glInit };
                p[2] = new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = &advancedControl };
                p[3] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = null };
                MpvException.ThrowOnError(
                    MpvRenderNative.mpv_render_context_create(&ctx, handle.Raw, p),
                    "mpv_render_context_create(opengl)");
            }

            return new MpvRenderContext(ctx, resolverHandle);
        }
        catch
        {
            resolverHandle.Free();
            throw;
        }
    }

    /// <summary>软渲染（MPV_RENDER_API_TYPE_SW，无 GL 依赖，回退路径用）。</summary>
    public static MpvRenderContext CreateSoftware(MpvHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        int advancedControl = 1;
        MpvRenderContextNative* ctx;
        ReadOnlySpan<byte> apiType = "sw"u8;
        fixed (byte* apiTypePtr = apiType)
        {
            MpvRenderParam* p = stackalloc MpvRenderParam[3];
            p[0] = new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = apiTypePtr };
            p[1] = new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = &advancedControl };
            p[2] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = null };
            MpvException.ThrowOnError(
                MpvRenderNative.mpv_render_context_create(&ctx, handle.Raw, p),
                "mpv_render_context_create(sw)");
        }

        return new MpvRenderContext(ctx, default);
    }

    /// <summary>
    /// MPV_RENDER_PARAM_OPENGL_FBO + flip_y=0。mpv 的"正常"渲染（flip_y=0）产出
    /// 顶行在前的纹理内容，与 D3D11 的顶左原点一致；flip_y=1 是给 GL 默认帧缓冲
    /// （底左原点）用的，渲染进 D3D 互操作纹理时反而会导致画面上下颠倒
    /// （参考实现 Richasy/mpv-winui 同样用 flip_y=0）。
    /// </summary>
    public void RenderOpenGL(int fbo, int width, int height)
    {
        AssertRenderThread();
        MpvOpenGLFBO fboParam = new() { Fbo = fbo, W = width, H = height, InternalFormat = 0 };
        int flipY = 0;
        MpvRenderParam* p = stackalloc MpvRenderParam[3];
        p[0] = new MpvRenderParam { Type = MpvRenderParamType.OpenGLFbo, Data = &fboParam };
        p[1] = new MpvRenderParam { Type = MpvRenderParamType.FlipY, Data = &flipY };
        p[2] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = null };
        MpvException.ThrowOnError(
            MpvRenderNative.mpv_render_context_render(_ctx, p), "mpv_render_context_render(opengl)");
    }

    /// <summary>渲染到 CPU 位图（"bgr0"；stride 对齐要求见 render.h，通常 4 的倍数）。</summary>
    public void RenderSoftware(nint buffer, int width, int height, nint stride)
    {
        AssertRenderThread();
        int* size = stackalloc int[2] { width, height };
        ReadOnlySpan<byte> format = "bgr0"u8;
        fixed (byte* formatPtr = format)
        {
            MpvRenderParam* p = stackalloc MpvRenderParam[5];
            p[0] = new MpvRenderParam { Type = MpvRenderParamType.SwSize, Data = size };
            p[1] = new MpvRenderParam { Type = MpvRenderParamType.SwFormat, Data = formatPtr };
            p[2] = new MpvRenderParam { Type = MpvRenderParamType.SwStride, Data = (void*)stride };
            p[3] = new MpvRenderParam { Type = MpvRenderParamType.SwPointer, Data = (void*)buffer };
            p[4] = new MpvRenderParam { Type = MpvRenderParamType.Invalid, Data = null };
            MpvException.ThrowOnError(
                MpvRenderNative.mpv_render_context_render(_ctx, p), "mpv_render_context_render(sw)");
        }
    }

    /// <summary>ADVANCED_CONTROL 下每次 FrameReady 后必须调用（可合并到最后一次）。</summary>
    public MpvRenderUpdateFlag Update()
    {
        AssertRenderThread();
        return (MpvRenderUpdateFlag)MpvRenderNative.mpv_render_context_update(_ctx);
    }

    /// <summary>Present 完成后调用（mpv 据此测量交换间隔/帧调度）。</summary>
    public void ReportSwap()
    {
        AssertRenderThread();
        MpvRenderNative.mpv_render_context_report_swap(_ctx);
    }

    /// <summary>
    /// 有新帧可渲染。在 mpv 任意内部线程触发：处理器内禁止调用任何 mpv API，
    /// 只允许置标志/发信号唤醒渲染线程。
    /// </summary>
    public event EventHandler? FrameReady;

    /// <summary>释放渲染上下文。必须在渲染线程调用（render.h 线程约束）。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ctx != null)
        {
            // 先摘掉回调，确保 free 后不会再有 FrameReady 触碰已释放的 GCHandle。
            MpvRenderNative.mpv_render_context_set_update_callback(_ctx, null, null);
            MpvRenderNative.mpv_render_context_free(_ctx);
            _ctx = null;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        if (_resolverHandle.IsAllocated)
            _resolverHandle.Free();
        GC.SuppressFinalize(this);
    }

    private void AssertRenderThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "MpvRenderContext 的方法只能在创建它的渲染线程上调用（libmpv render API 线程亲和约束）。");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFrameReady(void* data)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)data).Target is MpvRenderContext { _disposed: false } self)
                self.FrameReady?.Invoke(self, EventArgs.Empty);
        }
        catch
        {
            // 禁止跨 native 边界抛异常。
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* GetProcAddressThunk(void* ctx, byte* name)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)ctx).Target is Func<string, nint> resolver)
                return (void*)resolver(Utf8Marshaller.ToString(name) ?? string.Empty);
        }
        catch
        {
            // 禁止跨 native 边界抛异常；解析失败返回 NULL，由 mpv 侧报缺失符号。
        }

        return null;
    }
}
