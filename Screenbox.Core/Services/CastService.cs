using Screenbox.Core.Helpers;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;

namespace Screenbox.Core.Services;

/// <summary>
/// 投屏服务 stub（SPEC §D6）：mpv 内核下不支持投屏。
/// 发现返回空 watcher，激活渲染器恒失败。Phase 2 再做内核无关的 mDNS/DLNA 发现。
/// </summary>
public sealed class CastService : ICastService
{
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public RendererWatcher CreateRendererWatcher(IMediaPlayer player)
    {
        return new RendererWatcher();
    }

    /// <inheritdoc/>
    public bool SetActiveRenderer(IMediaPlayer player, Renderer? renderer)
    {
        return false;
    }
}
