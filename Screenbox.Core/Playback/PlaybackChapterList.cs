using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Screenbox.Mpv;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class PlaybackChapterList : ReadOnlyCollection<ChapterCue>
{
    private readonly List<ChapterCue> _chapters;
    private readonly PlaybackItem _item;

    internal PlaybackChapterList(PlaybackItem item) : base(new List<ChapterCue>())
    {
        _item = item;
        _chapters = (List<ChapterCue>)Items;
    }

    public void Load(IMediaPlayer player)
    {
        if (player is not MpvMediaPlayer mpvPlayer || player.PlaybackItem != _item)
            return;

        Load(mpvPlayer.ChaptersSnapshot, mpvPlayer.NaturalDuration);
        mpvPlayer.Chapter = _chapters.FirstOrDefault();
    }

    /// <summary>
    /// Rebuilds from an mpv <c>chapter-list</c> node array (keys: <c>title</c>, <c>time</c> in seconds).
    /// mpv chapters carry no duration; it is derived from the next chapter's start time
    /// (the last chapter uses the media duration).
    /// </summary>
    internal void Load(IReadOnlyList<MpvNodeValue> chapterList, TimeSpan duration)
    {
        _chapters.Clear();
        for (int i = 0; i < chapterList.Count; i++)
        {
            MpvNodeValue node = chapterList[i];
            if (node.Kind != MpvNodeKind.Map) continue;
            IReadOnlyDictionary<string, MpvNodeValue> map = node.AsMap;
            double time = MediaTrack.GetDouble(map, "time") ?? 0;
            TimeSpan startTime = TimeSpan.FromSeconds(time);
            TimeSpan chapterDuration;
            if (i + 1 < chapterList.Count &&
                chapterList[i + 1] is { Kind: MpvNodeKind.Map } next &&
                MediaTrack.GetDouble(next.AsMap, "time") is { } nextTime)
            {
                chapterDuration = TimeSpan.FromSeconds(Math.Max(0, nextTime - time));
            }
            else
            {
                chapterDuration = duration > startTime ? duration - startTime : TimeSpan.Zero;
            }

            _chapters.Add(new ChapterCue
            {
                Title = MediaTrack.GetString(map, "title") ?? string.Empty,
                Duration = chapterDuration,
                StartTime = startTime
            });
        }
    }
}
