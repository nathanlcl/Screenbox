using System;
using System.Collections.Generic;
using Screenbox.Core.Events;
using Screenbox.Core.Models;

namespace Screenbox.Core.Helpers;

/// <summary>
/// 投屏设备发现空壳（SPEC §D6 投屏 stub）：不发现任何设备，<see cref="Start"/>
/// 恒返回 <see langword="false"/>，事件从不触发。保留公共接口以避免
/// CastControlViewModel 等消费侧大面积改动；Phase 2 再做内核无关的 mDNS/DLNA 发现。
/// </summary>
public sealed class RendererWatcher : IDisposable
{
#pragma warning disable CS0067 // 事件是公共契约的一部分，stub 实现从不触发
    public event EventHandler<RendererFoundEventArgs>? RendererFound;
    public event EventHandler<RendererLostEventArgs>? RendererLost;
#pragma warning restore CS0067

    public bool IsStarted { get; private set; }

    internal RendererWatcher()
    {
    }

    public IReadOnlyList<Renderer> GetRenderers()
    {
        return Array.Empty<Renderer>();
    }

    public bool Start()
    {
        // 无发现器可启动
        return false;
    }

    public void Stop()
    {
        IsStarted = false;
    }

    public void Dispose()
    {
        Stop();
    }
}
