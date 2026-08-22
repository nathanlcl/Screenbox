using System;
using Screenbox.Core.Events;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Screenbox.Core.Playback;

public interface IMediaPlayer
{
    event TypedEventHandler<IMediaPlayer, EventArgs>? MediaEnded;
    event TypedEventHandler<IMediaPlayer, EventArgs>? MediaFailed;
    event TypedEventHandler<IMediaPlayer, EventArgs>? MediaOpened;
    event TypedEventHandler<IMediaPlayer, EventArgs>? IsMutedChanged;
    event TypedEventHandler<IMediaPlayer, EventArgs>? VolumeChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<PlaybackItem?>>? PlaybackItemChanged;
    event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingProgressChanged;
    event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingStarted;
    event TypedEventHandler<IMediaPlayer, EventArgs>? BufferingEnded;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? NaturalDurationChanged;
    event TypedEventHandler<IMediaPlayer, EventArgs>? NaturalVideoSizeChanged;
    event TypedEventHandler<IMediaPlayer, EventArgs>? CanSeekChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<TimeSpan>>? PositionChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<ChapterCue?>>? ChapterChanged;

    /// <summary>Raised when the chapter list of the current playback item changes (e.g. mpv <c>chapter-list</c> update).</summary>
    event EventHandler? ChapterListChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<MediaPlaybackState>>? PlaybackStateChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? PlaybackRateChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? SubtitleDelayChanged;
    event TypedEventHandler<IMediaPlayer, ValueChangedEventArgs<double>>? AudioDelayChanged;

    bool CanPause { get; }
    bool CanSeek { get; }
    bool IsMuted { get; set; }
    bool IsLoopingEnabled { get; set; }
    DeviceInformation? AudioDevice { get; set; }
    MediaPlaybackState PlaybackState { get; }
    double BufferingProgress { get; }
    uint NaturalVideoHeight { get; }
    uint NaturalVideoWidth { get; }
    TimeSpan Position { get; set; }
    TimeSpan NaturalDuration { get; }
    ChapterCue? Chapter { get; }
    double PlaybackRate { get; set; }

    /// <summary>Subtitle timing offset in milliseconds.</summary>
    double SubtitleDelay { get; set; }

    /// <summary>Audio timing offset in milliseconds.</summary>
    double AudioDelay { get; set; }
    Rect NormalizedSourceRect { get; set; }
    double Volume { get; set; }
    public PlaybackItem? PlaybackItem { get; set; }

    void Close();
    void Play();
    void Pause();
    void StepForwardOneFrame();
    void StepBackwardOneFrame();
    void AddSubtitle(IStorageFile file, bool select = true);

    /// <summary>
    /// Saves a snapshot of the current video frame to <paramref name="filePath"/>.
    /// Throws on failure or timeout.
    /// </summary>
    void SaveSnapshot(string filePath);
}
