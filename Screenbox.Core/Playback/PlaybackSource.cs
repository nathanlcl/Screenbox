using System;
using Windows.Storage;

namespace Screenbox.Core.Playback;

/// <summary>
/// Description of the media to play back (SPEC §5.1). mpv has no standalone media
/// object; this record carries everything <see cref="MpvMediaPlayer"/> needs to issue
/// a <c>loadfile</c> command when playback starts.
/// </summary>
/// <param name="PlayUri">
/// URI handed to mpv. Local files go through the custom <c>screenbox://&lt;token&gt;</c>
/// stream protocol (SPEC §D4); network and plain path sources are passed through as-is.
/// </param>
/// <param name="Options">Per-file mpv options (<c>key=value</c> strings) for <c>loadfile</c>.</param>
/// <param name="File">Original storage file when the source is a local file, otherwise null.</param>
/// <param name="FalToken">
/// FutureAccessList/SharedStorageAccessManager token backing <see cref="PlayUri"/>,
/// used by <see cref="Services.IPlayerService.DisposePlaybackItem"/> to release the access grant.
/// </param>
public sealed record PlaybackSource(Uri PlayUri, string[] Options, IStorageFile? File, string? FalToken)
{
    /// <summary>
    /// URL string for mpv <c>loadfile</c>. <c>file://</c> URIs are converted back to plain
    /// paths because libmpv/ffmpeg handle native paths more reliably on Windows.
    /// </summary>
    public string GetPlayUrl() => PlayUri.IsFile ? PlayUri.LocalPath : PlayUri.AbsoluteUri;
}
