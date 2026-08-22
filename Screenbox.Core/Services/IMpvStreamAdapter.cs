using Screenbox.Mpv.Streams;

namespace Screenbox.Core.Services;

/// <summary>
/// <see cref="IMpvStream"/> 的 Core 层特化标记（SPEC §5.1）：由
/// <see cref="MpvStreamAdapter"/> 实现，包装 WinRT <c>IRandomAccessStream</c>，
/// 供 <c>screenbox://</c> 协议工厂创建。与绑定层 <see cref="IMpvStream"/> 语义一致：
/// 同步阻塞读、可寻址、报告大小、可取消。
/// </summary>
public interface IMpvStreamAdapter : IMpvStream
{
}
