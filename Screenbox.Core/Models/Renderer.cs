namespace Screenbox.Core.Models;

/// <summary>
/// 投屏目标占位模型（SPEC §D6 投屏 stub）。播放内核替换为 mpv 后不再发现任何
/// 渲染设备；保留本模型与全部公共属性以避免 UI（CastControl.xaml）大面积改动。
/// Phase 2 再做内核无关的 mDNS/DLNA 发现。
/// </summary>
public sealed partial class Renderer
{
    public bool IsAvailable { get; private set; }

    public string Name { get; }

    public string Type { get; }

    public string? IconUri { get; }

    public bool CanRenderVideo { get; }

    public bool CanRenderAudio { get; }

    internal Renderer(string name, string type, string? iconUri, bool canRenderVideo, bool canRenderAudio)
    {
        Name = name;
        Type = type;
        IconUri = iconUri;
        CanRenderVideo = canRenderVideo;
        CanRenderAudio = canRenderAudio;
        IsAvailable = true;
    }

    internal void Dispose()
    {
        IsAvailable = false;
    }

    public override string ToString()
    {
        return $"{Name}, {Type}";
    }
}
