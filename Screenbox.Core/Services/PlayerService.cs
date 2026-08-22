using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Factories;
using Screenbox.Core.Playback;
using Screenbox.Mpv;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace Screenbox.Core.Services;

public sealed partial class PlayerService : IPlayerService
{
    private readonly ILogger<PlayerService> _logger;
    private readonly ILogger _mpvLogger;
    private readonly bool _useFal;
    private readonly Dictionary<string, int> _tokenReferences = new();
    private readonly MpvStreamFactory _streamFactory = new();

    public PlayerService(
        ILogger<PlayerService> logger,
        ILogger<MpvHandle> mpvLogger)
    {
        _logger = logger;
        _mpvLogger = mpvLogger;

        // FutureAccessList is preferred because it can handle network StorageFiles
        // If FutureAccessList is somehow unavailable, SharedStorageAccessManager will be the fallback
        _useFal = true;

        try
        {
            // Clear FA periodically because of 1000 items limit
            // Delete any entries with "media" metadata to avoid hitting the limit with stale entries
            var tokensToRemove = StorageApplicationPermissions.FutureAccessList.Entries
                .Where(entry => entry.Metadata == "media")
                .Select(entry => entry.Token)
                .ToList();
            foreach (var token in tokensToRemove)
            {
                StorageApplicationPermissions.FutureAccessList.Remove(token);
            }
        }
        catch (Exception)   // FileNotFoundException
        {
            // FutureAccessList is not available
            _useFal = false;
        }
    }

    public IMediaPlayer Initialize(string[] mpvOptions)
    {
        MpvHandle handle = new();
        try
        {
            // SPEC §6.3 初始选项表
            handle.SetOption("vo", "libmpv");
            handle.SetOption("ao", "wasapi");
            handle.SetOption("osd-level", "0");
            handle.SetOption("osd-on-seek", "no");
            handle.SetOption("config", "no");
            handle.SetOption("input-default-bindings", "no");
            handle.SetOption("input-builtin-bindings", "no");
            handle.SetOption("hwdec", "auto-safe");
            handle.SetOption("audio-device", "auto");
            handle.SetOption("keep-open", "yes");
            handle.SetOption("ytdl", "no");

            foreach (string option in mpvOptions)
            {
                // 传入参数为 "--key=value"（或 "--flag"）形式
                string arg = option.StartsWith("--", StringComparison.Ordinal) ? option.Substring(2) : option;
                int separatorIndex = arg.IndexOf('=');
                if (separatorIndex < 0)
                    handle.SetOption(arg, "yes");
                else
                    handle.SetOption(arg.Substring(0, separatorIndex), arg.Substring(separatorIndex + 1));
            }

            handle.Initialize();
        }
        catch
        {
            handle.Dispose();
            throw;
        }

#if DEBUG
        handle.RequestLogMessages("debug");
#else
        handle.RequestLogMessages("warn");
#endif
        handle.LogMessage += OnMpvLog;
        // SPEC §D4：本地文件统一走 screenbox:// 自定义协议（stream_cb）
        handle.AddStreamProtocol("screenbox", _streamFactory);
        return new MpvMediaPlayer(handle, _logger);
    }

    public PlaybackItem CreatePlaybackItem(IMediaPlayer player, object source, params string[] options)
    {
        if (player is not MpvMediaPlayer)
            throw new NotSupportedException("Only MpvMediaPlayer is supported");
        PlaybackSource playbackSource = CreatePlaybackSource(source, options);
        return new PlaybackItem(source, playbackSource);
    }

    public void DisposePlaybackItem(PlaybackItem item)
    {
        if (item.Source.FalToken is not { } token) return;
        try
        {
            DecrementRefCount(token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to release a playback access token.");
        }
    }

    public void DisposePlayer(IMediaPlayer player)
    {
        if (player is MpvMediaPlayer mpvMediaPlayer)
        {
            mpvMediaPlayer.Close();
        }
    }

    private PlaybackSource CreatePlaybackSource(object source, params string[] options)
    {
        return source switch
        {
            IStorageFile file => CreatePlaybackSource(file, options),
            string str => CreatePlaybackSource(str, options),
            Uri uri => new PlaybackSource(uri, options, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }

    private PlaybackSource CreatePlaybackSource(string str, params string[] options)
    {
        if (Uri.TryCreate(str, UriKind.Absolute, out var uri))
        {
            return new PlaybackSource(uri, options, null, null);
        }

        // 普通 Win32 路径（库能力覆盖的位置可直接读取）
        return new PlaybackSource(new Uri(str), options, null, null);
    }

    private PlaybackSource CreatePlaybackSource(IStorageFile file, params string[] options)
    {
        // NOTE: There have been reports of network locations not working when using the URI approach.
        // Optimization is disable until we can confirm that the issue is resolved.
        if (file is StorageFile storageFile
            && storageFile.Provider.Id.Equals("network", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(storageFile.Path)
            && !_useFal
            && Uri.TryCreate(storageFile.Path, UriKind.Absolute, out var uri))
        {
            // Optimization for network files. Avoid having to deal with WinRT quirks.
            return new PlaybackSource(uri, options, storageFile, null);
        }

        string token = IncrementRefCount(file);
        return new PlaybackSource(new Uri($"screenbox://{token}"), options, file, token);
    }

    private string IncrementRefCount(IStorageFile file)
    {
        string token = _useFal
            ? StorageApplicationPermissions.FutureAccessList.Add(file, "media")
            : SharedStorageAccessManager.AddFile(file);

        lock (_tokenReferences)
        {
            if (_tokenReferences.TryGetValue(token, out int refCount))
                _tokenReferences[token] = refCount + 1;
            else
                _tokenReferences[token] = 1;
        }

        return token;
    }

    private void DecrementRefCount(string token)
    {
        lock (_tokenReferences)
        {
            if (_tokenReferences.TryGetValue(token, out int refCount) && refCount > 1)
                _tokenReferences[token] = refCount - 1;
            else
            {
                _tokenReferences.Remove(token);

                if (_useFal)
                {
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
                else
                {
                    SharedStorageAccessManager.RemoveFile(token);
                }
            }
        }
    }

    private void OnMpvLog(object? sender, MpvLogMessageEventArgs e)
    {
        LogMpvMessage(_mpvLogger, e.Prefix, e.Level, e.Text);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{Prefix}: {MpvLogLevel}: {Message}")]
    private static partial void LogMpvMessage(ILogger logger, string prefix, string mpvLogLevel, string message);
}
