using CommunityToolkit.Diagnostics;
using Screenbox.Mpv;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public sealed partial class AudioTrack : MediaTrack
{
    public string Name { get; }

    internal AudioTrack(MpvNodeValue trackNode) : base(trackNode)
    {
        Guard.IsTrue(TrackKind == MediaTrackKind.Audio, nameof(trackNode));
        Name = TrackTitle ?? Language ?? Id;
    }
}
