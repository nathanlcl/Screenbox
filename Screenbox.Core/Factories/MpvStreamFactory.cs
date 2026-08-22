// MpvStreamFactory — screenbox:// 协议工厂（SPEC §D4）。
// 本地文件一律走 mpv_stream_cb_add_ro 自定义协议：PlayerService 把 StorageFile
// 加入 FAL/SharedStorageAccessManager 得到令牌，播放 URI 为 screenbox://<token>；
// mpv demuxer 命中该协议时回调 Open，按令牌赎回 IStorageFile 并包装成
// MpvStreamAdapter。外挂字幕用 screenbox://sub/<token>（SPEC §6.3 AddSubtitle），
// 走同一 stream 层，仅多一个 "sub/" 前缀。
//
// Open 在 mpv 内部线程被同步调用，此处 .GetAwaiter().GetResult() 阻塞合法（SPEC §D4）。

using System;
using System.IO;
using System.Threading.Tasks;
using Screenbox.Core.Services;
using Screenbox.Mpv.Streams;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Streams;

namespace Screenbox.Core.Factories;

/// <summary>
/// <c>screenbox://</c> 自定义协议的流工厂。无状态，可由多个 <c>MpvHandle</c>
/// （播放器实例与 <see cref="Helpers.MpvMediaProbe"/> 探测实例）共享注册。
/// </summary>
public sealed class MpvStreamFactory : IMpvStreamFactory
{
    /// <summary>协议名（注册用，不含 "://"）。</summary>
    public const string ProtocolScheme = "screenbox";

    /// <summary>URI 前缀（含 "://"）。</summary>
    public const string ProtocolPrefix = "screenbox://";

    /// <summary>外挂字幕令牌前缀（<c>screenbox://sub/&lt;token&gt;</c>，SPEC §6.3）。</summary>
    public const string SubtitlePrefix = "sub/";

    /// <inheritdoc/>
    public IMpvStream? Open(string uri)
    {
        if (!uri.StartsWith(ProtocolPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string token = uri.Substring(ProtocolPrefix.Length);
        if (token.StartsWith(SubtitlePrefix, StringComparison.OrdinalIgnoreCase))
            token = token.Substring(SubtitlePrefix.Length);
        if (token.Length == 0)
            return null;

        try
        {
            IStorageFile file = RedeemFileAsync(token).GetAwaiter().GetResult();
            return CreateAsync(file).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 令牌未知/已过期、文件不可访问等：返回 null，mpv 侧按加载失败处理
            return null;
        }
    }

    /// <summary>
    /// 打开文件并包装成 <see cref="IMpvStream"/>（256KB 预读、可寻址、可取消）。
    /// </summary>
    /// <param name="file">要打开的本地文件。</param>
    public static async Task<IMpvStream> CreateAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        IRandomAccessStreamWithContentType stream = await file.OpenAsync(FileAccessMode.Read);
        return new MpvStreamAdapter(stream);
    }

    /// <summary>
    /// 按令牌赎回文件。令牌由 PlayerService（"media"）或 MpvMediaProbe（"probe"）
    /// 写入 FutureAccessList；FAL 不可用（或令牌不属于 FAL）时回退
    /// SharedStorageAccessManager，与 PlayerService 的写入策略互为镜像。
    /// </summary>
    private static async Task<IStorageFile> RedeemFileAsync(string token)
    {
        try
        {
            return await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
        }
        catch (Exception)
        {
            // FileNotFoundException（令牌不在 FAL）/ FAL 不可用：回退 SSAM
        }

        IStorageFile? file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token);
        return file ?? throw new FileNotFoundException("Unable to redeem the playback access token.", token);
    }
}
