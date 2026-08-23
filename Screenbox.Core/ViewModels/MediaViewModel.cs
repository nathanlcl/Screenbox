using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Screenbox.Core.Contexts;
using Screenbox.Core.Enums;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace Screenbox.Core.ViewModels;

public sealed partial class MediaViewModel : ObservableRecipient
{
    public string Location { get; }

    public object Source { get; private set; }

    public bool IsFromLibrary { get; set; }

    public bool DetailsLoaded { get; private set; }

    public ArtistViewModel? MainArtist => Artists.FirstOrDefault();

    public Lazy<PlaybackItem?> Item { get; internal set; }

    public IReadOnlyList<string> Options { get; }

    public DateTimeOffset DateAdded { get; set; }

    public MediaPlaybackType MediaType => MediaInfo.MediaType;

    public TimeSpan Duration => MediaInfo.MusicProperties.Duration > TimeSpan.Zero
        ? MediaInfo.MusicProperties.Duration
        : MediaInfo.VideoProperties.Duration;

    public string TrackNumberText =>
        MediaInfo.MusicProperties.TrackNumber > 0 ? MediaInfo.MusicProperties.TrackNumber.ToString() : string.Empty;    // Helper for binding

    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnailRef == null) return null;
            return _thumbnailRef.TryGetTarget(out BitmapImage? image) ? image : null;
        }
        set
        {
            if (_thumbnailRef == null && value == null) return;
            if ((_thumbnailRef?.TryGetTarget(out BitmapImage? image) ?? false) && image == value) return;
            SetProperty(ref _thumbnailRef, value == null ? null : new WeakReference<BitmapImage>(value));
        }
    }

    private IMediaPlayer? MediaPlayer => _playerContext.MediaPlayer;

    private readonly IPlayerService _playerService;
    private readonly PlayerContext _playerContext;
    private readonly List<string> _options;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsMediaActive { get; set; }
    [ObservableProperty] public partial bool IsAvailable { get; set; } = true;
    [ObservableProperty] public partial AlbumViewModel? Album { get; set; }
    [ObservableProperty] public partial string Caption { get; set; } = string.Empty;  // For list item subtitle
    [ObservableProperty] public partial string AltCaption { get; set; } = string.Empty;   // For player page subtitle

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackNumberText))]
    public partial MediaInfo MediaInfo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainArtist))]
    public partial ArtistViewModel[] Artists { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    private WeakReference<BitmapImage>? _thumbnailRef;
    private Task<MpvProbeResult?>? _probeTask;  // 远程 URI 源的元数据探测（备忘复用）

    public MediaViewModel(MediaViewModel source)
    {
        _playerService = source._playerService;
        _playerContext = source._playerContext;
        Name = source.Name;
        _thumbnailRef = source._thumbnailRef;
        MediaInfo = source.MediaInfo;
        Artists = source.Artists;
        Album = source.Album;
        Caption = source.Caption;
        AltCaption = source.AltCaption;
        _options = new List<string>(source.Options);
        Options = new ReadOnlyCollection<string>(_options);
        Location = source.Location;
        Source = source.Source;
        Item = new Lazy<PlaybackItem?>(CreatePlaybackItem);
        DateAdded = source.DateAdded;
        IsFromLibrary = source.IsFromLibrary;
        DetailsLoaded = source.DetailsLoaded;
    }

    private MediaViewModel(object source, MediaInfo mediaInfo, PlayerContext playerContext, IPlayerService playerService)
    {
        _playerService = playerService;
        _playerContext = playerContext;
        Source = source;
        Location = string.Empty;
        DateAdded = DateTimeOffset.Now;
        Name = string.Empty;
        MediaInfo = mediaInfo;
        Artists = Array.Empty<ArtistViewModel>();
        _options = new List<string>();
        Options = new ReadOnlyCollection<string>(_options);
        Item = new Lazy<PlaybackItem?>(CreatePlaybackItem);
    }

    public MediaViewModel(PlayerContext playerContext, IPlayerService playerService, StorageFile file)
        : this(file, new MediaInfo(FilesHelpers.GetMediaTypeForFile(file)), playerContext, playerService)
    {
        Location = file.Path;
        Name = file.Name;
        AltCaption = file.Name;
    }

    public MediaViewModel(PlayerContext playerContext, IPlayerService playerService, Uri uri)
        : this(uri, new MediaInfo(MediaPlaybackType.Unknown), playerContext, playerService)
    {
        Guard.IsTrue(uri.IsAbsoluteUri);
        Location = uri.OriginalString;
        Name = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments.Last()) : string.Empty;
    }

    partial void OnMediaInfoChanged(MediaInfo value)
    {
        UpdateCaptions();
    }

    private PlaybackItem? CreatePlaybackItem()
    {
        if (MediaPlayer == null)
        {
            Messenger.Send(new MediaLoadFailedNotificationMessage("Media player is not initialized", Location));
            return null;
        }

        PlaybackItem? item = null;
        try
        {
            item = _playerService.CreatePlaybackItem(MediaPlayer, Source, _options.ToArray());
        }
        catch (ArgumentOutOfRangeException)
        {
            // Coding error. Rethrow.
            throw;
        }
        catch (Exception e)
        {
            // 附带异常与源类型，便于定位路径解析失败的链路（如 Windows 方括号路径问题）
            string reason = $"{e.GetType().Name}: {e.Message} (Source: {Source.GetType().Name})";
            Messenger.Send(new MediaLoadFailedNotificationMessage(reason, Location));
        }

        return item;
    }

    public override string ToString()
    {
        return $"{Name}; {Caption}";
    }

    public void SetOptions(string options)
    {
        // mpv 选项语法：--k=v（SPEC §6.3）
        string[] opts = options.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(o => o.StartsWith("--") && o.Length > 2).ToArray();

        // Check if new options and existing options are the same
        if (opts.Length == _options.Count)
        {
            bool same = !opts.Where((o, i) => o != _options[i]).Any();
            if (same) return;
        }

        _options.Clear();
        _options.AddRange(opts);

        if (!Item.IsValueCreated) return;
        Clean();
    }

    public void Clean()
    {
        if (!Item.IsValueCreated) return;
        PlaybackItem? item = Item.Value;
        Item = new Lazy<PlaybackItem?>(CreatePlaybackItem);
        if (item == null) return;
        _playerService.DisposePlaybackItem(item);
    }

    public void UpdateSource(StorageFile file)
    {
        Source = file;
        AltCaption = file.Name;
    }

    public async Task LoadDetailsAsync(IFilesService filesService)
    {
        DetailsLoaded = true;
        switch (Source)
        {
            case StorageFile file:
                MediaInfo = await filesService.GetMediaInfoAsync(file);
                break;
            case Uri uri when await TryGetStorageFileFromUri(uri) is { } uriFile:
                UpdateSource(uriFile);
                MediaInfo = await filesService.GetMediaInfoAsync(uriFile);
                break;
            case Uri remoteUri:
                // 远程 URI 源：用 mpv 探测实例取元数据（SPEC §D5）
                if (await ProbeSourceAsync(remoteUri) is { } probe)
                    ApplyProbeResult(probe);
                break;
        }

        switch (MediaType)
        {
            case MediaPlaybackType.Music when !string.IsNullOrEmpty(MediaInfo.MusicProperties.Title):
                Name = MediaInfo.MusicProperties.Title;
                break;
            case MediaPlaybackType.Video when !string.IsNullOrEmpty(MediaInfo.VideoProperties.Title):
                Name = MediaInfo.VideoProperties.Title;
                break;
        }

        if (Name == AltCaption)
            AltCaption = string.Empty;
    }

    /// <summary>
    /// Applies mpv probe metadata to this view model (SPEC §D5 元数据键映射表).
    /// 探测失败/超时时不调用本方法，保持文件名兜底展示。
    /// </summary>
    private void ApplyProbeResult(MpvProbeResult probe)
    {
        // 类型推断：无（非封面）视频轨 → Music。替代原 ParsedStatus + VideoTracks.Count 判断。
        if (MediaType == MediaPlaybackType.Unknown && !probe.HasVideoStream)
            MediaInfo.MediaType = MediaPlaybackType.Music;

        if (probe.Title is { } title &&
            !string.IsNullOrEmpty(title) &&
            !Guid.TryParse(title, out Guid _))
        {
            Name = title;
        }

        VideoInfo videoProperties = MediaInfo.VideoProperties;
        videoProperties.ShowName = probe.ShowName ?? videoProperties.ShowName;
        videoProperties.Season = probe.Season ?? videoProperties.Season;
        videoProperties.Episode = probe.Episode ?? videoProperties.Episode;

        // 写入 MusicInfo 后由 UpdateCaptions 统一生成 Caption/AltCaption
        MusicInfo musicProperties = MediaInfo.MusicProperties;
        musicProperties.Artist = probe.Artist ?? musicProperties.Artist;
        musicProperties.Album = probe.Album ?? musicProperties.Album;

        if (probe.Duration is { } duration && Duration <= TimeSpan.Zero)
        {
            musicProperties.Duration = duration;
            videoProperties.Duration = duration;
        }

        UpdateCaptions();
    }

    /// <summary>
    /// Probes a remote URI source with the shared mpv probe instance. The probe task is
    /// memoized so repeated detail/caption loads reuse the same result.
    /// </summary>
    private Task<MpvProbeResult?> ProbeSourceAsync(Uri uri)
    {
        return _probeTask ??= MpvMediaProbe.Shared.ProbeAsync(uri);
    }

    public async Task LoadThumbnailAsync()
    {
        if (Thumbnail != null) return;
        if (Source is Uri uri && await TryGetStorageFileFromUri(uri) is { } storageFile)
        {
            UpdateSource(storageFile);
        }

        if (Source is StorageFile file)
        {
            using var source = await GetThumbnailSourceAsync(file);
            if (source == null) return;
            BitmapImage image = new()
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelHeight = 300
            };

            try
            {
                await image.SetSourceAsync(source);
            }
            catch (Exception)
            {
                // WinRT component not found exception???
                return;
            }

            Thumbnail = image;
        }

        // 远程 URI 源无缩略图：mpv metadata 无封面 URL（SPEC §D5 ArtworkURL 降级为 null）
    }

    public Task<IRandomAccessStream?> GetThumbnailSourceAsync()
    {
        return Source is not StorageFile file
            ? Task.FromResult<IRandomAccessStream?>(null)
            : GetThumbnailSourceAsync(file);
    }

    private static async Task<IRandomAccessStream?> GetThumbnailSourceAsync(StorageFile file)
    {
        if (!file.IsAvailable)
            return null;

        try
        {
            // Use SingleItem mode to retrieve embedded album art with original aspect ratio.
            // https://learn.microsoft.com/windows/apps/develop/files/thumbnails
            StorageItemThumbnail? source =
                await file.GetThumbnailAsync(ThumbnailMode.SingleItem, requestedSize: 1280, ThumbnailOptions.UseCurrentScale);
            if (source is { Type: ThumbnailType.Image })
            {
                return source;
            }
        }
        catch (Exception)
        {
            //// System.Exception: The data necessary to complete this operation is not yet available.
            //if (e.HResult != unchecked((int)0x8000000A) &&
            //    // System.Exception: The RPC server is unavailable.
            //    e.HResult != unchecked((int)0x800706BA))
            //    _logger.LogError(e, "Failed to load the image thumbnail.");
        }

        return null;
    }

    private void UpdateCaptions()
    {
        if (Duration > TimeSpan.Zero)
        {
            Caption = Humanizer.ToDuration(Duration);
        }

        MusicInfo musicProperties = MediaInfo.MusicProperties;
        if (!string.IsNullOrEmpty(musicProperties.Artist))
        {
            Caption = musicProperties.Artist;
            AltCaption = string.IsNullOrEmpty(musicProperties.Album)
                ? musicProperties.Artist
                : $"{musicProperties.Artist} – {musicProperties.Album}";
        }
        else if (!string.IsNullOrEmpty(musicProperties.Album))
        {
            AltCaption = musicProperties.Album;
        }
    }

    private static async Task<StorageFile?> TryGetStorageFileFromUri(Uri uri)
    {
        if (uri is { IsFile: true, IsLoopback: true, IsAbsoluteUri: true })
        {
            // 用 LocalPath 而非 OriginalString：后者可能是百分号转义的 file:/// 形式
            return await FilesHelpers.TryGetFileFromPathAsync(uri.LocalPath);
        }

        return null;
    }
}
