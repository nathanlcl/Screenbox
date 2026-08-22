// Screenbox.Mpv — libmpv client.h P/Invoke 声明层。
// 约定：字符串全部手工 UTF-8（byte*，见 Utf8Marshaller）；CallingConvention.Cdecl
// 为显式指定（MinGW 构建的 libmpv-2.dll 导出为 cdecl，x86 上与默认 Winapi/Stdcall
// 混用会破坏栈平衡，x64/arm64 无差异）。全部签名已与 client.h 核对。

using System.Runtime.InteropServices;

namespace Screenbox.Mpv.Interop;

internal static unsafe partial class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    // ---- 生命周期 ----
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial MpvHandleNative* mpv_create();

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_initialize(MpvHandleNative* ctx);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial void mpv_terminate_destroy(MpvHandleNative* ctx);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_set_option_string(MpvHandleNative* ctx, byte* name, byte* data);

    /// <summary>返回静态串，免 free。</summary>
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial byte* mpv_error_string(int error);

    // ---- 命令 ----
    /// <summary>args 为 NULL 结尾数组。</summary>
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_command(MpvHandleNative* ctx, byte** args);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_command_async(MpvHandleNative* ctx, ulong reply_userdata, byte** args);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_command_node(MpvHandleNative* ctx, MpvNode* args, MpvNode* result);

    // ---- 属性 ----
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_set_property_string(MpvHandleNative* ctx, byte* name, byte* data);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_get_property(MpvHandleNative* ctx, byte* name, MpvFormat format, void* data);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_set_property(MpvHandleNative* ctx, byte* name, MpvFormat format, void* data);

    /// <summary>结果须 mpv_free。</summary>
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial byte* mpv_get_property_string(MpvHandleNative* ctx, byte* name);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_observe_property(MpvHandleNative* ctx, ulong reply_userdata, byte* name, MpvFormat format);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_unobserve_property(MpvHandleNative* ctx, ulong registered_reply_userdata);

    // ---- 事件 / 日志 ----
    /// <summary>返回值永不为 NULL；超时/无事件返回 MPV_EVENT_NONE。</summary>
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial MpvEvent* mpv_wait_event(MpvHandleNative* ctx, double timeout);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial void mpv_set_wakeup_callback(
        MpvHandleNative* ctx, delegate* unmanaged[Cdecl]<void*, void> cb, void* data);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_request_log_messages(MpvHandleNative* ctx, byte* min_level);

    // ---- 内存 ----
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial void mpv_free(void* data);

    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial void mpv_free_node_contents(MpvNode* node);
}
