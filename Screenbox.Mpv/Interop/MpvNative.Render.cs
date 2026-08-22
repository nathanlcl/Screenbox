// Screenbox.Mpv — libmpv render.h P/Invoke 声明层。
// 线程约束（render.h 文档硬约束）：mpv_render_context_* 只允许在「拥有 GL/SW 上下文
// 的渲染线程」上调用，且绝不允许在 update callback 内调用；封装层 MpvRenderContext
// 以线程亲和断言执行该约束。

using System.Runtime.InteropServices;

namespace Screenbox.Mpv.Interop;

internal static unsafe partial class MpvRenderNative
{
    private const string Lib = "libmpv-2.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_create(
        MpvRenderContextNative** res, MpvHandleNative* mpv, MpvRenderParam* @params);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_free(MpvRenderContextNative* ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_render(MpvRenderContextNative* ctx, MpvRenderParam* @params);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_render_context_update(MpvRenderContextNative* ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_set_update_callback(
        MpvRenderContextNative* ctx, delegate* unmanaged[Cdecl]<void*, void> cb, void* data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_report_swap(MpvRenderContextNative* ctx);
}
