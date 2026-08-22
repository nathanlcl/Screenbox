using System.Collections.Generic;
using Screenbox.Mpv;

namespace Screenbox.Core.Playback;

public sealed partial class PlaybackAudioTrackList : SingleSelectTrackList<AudioTrack>
{
    internal PlaybackAudioTrackList()
    {
    }

    /// <summary>
    /// Rebuilds the list from an mpv <c>track-list</c> snapshot. Selection follows the
    /// track with <c>selected=true</c>; falls back to the previously selected track id,
    /// then to the first track.
    /// </summary>
    internal void Populate(IReadOnlyList<MpvNodeValue> trackList)
    {
        long? previousId = SelectedIndex >= 0 && SelectedIndex < Count ? this[SelectedIndex].MpvTrackId : null;
        TrackList.Clear();
        long selectedId = -1;
        foreach (MpvNodeValue node in trackList)
        {
            if (node.Kind != MpvNodeKind.Map || MediaTrack.GetTrackType(node.AsMap) != "audio") continue;
            AudioTrack track = new(node);
            if (track.MpvSelected) selectedId = track.MpvTrackId;
            TrackList.Add(track);
        }

        int selected = FindIndexById(selectedId);
        if (selected < 0 && previousId.HasValue) selected = FindIndexById(previousId.Value);
        if (selected < 0) selected = Count > 0 ? 0 : -1;
        SelectedIndex = selected;
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
