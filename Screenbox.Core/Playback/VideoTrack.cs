using CommunityToolkit.Diagnostics;
using Screenbox.Mpv;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class VideoTrack : MediaTrack
{
    public string Name { get; }

    internal VideoTrack(MpvNodeValue trackNode) : base(trackNode)
    {
        Guard.IsTrue(TrackKind == MediaTrackKind.Video, nameof(trackNode));
        Name = TrackTitle ?? Language ?? Id;
    }
}
