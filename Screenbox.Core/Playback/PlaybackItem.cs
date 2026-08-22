using System;
using System.Collections.Generic;
using Screenbox.Mpv;

namespace Screenbox.Core.Playback;

public class PlaybackItem
{
    internal PlaybackSource Source { get; }

    public object OriginalSource { get; }

    public bool IsDisabledInPlaybackList { get; set; }

    public PlaybackAudioTrackList AudioTracks { get; }

    public PlaybackVideoTrackList VideoTracks { get; }

    public PlaybackSubtitleTrackList SubtitleTracks { get; }

    public PlaybackChapterList Chapters { get; }

    /// <summary>
    /// Media duration. Backfilled by <see cref="MpvMediaPlayer"/> once mpv reports the
    /// <c>duration</c> property (or by the metadata probe / Windows API paths).
    /// </summary>
    public TimeSpan? Duration { get; internal set; }

    internal PlaybackItem(object source, PlaybackSource playbackSource)
    {
        OriginalSource = source;
        Source = playbackSource;
        AudioTracks = new PlaybackAudioTrackList();
        VideoTracks = new PlaybackVideoTrackList();
        SubtitleTracks = new PlaybackSubtitleTrackList();
        Chapters = new PlaybackChapterList(this);
    }

    /// <summary>
    /// Populates the track lists from an mpv <c>track-list</c> node snapshot
    /// (invoked by <see cref="MpvMediaPlayer"/> on file load and on track-list changes).
    /// </summary>
    internal void PopulateTracks(IReadOnlyList<MpvNodeValue> trackList)
    {
        AudioTracks.Populate(trackList);
        VideoTracks.Populate(trackList);
        SubtitleTracks.Populate(trackList);
    }
}
