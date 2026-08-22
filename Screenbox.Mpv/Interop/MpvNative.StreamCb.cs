// Screenbox.Mpv — libmpv stream_cb.h P/Invoke 声明层。
// 与头文件核对差异：SPEC 草案中 read_fn 返回类型写作 int，头文件实际为
// int64_t (*read_fn)(void *cookie, char *buf, uint64_t nbytes)——x86/x64 上
// 返回值宽度错误会破坏 ABI（负值 -1 会被读成 4294967295），已按头文件修正为 long。

using System.Runtime.InteropServices;

namespace Screenbox.Mpv.Interop;

/// <summary>
/// mpv_stream_cb_info。open_fn 返回 0 时必须填充 cookie 与全部函数指针；
/// 返回负值（mpv_error）表示打开失败。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvStreamCbInfo
{
    public void* cookie;

    /// <summary>返回读取字节数；0=EOF；负值=错误。</summary>
    public delegate* unmanaged[Cdecl]<void*, byte*, ulong, long> read_fn;

    /// <summary>返回新偏移；-1=不支持 seek（流则为不可定位）。</summary>
    public delegate* unmanaged[Cdecl]<void*, long, long> seek_fn;

    /// <summary>返回总大小；未知可返回 -1。</summary>
    public delegate* unmanaged[Cdecl]<void*, long> size_fn;

    /// <summary>mpv 关闭流时调用；在此释放 cookie 关联资源。</summary>
    public delegate* unmanaged[Cdecl]<void*, void> close_fn;

    /// <summary>请求中止进行中的阻塞读；之后 read_fn 应尽快返回负值。</summary>
    public delegate* unmanaged[Cdecl]<void*, void> cancel_fn;
}

internal static unsafe partial class MpvNative
{
    /// <summary>
    /// 注册只读自定义协议。open_fn 在 mpv 内部线程调用，须尽快返回；
    /// user_data 透传给 open_fn。
    /// </summary>
    [LibraryImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static partial int mpv_stream_cb_add_ro(
        MpvHandleNative* ctx, byte* protocol, void* user_data,
        delegate* unmanaged[Cdecl]<void*, byte*, MpvStreamCbInfo*, int> open_fn);
}
