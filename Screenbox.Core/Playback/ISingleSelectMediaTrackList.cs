using Windows.Foundation;

namespace Screenbox.Core.Playback;

public interface ISingleSelectMediaTrackList
{
    event TypedEventHandler<ISingleSelectMediaTrackList, object?>? SelectedIndexChanged;

    int SelectedIndex { get; set; }
}
