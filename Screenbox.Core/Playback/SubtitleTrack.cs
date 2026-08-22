using CommunityToolkit.Diagnostics;
using Screenbox.Mpv;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class SubtitleTrack : MediaTrack
{
    public SubtitleTrack(string language = "") : base(MediaTrackKind.TimedMetadata, language)
    {
    }

    internal SubtitleTrack(MpvNodeValue trackNode) : base(trackNode)
    {
        Guard.IsTrue(TrackKind == MediaTrackKind.TimedMetadata, nameof(trackNode));
    }
}
