using System;
using System.Collections.Generic;
using System.Linq;
using Screenbox.Mpv;
using Windows.Media.Core;
using Windows.Storage;

namespace Screenbox.Core.Playback;

public sealed partial class PlaybackSubtitleTrackList : SingleSelectTrackList<SubtitleTrack>
{
    private readonly List<LazySubtitleTrack> _pendingSubtitleTracks;

    private class LazySubtitleTrack
    {
        public SubtitleTrack Track { get; }

        public StorageFile File { get; }

        public IMediaPlayer Player { get; }

        public LazySubtitleTrack(IMediaPlayer player, StorageFile file)
        {
            Player = player;
            File = file;
            Track = new SubtitleTrack
            {
                Id = "-1",
                MpvTrackId = -1,
                Label = file.Name,
            };
        }
    }

    private long _delaySid = -1;
    private bool _hasPopulated;

    internal PlaybackSubtitleTrackList()
    {
        _pendingSubtitleTracks = new List<LazySubtitleTrack>();
        SelectedIndexChanged += OnSelectedIndexChanged;
    }

    /// <summary>
    /// Rebuilds the embedded subtitle tracks from an mpv <c>track-list</c> snapshot,
    /// keeping lazy (external, not yet loaded) placeholders appended at the end.
    /// Newly appeared sub track ids after the first populate are used to backfill a
    /// pending lazy track.
    /// </summary>
    internal void Populate(IReadOnlyList<MpvNodeValue> trackList)
    {
        HashSet<long> oldIds = new(TrackList.Select(t => t.MpvTrackId).Where(id => id >= 0));
        List<long> newIds = new();
        List<SubtitleTrack> realTracks = new();
        long selectedSid = -1;
        foreach (MpvNodeValue node in trackList)
        {
            if (node.Kind != MpvNodeKind.Map || MediaTrack.GetTrackType(node.AsMap) != "sub") continue;
            SubtitleTrack track = new(node);
            if (track.MpvSelected) selectedSid = track.MpvTrackId;
            if (track.MpvTrackId >= 0 && !oldIds.Contains(track.MpvTrackId)) newIds.Add(track.MpvTrackId);
            realTracks.Add(track);
        }

        // Preserve the currently selected lazy placeholder across the rebuild.
        SubtitleTrack? selectedLazy = SelectedIndex >= 0 && SelectedIndex < Count &&
            _pendingSubtitleTracks.FirstOrDefault(x => ReferenceEquals(x.Track, this[SelectedIndex])) is { } selected
            ? selected.Track
            : null;

        TrackList.Clear();
        TrackList.AddRange(realTracks);
        foreach (LazySubtitleTrack lazy in _pendingSubtitleTracks)
        {
            TrackList.Add(lazy.Track);
        }

        if (_hasPopulated && newIds.Count > 0)
        {
            // A sub-add we initiated surfaced in the track list: backfill the first pending
            // lazy track (preferring the selected one) with the new mpv track id.
            LazySubtitleTrack? target = _pendingSubtitleTracks.FirstOrDefault(x => x.Track.MpvTrackId == -1 && ReferenceEquals(x.Track, selectedLazy))
                ?? _pendingSubtitleTracks.FirstOrDefault(x => x.Track.MpvTrackId == -1);
            if (target != null)
            {
                target.Track.MpvTrackId = newIds[0];
                target.Track.Id = newIds[0].ToString();
            }
        }

        _hasPopulated = true;

        if (_delaySid >= 0)
        {
            long sid = _delaySid;
            _delaySid = -1;
            SelectMpvSid(sid);
            return;
        }

        int selectedIndex = selectedSid >= 0 ? FindIndexById(selectedSid) : -1;
        if (selectedIndex >= 0)
        {
            SelectedIndex = selectedIndex;
        }
        else if (selectedLazy != null)
        {
            SelectedIndex = TrackList.IndexOf(selectedLazy);
        }
        else if (SelectedIndex >= Count)
        {
            SelectedIndex = -1;
        }
    }

    /// <summary>mpv 自动选轨同步（observe sid）。</summary>
    internal void SelectMpvSid(long sid)
    {
        if (sid < 0)
        {
            SelectedIndex = -1;
            return;
        }

        // Sid may be set before tracks are populated. Delay select.
        if (Count == 0)
        {
            _delaySid = sid;
            return;
        }

        int index = FindIndexById(sid);
        if (index >= 0)
        {
            SelectedIndex = index;
        }
    }

    private void OnSelectedIndexChanged(ISingleSelectMediaTrackList sender, object? args)
    {
        if (SelectedIndex >= 0 && TrackList[SelectedIndex] is { } selectedTrack &&
            _pendingSubtitleTracks.FirstOrDefault(x => ReferenceEquals(x.Track, selectedTrack)) is { } lazyTrack &&
            (selectedTrack.MpvTrackId == -1 ||
             !TrackList.Any(t => !ReferenceEquals(t, selectedTrack) && t.MpvTrackId == selectedTrack.MpvTrackId)))
        {
            selectedTrack.MpvTrackId = -1;
            lazyTrack.Player.AddSubtitle(lazyTrack.File, true);
        }
    }

    public void AddExternalSubtitle(IMediaPlayer player, StorageFile file, bool select)
    {
        string filePath = file.Path;

        // Check if the subtitle track already exists in the pending list
        var existing = _pendingSubtitleTracks.FirstOrDefault(x =>
            !string.IsNullOrEmpty(filePath) &&
            string.Equals(x.File.Path, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (select)
            {
                int existingIndex = TrackList.FindIndex(t => ReferenceEquals(t, existing.Track));
                if (existingIndex >= 0)
                {
                    SelectedIndex = existingIndex;
                }
            }

            return;
        }

        var lazySub = new LazySubtitleTrack(player, file);
        _pendingSubtitleTracks.Add(lazySub);
        TrackList.Add(lazySub.Track);

        if (select)
        {
            SelectedIndex = TrackList.Count - 1;
        }
    }

    private int FindIndexById(long mpvTrackId)
    {
        for (int i = 0; i < TrackList.Count; i++)
        {
            if (TrackList[i].MpvTrackId == mpvTrackId) return i;
        }

        return -1;
    }
}
