using Screenbox.Core.Playback;

namespace Screenbox.Core.Services;

public interface IPlayerService
{
    /// <summary>
    /// Creates the media player. <paramref name="mpvOptions"/> are mpv options in
    /// <c>--key=value</c> form, applied before mpv initialization.
    /// </summary>
    IMediaPlayer Initialize(string[] mpvOptions);

    /// <summary>
    /// Creates a playback item. <paramref name="options"/> are per-file mpv options
    /// (<c>key=value</c>) passed to <c>loadfile</c>.
    /// </summary>
    PlaybackItem CreatePlaybackItem(IMediaPlayer player, object source, params string[] options);

    void DisposePlaybackItem(PlaybackItem item);

    void DisposePlayer(IMediaPlayer player);
}
