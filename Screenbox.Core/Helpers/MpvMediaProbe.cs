// MpvMediaProbe — 网络/URI 源与播放列表子项的元数据探测实例（SPEC §D5）。
// 惰性单例 mpv handle，隐藏实例（vo=null ao=null，无音视频轨输出），
// 注册同一 screenbox 协议（本地播放列表文件经 FAL 令牌走 stream_cb）。
// 探测流程全程 SemaphoreSlim(1,1) 串行，10s 超时 + 调用方 CancellationToken：
//   loadfile <uri> replace → 等 FILE_LOADED（或 END_FILE）→
//   读 metadata / duration / track-list / playlist 快照 → stop。
// 失败/超时返回 null，消费侧走现状同等的"文件名兜底"降级路径（SPEC §D5 回退）。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Screenbox.Core.Factories;
using Screenbox.Mpv;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Helpers;

/// <summary>
/// 元数据/播放列表探测实例。线程安全（内部串行化）；事件在 mpv 事件泵线程触发，
/// 公开 API 均为 async，可在任意线程调用。
/// </summary>
public sealed class MpvMediaProbe : IDisposable
{
    /// <summary>探测超时（SPEC §D5：10s）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly Lazy<MpvMediaProbe> s_shared = new(() => new MpvMediaProbe());

    private readonly MpvHandle _handle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private MpvMediaProbe()
    {
        _handle = new MpvHandle();
        // SPEC §D5：隐藏探测实例，不解码不输出，限制 demuxer 内存
        _handle.SetOption("vo", "null");
        _handle.SetOption("ao", "null");
        _handle.SetOption("vid", "no");
        _handle.SetOption("aid", "no");
        _handle.SetOption("sid", "no");
        _handle.SetOption("pause", "yes");
        _handle.SetOption("config", "no");
        _handle.SetOption("cache", "no");
        _handle.SetOption("demuxer-max-bytes", "5MiB");
        _handle.AddStreamProtocol(MpvStreamFactory.ProtocolScheme, new MpvStreamFactory());
        _handle.Initialize();
    }

    /// <summary>全局共享探测实例（惰性创建）。</summary>
    public static MpvMediaProbe Shared => s_shared.Value;

    /// <summary>
    /// 探测网络/URI 源。成功返回快照；失败或超时返回 <see langword="null"/>。
    /// </summary>
    public Task<MpvProbeResult?> ProbeAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        // file:// URI 还原为原生路径（mpv 对原生 Windows 路径处理更可靠，
        // 且避免百分号转义字符在 file URL 中不被解码的问题）
        return ProbeCoreAsync(uri.IsFile ? uri.LocalPath : uri.OriginalString, cancellationToken);
    }

    /// <summary>
    /// 探测本地文件（如 .ts/.pls/.xspf 播放列表）。临时登记 FAL 令牌后经
    /// <c>screenbox://</c> 协议读取，探测结束立即撤销令牌。
    /// </summary>
    public async Task<MpvProbeResult?> ProbeAsync(StorageFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string token;
        try
        {
            token = StorageApplicationPermissions.FutureAccessList.Add(file, "probe");
        }
        catch (Exception)
        {
            // FAL 不可用则无法探测沙盒文件（SSAM 已从 SDK 26100 移除，无回退）；
            // 探测失败按未知处理，不影响播放链路
            return null;
        }

        try
        {
            return await ProbeCoreAsync(MpvStreamFactory.ProtocolPrefix + token, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                StorageApplicationPermissions.FutureAccessList.Remove(token);
            }
            catch (Exception)
            {
                // 令牌撤销失败不影响探测结果
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        _gate.Dispose();
    }

    private async Task<MpvProbeResult?> ProbeCoreAsync(string uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            // 事件按序派发：上一个探测的 stop 产生的 END_FILE 一定先于本次
            // START_FILE 入队，因此只有见过 START_FILE 之后才采信 END_FILE，
            // 避免把上一轮的 stop 误判为本次加载失败。
            bool started = false;
            TaskCompletionSource<bool> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnStartFile(object? sender, MpvEventArgs e) => started = true;
            void OnFileLoaded(object? sender, MpvFileLoadedEventArgs e) => loaded.TrySetResult(true);
            void OnEndFile(object? sender, MpvEndFileEventArgs e)
            {
                if (started) loaded.TrySetResult(false);
            }

            _handle.StartFile += OnStartFile;
            _handle.FileLoaded += OnFileLoaded;
            _handle.EndFile += OnEndFile;
            try
            {
                _handle.Command("loadfile", uri, "replace");

                bool success;
                try
                {
                    success = await loaded.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;    // 超时或调用方取消（SPEC §D5 回退：降级）
                }

                return success ? ReadSnapshot() : null;
            }
            finally
            {
                _handle.StartFile -= OnStartFile;
                _handle.FileLoaded -= OnFileLoaded;
                _handle.EndFile -= OnEndFile;
                TryStop();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private MpvProbeResult? ReadSnapshot()
    {
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
        if (TryGetNode("metadata", out MpvNodeValue? metadataNode) && metadataNode.Kind == MpvNodeKind.Map)
        {
            foreach (KeyValuePair<string, MpvNodeValue> pair in metadataNode.AsMap)
            {
                if (pair.Value.Kind == MpvNodeKind.String)
                    metadata[pair.Key] = pair.Value.AsString;
            }
        }

        TimeSpan? duration = null;
        try
        {
            double seconds = _handle.GetPropertyDouble("duration"); // 不可用则抛异常
            if (seconds > 0)
                duration = TimeSpan.FromSeconds(seconds);
        }
        catch (MpvException)
        {
            // duration 不可用（流式源等）
        }

        bool hasVideoStream = false;
        if (TryGetNode("track-list", out MpvNodeValue? trackList) && trackList.Kind == MpvNodeKind.Array)
        {
            foreach (MpvNodeValue track in trackList.AsList)
            {
                if (track.Kind != MpvNodeKind.Map) continue;
                IReadOnlyDictionary<string, MpvNodeValue> trackMap = track.AsMap;
                // SPEC §D5：无视频轨（或视频轨为 albumart 封面）→ 按音频处理
                if (!TryGetString(trackMap, "type", out string? type) || type != "video")
                    continue;

                bool isAlbumArt = trackMap.TryGetValue("albumart", out MpvNodeValue? albumArt) &&
                    ((albumArt.Kind == MpvNodeKind.Flag && albumArt.AsBoolean) ||
                     (albumArt.Kind == MpvNodeKind.String && albumArt.AsString == "yes"));
                if (!isAlbumArt)
                    hasVideoStream = true;
            }
        }

        List<MpvPlaylistEntry> playlist = new();
        if (TryGetNode("playlist", out MpvNodeValue? playlistNode) && playlistNode.Kind == MpvNodeKind.Array)
        {
            foreach (MpvNodeValue entry in playlistNode.AsList)
            {
                if (entry.Kind != MpvNodeKind.Map) continue;
                IReadOnlyDictionary<string, MpvNodeValue> entryMap = entry.AsMap;
                if (!TryGetString(entryMap, "filename", out string? filename) || string.IsNullOrEmpty(filename))
                    continue;

                // 注意：外层作用域下方已有同名局部 title（CS0136），这里用 entryTitle。
                TryGetString(entryMap, "title", out string? entryTitle);
                bool current = entryMap.TryGetValue("current", out MpvNodeValue? currentNode) &&
                               currentNode.Kind == MpvNodeKind.Flag && currentNode.AsBoolean;
                playlist.Add(new MpvPlaylistEntry(filename, entryTitle, current));
            }
        }

        // SPEC §D5：title 取 metadata["title"]，media-title 兜底
        string? title = metadata.TryGetValue("title", out string? metaTitle) && !string.IsNullOrEmpty(metaTitle)
            ? metaTitle
            : _handle.GetPropertyString("media-title");

        return new MpvProbeResult(metadata, duration, hasVideoStream, playlist, title);
    }

    private bool TryGetNode(string property, out MpvNodeValue? value)
    {
        try
        {
            value = _handle.GetPropertyNode(property);
            return value.Kind != MpvNodeKind.None;
        }
        catch (MpvException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryGetString(IReadOnlyDictionary<string, MpvNodeValue> map, string key, out string? value)
    {
        if (map.TryGetValue(key, out MpvNodeValue? node) && node.Kind == MpvNodeKind.String)
        {
            value = node.AsString;
            return true;
        }

        value = null;
        return false;
    }

    private void TryStop()
    {
        try
        {
            _handle.Command("stop");
        }
        catch (Exception)
        {
            // 探测收尾的 stop 失败不影响结果；句柄销毁时统一清理
        }
    }
}

/// <summary>一次探测的只读结果快照。</summary>
public sealed class MpvProbeResult
{
    internal MpvProbeResult(IReadOnlyDictionary<string, string> metadata, TimeSpan? duration,
        bool hasVideoStream, IReadOnlyList<MpvPlaylistEntry> playlist, string? title)
    {
        Metadata = metadata;
        Duration = duration;
        HasVideoStream = hasVideoStream;
        Playlist = playlist;
        Title = title;
    }

    /// <summary>mpv <c>metadata</c> 节点快照（键不区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>时长；不可用（流式源）时为 <see langword="null"/>。</summary>
    public TimeSpan? Duration { get; }

    /// <summary>是否存在非封面（albumart）视频轨。false → 按音频处理（SPEC §D5 类型推断）。</summary>
    public bool HasVideoStream { get; }

    /// <summary>mpv 原生展开的播放列表项；普通媒体仅含其自身（1 项）。</summary>
    public IReadOnlyList<MpvPlaylistEntry> Playlist { get; }

    /// <summary>标题（metadata["title"]，media-title 兜底）；无则 <see langword="null"/>。</summary>
    public string? Title { get; }

    // SPEC §D5 元数据键映射表：键存在则用，否则 null
    /// <summary>metadata["artist"]。</summary>
    public string? Artist => GetMetadataOrNull("artist");

    /// <summary>metadata["album"]。</summary>
    public string? Album => GetMetadataOrNull("album");

    /// <summary>metadata["show_name"]（mpv 无标准剧集键，键存在则用）。</summary>
    public string? ShowName => GetMetadataOrNull("show_name");

    /// <summary>metadata["season"]。</summary>
    public string? Season => GetMetadataOrNull("season");

    /// <summary>metadata["episode"]。</summary>
    public string? Episode => GetMetadataOrNull("episode");

    private string? GetMetadataOrNull(string key) =>
        Metadata.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
}

/// <summary>播放列表条目（mpv <c>playlist</c> 数组元素）。</summary>
public sealed class MpvPlaylistEntry
{
    internal MpvPlaylistEntry(string fileName, string? title, bool current)
    {
        FileName = fileName;
        Title = title;
        Current = current;
    }

    /// <summary>条目 URI/路径（可能为相对路径，由消费方按源目录解析）。</summary>
    public string FileName { get; }

    /// <summary>条目标题；无则 <see langword="null"/>。</summary>
    public string? Title { get; }

    /// <summary>是否为当前播放项。</summary>
    public bool Current { get; }
}
