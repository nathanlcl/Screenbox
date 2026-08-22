// Screenbox.Mpv — 自定义协议流工厂（SPEC §6.2）。

namespace Screenbox.Mpv.Streams;

/// <summary>对应 mpv_stream_cb_open_ro_fn：按 URI 打开流。在 mpv 内部线程调用，须尽快返回。</summary>
public interface IMpvStreamFactory
{
    /// <summary>打开完整 URI（如 screenbox://&lt;token&gt;）；失败/未知令牌返回 null。</summary>
    IMpvStream? Open(string uri);
}
