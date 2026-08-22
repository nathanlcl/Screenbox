// Screenbox.Mpv — libmpv 结构体（移植自 libmpv/client.h、render.h、render_gl.h）。
// 全部 blittable，满足 DisableRuntimeMarshalling。布局已与上游头文件逐一核对。

using System;
using System.Runtime.InteropServices;

namespace Screenbox.Mpv.Interop;

/// <summary>typedef struct mpv_handle mpv_handle;（不透明指针，仅经指针使用）</summary>
internal struct MpvHandleNative { }

/// <summary>typedef struct mpv_render_context mpv_render_context;（不透明指针）</summary>
internal struct MpvRenderContextNative { }

/// <summary>
/// mpv_event。x86/x64/arm64 三架构偏移一致：
/// event_id(0) error(4) reply_userdata(8) data(16 x64 | 12 x86/arm32 不适用，x86 为 12)。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public void* Data;
}

/// <summary>
/// mpv_event_property。注意 data 是「指向值的指针」而非内联联合体：
/// FLAG→*(int*)Data；INT64→*(long*)Data；DOUBLE→*(double*)Data；
/// STRING→*(byte**)Data；NODE→(MpvNode*)Data。
/// 该内存由 libmpv 持有，下一次 mpv_wait_event 后失效，调用方不得 free。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventProperty
{
    public byte* Name;
    public MpvFormat Format;
    public void* Data;
}

/// <summary>
/// mpv_event_end_file 的兼容前缀。client API ≥1.108 在其后追加
/// playlist_entry_id / playlist_insert_id / playlist_insert_num_entries；
/// 本绑定只读前两字段，Sequential 前缀布局对任意版本均安全。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;

    /// <summary>reason == Error 时为 MpvError，其他情况为 0。</summary>
    public MpvError Error;
}

/// <summary>mpv_event_log_message。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventLogMessage
{
    public byte* Prefix;
    public byte* Level;
    public byte* Text;
    public int LogLevel;
}

/// <summary>
/// mpv_node。数据联合体在偏移 0（8 字节），format 在偏移 8，三架构一致。
/// 注意：x86（MinGW i686，GCC 对 i64/double 按 4 字节对齐）下原生 sizeof(mpv_node)==12，
/// 而 x64/arm64 为 16。本结构 Size=16 仅保证字段偏移正确；
/// 遍历 mpv_node_list.values 数组或自建节点数组时必须使用 <see cref="MpvNodeLayout.NodeStride"/>，
/// 严禁使用 sizeof(MpvNode) 作数组步长。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct MpvNode
{
    [FieldOffset(0)] public byte* String;   // MPV_FORMAT_STRING
    [FieldOffset(0)] public int Flag;       // MPV_FORMAT_FLAG
    [FieldOffset(0)] public long Int64;     // MPV_FORMAT_INT64
    [FieldOffset(0)] public double Double;  // MPV_FORMAT_DOUBLE
    [FieldOffset(0)] public MpvNodeList* List; // MPV_FORMAT_NODE_ARRAY / NODE_MAP
    [FieldOffset(8)] public MpvFormat Format;
}

/// <summary>
/// mpv_node 数组的原生步长。MinGW i686 遵循 GCC x86 ABI（double/int64 按 4 字节对齐，
/// sizeof(mpv_node)=12）；x64/arm64 为 16。
/// </summary>
internal static class MpvNodeLayout
{
    public static readonly int NodeStride = IntPtr.Size == 8 ? 16 : 12;
}

/// <summary>mpv_node_list。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvNodeList
{
    public int Num;
    public MpvNode* Values;

    /// <summary>NODE_MAP 时有效，keys[N] 对应 values[N]；NODE_ARRAY 时为 NULL。</summary>
    public byte** Keys;
}

/// <summary>mpv_render_param。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public void* Data;
}

/// <summary>
/// mpv_opengl_init_params（render_gl.h）。上游头文件仅两个字段
/// （get_proc_address / get_proc_address_ctx）；SPEC 草案中的 extra_exts 字段
/// 在当前头文件中不存在，已按头文件移除。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvOpenGLInitParams
{
    public delegate* unmanaged[Cdecl]<void*, byte*, void*> GetProcAddress;
    public void* GetProcAddressCtx;
}

/// <summary>mpv_opengl_fbo（render_gl.h）。internal_format 未知时传 0。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGLFBO
{
    public int Fbo;
    public int W;
    public int H;
    public int InternalFormat;
}
