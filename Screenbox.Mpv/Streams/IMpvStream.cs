// Screenbox.Mpv — mpv_stream_cb_add_ro 自定义协议流抽象（SPEC §6.2）。
// 实现注意：Read/Seek 在 mpv 内部线程同步阻塞调用；任何异常都会被绑定层
// 吞掉并转为 -1（错误）返回给 mpv，实现方可抛异常表示失败。

using System;

namespace Screenbox.Mpv.Streams;

/// <summary>只读媒体流（对应 mpv_stream_cb_info 的 read/seek/size/close/cancel）。</summary>
public interface IMpvStream : IDisposable
{
    /// <summary>流总大小；对应 size_fn。</summary>
    long Size { get; }

    /// <summary>读取至多 buffer.Length 字节；返回实际读取数，0=EOF，抛异常=错误。</summary>
    int Read(Span<byte> buffer);

    /// <summary>定位到绝对偏移并返回新偏移；不支持返回 -1。</summary>
    long Seek(long offset);

    /// <summary>请求中止进行中的阻塞读；之后 Read 应尽快返回或抛异常。对应 cancel_fn。</summary>
    void Cancel();
}
