using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Events;
using Screenbox.Mpv;
using Screenbox.Mpv.Interop;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Playback;

/// <summary>
/// libmpv 内核的 <see cref="IMediaPlayer"/> 实现（SPEC §6.3 映射总表）。
/// 事件在 mpv 事件泵后台线程触发；消费侧沿用 DispatcherQueue 回 UI 线程的模式。
/// </summary>
public sealed partial class MpvMediaPlayer : IMediaPlayer, IMpvPlayer
{
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaEnded;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaFailed;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? MediaOpened;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? IsMutedChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? VolumeChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<PlaybackItem?>>? PlaybackItemChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingProgressChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingStarted;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingEnded;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? NaturalDurationChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? NaturalVideoSizeChanged;
    public event TypedEventHandler<IMediaPlayer, EventArgs>? CanSeekChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? PositionChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<ChapterCue?>>? ChapterChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<MediaPlaybackState>>? PlaybackStateChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? PlaybackRateChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? SubtitleDelayChanged;
    public event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? AudioDelayChanged;

    /// <summary>mpv <c>chapter-list</c> 快照更新（含 FileLoaded 后的首次填充），供章节 UI 刷新。</summary>
    public event EventHandler? ChapterListChanged;

    public ChapterCue? Chapter
    {
        get => _chapter;
        set
        {
            if (value == _chapter) return;
            ChapterCue? oldValue = _chapter;
            _chapter = value;
            ChapterChanged?.Invoke(this, new ValueChangedEventArgs<ChapterCue?>(value, oldValue));
        }
    }

    public TimeSpan NaturalDuration
    {
        get => _naturalDuration;
        private set
        {
            // Length can fluctuate during playback. Check for tolerance here.
            if (Math.Abs((_naturalDuration - value).TotalMilliseconds) <= 50) return;
            TimeSpan oldValue = _naturalDuration;
            _naturalDuration = value;
            if (_playbackItem != null) _playbackItem.Duration = value > TimeSpan.Zero ? value : null;
            NaturalDurationChanged?.Invoke(this, new ValueChangedEventArgs<TimeSpan>(value, oldValue));
        }
    }

    public TimeSpan Position
    {
        get => _position;
        set
        {
            if (PlaybackItem == null) return;
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value > NaturalDuration) value = NaturalDuration;
            if (_ended && value == NaturalDuration)
            {
                _position = value;
                return;
            }

            TimeSpan oldValue = _position;
            _position = value;
            try
            {
                Handle.SetPropertyDouble("time-pos", value.TotalSeconds);
                if (_ended)
                {
                    // keep-open 停在 EOF 后，seek 回去即恢复播放
                    _ended = false;
                    Handle.SetPropertyFlag("pause", false);
                }
            }
            catch (Exception e) when (e is MpvException or ObjectDisposedException)
            {
                LogSeekError(_logger, e);
                return;
            }

            // Position changed will not fire if the player is paused
            if (PlaybackState is MediaPlaybackState.Paused)
            {
                PositionChanged?.Invoke(this, new ValueChangedEventArgs<TimeSpan>(value, oldValue));
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            SetPropertySafe("mute", () => Handle.SetPropertyFlag("mute", value));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            double newValue = Math.Clamp(value, 0, 1);
            if (Math.Abs(_volume - newValue) <= 0.0001) return;
            _volume = newValue;
            SetPropertySafe("volume", () => Handle.SetPropertyDouble("volume", newValue * 100));
        }
    }

    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            if (Math.Abs(_playbackRate - value) <= 0.0001) return;
            double oldValue = _playbackRate;
            _playbackRate = value;
            SetPropertySafe("speed", () => Handle.SetPropertyDouble("speed", value));
            PlaybackRateChanged?.Invoke(this, new ValueChangedEventArgs<double>(value, oldValue));
        }
    }

    /// <summary>Subtitle timing offset in milliseconds（mpv <c>sub-delay</c> 秒换算）。</summary>
    public double SubtitleDelay
    {
        get => _subtitleDelay;
        set
        {
            if (Math.Abs(_subtitleDelay - value) <= 0.0001) return;
            double oldValue = _subtitleDelay;
            _subtitleDelay = value;
            SetPropertySafe("sub-delay", () => Handle.SetPropertyDouble("sub-delay", value / 1000.0));
            SubtitleDelayChanged?.Invoke(this, new ValueChangedEventArgs<double>(value, oldValue));
        }
    }

    /// <summary>Audio timing offset in milliseconds（mpv <c>audio-delay</c> 秒换算）。</summary>
    public double AudioDelay
    {
        get => _audioDelay;
        set
        {
            if (Math.Abs(_audioDelay - value) <= 0.0001) return;
            double oldValue = _audioDelay;
            _audioDelay = value;
            SetPropertySafe("audio-delay", () => Handle.SetPropertyDouble("audio-delay", value / 1000.0));
            AudioDelayChanged?.Invoke(this, new ValueChangedEventArgs<double>(value, oldValue));
        }
    }

    public Rect NormalizedSourceRect
    {
        get => _normalizedSourceRect;
        set
        {
            _normalizedSourceRect = value;
            ApplyCrop();
        }
    }

    public DeviceInformation? AudioDevice
    {
        get => null;    // TODO: Implement AudioDevice getter
        set
        {
            // mpv wasapi 设备名即 endpoint id，与 DeviceInformation.Id 一致（SPEC §6.3）。
            // null 时回退自动跟随默认设备。
            string deviceId = value?.Id ?? "auto";
            SetPropertySafe("audio-device", () => Handle.SetPropertyString("audio-device", deviceId));
        }
    }

    public MediaPlaybackState PlaybackState
    {
        get => _playbackState;
        private set
        {
            if (value == _playbackState) return;
            MediaPlaybackState oldValue = _playbackState;
            _playbackState = value;
            PlaybackStateChanged?.Invoke(this, new ValueChangedEventArgs<MediaPlaybackState>(value, oldValue));
        }
    }

    public PlaybackItem? PlaybackItem
    {
        get => _playbackItem;
        set
        {
            if (_playbackItem == value) return;
            PlaybackItem? oldValue = _playbackItem;
            if (value == null)
            {
                if (_playbackItem != null) RemoveItemHandlers(_playbackItem);
                _playbackItem = null;
                _loaded = false;
                _ended = false;
                ChaptersSnapshot = Array.Empty<MpvNodeValue>();
                try
                {
                    Handle.SetPropertyString("loop-file", "no");
                    Handle.Command("stop");
                }
                catch (Exception e) when (e is MpvException or ObjectDisposedException)
                {
                    LogCommandError(_logger, e);
                }
            }
            else
            {
                _playbackItem = value;
                _readyToPlay = true;
                _ended = false;
                _tracksReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RegisterItemHandlers(_playbackItem);
            }

            PlaybackItemChanged?.Invoke(this, new ValueChangedEventArgs<PlaybackItem?>(value, oldValue));
        }
    }

    public bool CanSeek { get; private set; }

    public bool IsLoopingEnabled
    {
        get => _isLoopingEnabled;
        set
        {
            if (_isLoopingEnabled == value) return;
            _isLoopingEnabled = value;
            // mpv 原生无缝循环；循环时 mpv 不抛 EOF（无 MediaEnded），自动续播
            SetPropertySafe("loop-file", () => Handle.SetPropertyString("loop-file", value ? "inf" : "no"));
        }
    }

    public double BufferingProgress { get; private set; }

    public uint NaturalVideoHeight { get; private set; }

    public uint NaturalVideoWidth { get; private set; }

    public bool CanPause { get; private set; } = true;

    public MpvHandle Handle { get; }

    /// <summary>当前 mpv <c>chapter-list</c> 快照（空数组表示无章节或未加载）。</summary>
    internal IReadOnlyList<MpvNodeValue> ChaptersSnapshot { get; private set; } = Array.Empty<MpvNodeValue>();

    /// <summary>当前媒体轨道列表就绪（FileLoaded/track-list 首次填充）或失败时完成。</summary>
    internal Task TracksReady => _tracksReady.Task;

    private readonly ILogger _logger;
    private readonly Rect _defaultSourceRect;
    private ChapterCue? _chapter;
    private Rect _normalizedSourceRect;
    private TaskCompletionSource _tracksReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _naturalDuration;
    private TimeSpan _position;
    private MediaPlaybackState _playbackState;
    private PlaybackItem? _playbackItem;
    private double _volume = 1.0;
    private double _playbackRate = 1.0;
    private double _subtitleDelay;
    private double _audioDelay;
    private bool _isMuted;
    private bool _isLoopingEnabled;
    private bool _readyToPlay;
    private bool _loaded;
    private bool _ended;
    private bool _disposed;

    internal MpvMediaPlayer(MpvHandle handle, ILogger logger)
    {
        Handle = handle;
        _logger = logger;
        _defaultSourceRect = new Rect(0, 0, 1, 1);
        _normalizedSourceRect = _defaultSourceRect;

        handle.PropertyChanged += OnPropertyChanged;
        handle.FileLoaded += OnFileLoaded;
        handle.EndFile += OnEndFile;
        handle.StartFile += OnStartFile;

        ObserveSafe("time-pos", MpvFormat.Double);
        ObserveSafe("duration", MpvFormat.Double);
        ObserveSafe("pause", MpvFormat.Flag);
        ObserveSafe("eof-reached", MpvFormat.Flag);
        ObserveSafe("seekable", MpvFormat.Flag);
        ObserveSafe("can-pause", MpvFormat.Flag);
        ObserveSafe("cache-buffering-state", MpvFormat.Int64);
        ObserveSafe("video-params", MpvFormat.Node);
        ObserveSafe("chapter", MpvFormat.Int64);
        ObserveSafe("chapter-list", MpvFormat.Node);
        ObserveSafe("track-list", MpvFormat.Node);
        ObserveSafe("volume", MpvFormat.Double);
        ObserveSafe("mute", MpvFormat.Flag);
        ObserveSafe("speed", MpvFormat.Double);
        ObserveSafe("sid", MpvFormat.String);
    }

    public void Play()
    {
        if (PlaybackItem == null) return;
        try
        {
            if (_readyToPlay)
            {
                _readyToPlay = false;
                PlaybackSource source = PlaybackItem.Source;
                string url = source.PlayUrl;
                if (source.Options.Length > 0)
                {
                    Handle.CommandNode(ParseFileOptions(source.Options), "loadfile", url, "replace");
                }
                else
                {
                    Handle.Command("loadfile", url, "replace");
                }

                // loop-file 在 PlaybackItem=null 时被复位，加载新媒体后按设置恢复
                if (_isLoopingEnabled)
                    Handle.SetPropertyString("loop-file", "inf");
            }
            else
            {
                if (_ended)
                {
                    _ended = false;
                    Handle.Command("seek", "0", "absolute");
                }

                Handle.SetPropertyFlag("pause", false);
            }
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            LogCommandError(_logger, e);
        }
    }

    public void Pause()
    {
        if (PlaybackState != MediaPlaybackState.Playing) return;
        SetPropertySafe("pause", () => Handle.SetPropertyFlag("pause", true));
    }

    public void Close()
    {
        if (_disposed) return;
        _disposed = true;
        Handle.PropertyChanged -= OnPropertyChanged;
        Handle.FileLoaded -= OnFileLoaded;
        Handle.EndFile -= OnEndFile;
        Handle.StartFile -= OnStartFile;
        Handle.Dispose();
    }

    public void StepForwardOneFrame()
    {
        try
        {
            Handle.Command("frame-step");
            // time-pos observe 会自动补 PositionChanged
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            LogCommandError(_logger, e);
        }
    }

    public void StepBackwardOneFrame()
    {
        try
        {
            Handle.Command("frame-back-step");
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            LogCommandError(_logger, e);
        }
    }

    public void AddSubtitle(IStorageFile file, bool select = true)
    {
        if (PlaybackItem == null) return;
        string token = StorageApplicationPermissions.FutureAccessList.Add(file, "subtitle");
        try
        {
            Handle.Command("sub-add", $"screenbox://{token}", select ? "select" : "auto");
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            try
            {
                StorageApplicationPermissions.FutureAccessList.Remove(token);
            }
            catch (Exception)
            {
                // Best effort cleanup
            }

            LogCommandError(_logger, e);
        }
    }

    /// <summary>
    /// mpv <c>screenshot-to-file</c>（异步完成）：轮询目标文件就绪（≤2s），超时抛异常。
    /// </summary>
    public void SaveSnapshot(string filePath)
    {
        Handle.Command("screenshot-to-file", filePath, "video");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    return;
            }
            catch (IOException)
            {
                // 文件仍在写入，继续等待
            }

            Thread.Sleep(20);
        }

        throw new InvalidOperationException("mpv failed to save snapshot");
    }

    // ---- mpv 事件 ----

    private void OnStartFile(object? sender, MpvEventArgs e)
    {
        _loaded = false;
        _ended = false;
        _position = TimeSpan.Zero;
        NaturalDuration = TimeSpan.Zero;
        PlaybackState = MediaPlaybackState.Opening;
    }

    private void OnFileLoaded(object? sender, MpvFileLoadedEventArgs e)
    {
        _loaded = true;
        MediaOpened?.Invoke(this, EventArgs.Empty);

        // track-list/chapter-list 快照（observe 事件也会随后到达，二者幂等）
        try
        {
            MpvNodeValue trackList = Handle.GetPropertyNode("track-list");
            if (trackList.Kind == MpvNodeKind.Array)
                PopulateTracks(trackList.AsList);
        }
        catch (Exception ex) when (ex is MpvException or ObjectDisposedException)
        {
            LogCommandError(_logger, ex);
        }

        try
        {
            PlaybackState = Handle.GetPropertyFlag("pause") ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
        }
        catch (Exception ex) when (ex is MpvException or ObjectDisposedException)
        {
            PlaybackState = MediaPlaybackState.Playing;
        }

        _tracksReady.TrySetResult();
    }

    private void OnEndFile(object? sender, MpvEndFileEventArgs e)
    {
        _loaded = false;
        switch (e.Reason)
        {
            case MpvEndFileReason.Error:
                // Phase 2：http(s) 源可在此挂 401 检测钩子复用认证对话框
                PlaybackState = MediaPlaybackState.None;
                _tracksReady.TrySetResult();
                MediaFailed?.Invoke(this, EventArgs.Empty);
                break;
            case MpvEndFileReason.Eof:
                // keep-open=yes 时正常 EOF 不触发此分支（走 eof-reached）；防御性处理
                if (!_isLoopingEnabled)
                {
                    _ended = true;
                    PlaybackState = MediaPlaybackState.None;
                    MediaEnded?.Invoke(this, EventArgs.Empty);
                }

                break;
            default:
                // Stop / Quit / Redirect
                PlaybackState = MediaPlaybackState.None;
                _tracksReady.TrySetResult();
                break;
        }
    }

    // ---- 属性观察 ----

    private void OnPropertyChanged(object? sender, MpvPropertyChangedEventArgs e)
    {
        try
        {
            switch (e.Name)
            {
                case "time-pos":
                    if (e.Value is { Kind: MpvNodeKind.Double } timePos)
                        UpdatePosition(TimeSpan.FromSeconds(timePos.AsDouble));
                    break;
                case "duration":
                    NaturalDuration = e.Value is { Kind: MpvNodeKind.Double } duration
                        ? TimeSpan.FromSeconds(duration.AsDouble)
                        : TimeSpan.Zero;
                    break;
                case "pause":
                    if (e.Value is { Kind: MpvNodeKind.Flag } pause)
                        OnPauseChanged(pause.AsBoolean);
                    break;
                case "eof-reached":
                    if (e.Value is { Kind: MpvNodeKind.Flag } eof && eof.AsBoolean)
                        OnEofReached();
                    break;
                case "seekable":
                    if (e.Value is { Kind: MpvNodeKind.Flag } seekable)
                        UpdateCanSeek(seekable.AsBoolean);
                    break;
                case "can-pause":
                    if (e.Value is { Kind: MpvNodeKind.Flag } canPause)
                        CanPause = canPause.AsBoolean;
                    break;
                case "cache-buffering-state":
                    if (e.Value is { Kind: MpvNodeKind.Int64 } buffering)
                        OnBuffering(buffering.AsInt64);
                    break;
                case "video-params":
                    OnVideoParamsChanged(e.Value);
                    break;
                case "chapter":
                    OnChapterChanged(e.Value);
                    break;
                case "chapter-list":
                    if (e.Value is { Kind: MpvNodeKind.Array } chapterList)
                        UpdateChapters(chapterList.AsList);
                    break;
                case "track-list":
                    if (e.Value is { Kind: MpvNodeKind.Array } trackList)
                    {
                        PopulateTracks(trackList.AsList);
                        _tracksReady.TrySetResult();
                    }

                    break;
                case "volume":
                    if (e.Value is { Kind: MpvNodeKind.Double } volume)
                    {
                        double newVolume = volume.AsDouble / 100d;
                        if (Math.Abs(_volume - newVolume) > 0.0001)
                        {
                            _volume = newVolume;
                            VolumeChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    break;
                case "mute":
                    if (e.Value is { Kind: MpvNodeKind.Flag } muted && _isMuted != muted.AsBoolean)
                    {
                        _isMuted = muted.AsBoolean;
                        IsMutedChanged?.Invoke(this, EventArgs.Empty);
                    }

                    break;
                case "speed":
                    if (e.Value is { Kind: MpvNodeKind.Double } speed && Math.Abs(_playbackRate - speed.AsDouble) > 0.0001)
                    {
                        double oldRate = _playbackRate;
                        _playbackRate = speed.AsDouble;
                        PlaybackRateChanged?.Invoke(this, new ValueChangedEventArgs<double>(_playbackRate, oldRate));
                    }

                    break;
                case "sid":
                    OnSidChanged(e.Value);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogEventError(_logger, ex);
        }
    }

    private void UpdatePosition(TimeSpan newValue)
    {
        if (newValue == _position) return;
        TimeSpan oldValue = _position;
        _position = newValue;
        PositionChanged?.Invoke(this, new ValueChangedEventArgs<TimeSpan>(newValue, oldValue));
    }

    private void OnPauseChanged(bool paused)
    {
        if (!_loaded || _ended) return;
        PlaybackState = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
    }

    private void OnEofReached()
    {
        // keep-open=yes：EOF 停帧，由 C# 合成 Ended 语义（loop-file=inf 时不会到达这里）
        if (_ended || _isLoopingEnabled) return;
        _ended = true;
        PlaybackState = MediaPlaybackState.None;
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCanSeek(bool seekable)
    {
        if (CanSeek == seekable) return;
        CanSeek = seekable;
        CanSeekChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBuffering(long percent)
    {
        if (BufferingProgress == 0)
        {
            BufferingStarted?.Invoke(this, EventArgs.Empty);
        }

        BufferingProgress = percent / 100d;
        BufferingProgressChanged?.Invoke(this, EventArgs.Empty);
        if (BufferingProgress >= 1.0)
        {
            BufferingEnded?.Invoke(this, EventArgs.Empty);
            BufferingProgress = 0;
        }
    }

    private void OnVideoParamsChanged(MpvNodeValue? value)
    {
        uint width = 0, height = 0;
        if (value is { Kind: MpvNodeKind.Map } videoParams)
        {
            IReadOnlyDictionary<string, MpvNodeValue> map = videoParams.AsMap;
            width = (uint)(MediaTrack.GetInt64(map, "w") ?? 0);
            height = (uint)(MediaTrack.GetInt64(map, "h") ?? 0);
            // video-params/rotate 预留（按需后续支持 Rotation）
        }

        if (NaturalVideoWidth == width && NaturalVideoHeight == height) return;
        NaturalVideoWidth = width;
        NaturalVideoHeight = height;
        NaturalVideoSizeChanged?.Invoke(this, EventArgs.Empty);
        if (_normalizedSourceRect != _defaultSourceRect)
            ApplyCrop();
    }

    private void OnChapterChanged(MpvNodeValue? value)
    {
        if (value is not { Kind: MpvNodeKind.Int64 } chapter)
        {
            Chapter = null;
            return;
        }

        OnChapterChanged(chapter.AsInt64);
    }

    private void OnChapterChanged(long index)
    {
        if (PlaybackItem == null || index < 0 || index >= PlaybackItem.Chapters.Count)
        {
            Chapter = null;
            return;
        }

        Chapter = PlaybackItem.Chapters[(int)index];
    }

    private void OnSidChanged(MpvNodeValue? value)
    {
        if (PlaybackItem == null) return;
        switch (value)
        {
            case { Kind: MpvNodeKind.String } sid when long.TryParse(sid.AsString, out long id):
                PlaybackItem.SubtitleTracks.SelectMpvSid(id);
                break;
            case { Kind: MpvNodeKind.String }:
            case null:
                // "no" / 不可用 → 取消选择
                PlaybackItem.SubtitleTracks.SelectMpvSid(-1);
                break;
        }
    }

    private void PopulateTracks(IReadOnlyList<MpvNodeValue> trackList)
    {
        PlaybackItem?.PopulateTracks(trackList);
    }

    private void UpdateChapters(IReadOnlyList<MpvNodeValue> chapterList)
    {
        ChaptersSnapshot = chapterList;
        if (PlaybackItem != null)
        {
            PlaybackItem.Chapters.Load(chapterList, NaturalDuration);
            // 章节列表变化后同步当前章节
            try
            {
                OnChapterChanged(Handle.GetPropertyInt64("chapter"));
            }
            catch (Exception ex) when (ex is MpvException or ObjectDisposedException)
            {
                Chapter = null;
            }
        }

        ChapterListChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 轨道选择 ----

    private void RemoveItemHandlers(PlaybackItem item)
    {
        item.SubtitleTracks.SelectedIndexChanged -= SubtitleTracksOnSelectedIndexChanged;
        item.AudioTracks.SelectedIndexChanged -= AudioTracksOnSelectedIndexChanged;
        item.VideoTracks.SelectedIndexChanged -= VideoTracksOnSelectedIndexChanged;
    }

    private void RegisterItemHandlers(PlaybackItem item)
    {
        RemoveItemHandlers(item);
        item.SubtitleTracks.SelectedIndexChanged += SubtitleTracksOnSelectedIndexChanged;
        item.AudioTracks.SelectedIndexChanged += AudioTracksOnSelectedIndexChanged;
        item.VideoTracks.SelectedIndexChanged += VideoTracksOnSelectedIndexChanged;
    }

    private void AudioTracksOnSelectedIndexChanged(ISingleSelectMediaTrackList sender, object? args)
    {
        PlaybackAudioTrackList trackList = (PlaybackAudioTrackList)sender;
        SetTrackId("aid", sender.SelectedIndex < 0 ? null : trackList[sender.SelectedIndex].MpvTrackId);
    }

    private void VideoTracksOnSelectedIndexChanged(ISingleSelectMediaTrackList sender, object? args)
    {
        PlaybackVideoTrackList trackList = (PlaybackVideoTrackList)sender;
        SetTrackId("vid", sender.SelectedIndex < 0 ? null : trackList[sender.SelectedIndex].MpvTrackId);
    }

    private void SubtitleTracksOnSelectedIndexChanged(ISingleSelectMediaTrackList sender, object? args)
    {
        PlaybackSubtitleTrackList trackList = (PlaybackSubtitleTrackList)sender;
        if (sender.SelectedIndex < 0) SetTrackId("sid", null);
        else if (trackList[sender.SelectedIndex].MpvTrackId >= 0)   // MpvTrackId < 0 on lazy load
            SetTrackId("sid", trackList[sender.SelectedIndex].MpvTrackId);
    }

    private void SetTrackId(string property, long? trackId)
    {
        SetPropertySafe(property, () =>
            Handle.SetPropertyString(property, trackId?.ToString() ?? "no"));
    }

    // ---- 裁剪 ----

    private void ApplyCrop()
    {
        SetPropertySafe("video-crop", () =>
        {
            if (_normalizedSourceRect == _defaultSourceRect || NaturalVideoWidth == 0 || NaturalVideoHeight == 0)
            {
                // 空串重置为全帧（SPEC §6.3）
                Handle.SetPropertyString("video-crop", "");
            }
            else
            {
                double width = _normalizedSourceRect.Width * NaturalVideoWidth;
                double height = _normalizedSourceRect.Height * NaturalVideoHeight;
                double x = _normalizedSourceRect.X * NaturalVideoWidth;
                double y = _normalizedSourceRect.Y * NaturalVideoHeight;
                Handle.SetPropertyString("video-crop", $"{width:F0}x{height:F0}+{x:F0}+{y:F0}");
            }
        });
    }

    // ---- 工具 ----

    private static Dictionary<string, string> ParseFileOptions(string[] options)
    {
        // 兼容 VLC 风格 ":key=value" 与 mpv 风格 "--key=value"/"key=value"
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string raw in options)
        {
            string option = raw.TrimStart(':');
            if (option.StartsWith("--", StringComparison.Ordinal)) option = option.Substring(2);
            int separatorIndex = option.IndexOf('=');
            if (separatorIndex < 0)
                map[option] = "yes";
            else
                map[option.Substring(0, separatorIndex)] = option.Substring(separatorIndex + 1);
        }

        return map;
    }

    private void ObserveSafe(string name, MpvFormat format)
    {
        try
        {
            Handle.ObserveProperty(name, format);
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            LogCommandError(_logger, e);
        }
    }

    private void SetPropertySafe(string name, Action set)
    {
        try
        {
            set();
        }
        catch (Exception e) when (e is MpvException or ObjectDisposedException)
        {
            LogPropertyError(_logger, name, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "mpv command failed")]
    private static partial void LogCommandError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set mpv property {Name}")]
    private static partial void LogPropertyError(ILogger logger, string name, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to seek mpv player")]
    private static partial void LogSeekError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling mpv property change")]
    private static partial void LogEventError(ILogger logger, Exception exception);
}
