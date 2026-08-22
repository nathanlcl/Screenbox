using System;
using System.Collections.Generic;
using Screenbox.Core.Helpers;
using Screenbox.Mpv;
using Windows.Globalization;
using Windows.Media.Core;

namespace Screenbox.Core.Playback;

public abstract partial class MediaTrack : IMediaTrack
{
    public string Id { get; internal set; }

    public string Label { get; set; }

    public string Language => _language?.DisplayName ?? _languageStr;

    public string LanguageTag => _language?.LanguageTag ?? string.Empty;

    private readonly Language? _language;
    private readonly string _languageStr;

    public MediaTrackKind TrackKind { get; }

    /// <summary>mpv <c>track-list/N/id</c>。懒加载占位轨为 -1，加载后回填。</summary>
    internal long MpvTrackId { get; set; } = -1;

    /// <summary>mpv <c>track-list/N/selected</c>（该轨当前是否被选中）。</summary>
    internal bool MpvSelected { get; }

    /// <summary>mpv <c>track-list/N/title</c>。</summary>
    protected string? TrackTitle { get; }

    internal MediaTrack(MediaTrackKind trackKind, string language = "")
    {
        TrackKind = trackKind;
        _languageStr = language;
        Id = string.Empty;
        Label = string.Empty;
    }

    /// <summary>
    /// Creates a track from an mpv <c>track-list</c> node
    /// (keys: <c>id</c>, <c>type</c>, <c>title</c>, <c>lang</c>, <c>selected</c>, <c>external</c>).
    /// </summary>
    protected MediaTrack(MpvNodeValue trackNode)
    {
        IReadOnlyDictionary<string, MpvNodeValue> map = trackNode.AsMap;
        TrackKind = GetString(map, "type") switch
        {
            "audio" => MediaTrackKind.Audio,
            "video" => MediaTrackKind.Video,
            "sub" => MediaTrackKind.TimedMetadata,
            var type => throw new ArgumentException($"Unknown mpv track type '{type}'.", nameof(trackNode))
        };

        _languageStr = GetString(map, "lang") ?? string.Empty;
        if (Windows.Globalization.Language.IsWellFormed(_languageStr))
        {
            if (LanguageHelper.TryConvertISO6392ToISO6391(_languageStr, out string bc47Tag))
                _languageStr = bc47Tag;
            _language = new Language(_languageStr);
        }

        MpvTrackId = GetInt64(map, "id") ?? -1;
        Id = MpvTrackId.ToString();
        MpvSelected = GetFlag(map, "selected") ?? false;
        TrackTitle = GetString(map, "title");
        Label = GetFullLabel(TrackTitle, Language);
    }

    internal static string? GetString(IReadOnlyDictionary<string, MpvNodeValue> map, string key) =>
        map.TryGetValue(key, out MpvNodeValue? value) && value.Kind == MpvNodeKind.String
            ? value.AsString
            : null;

    internal static long? GetInt64(IReadOnlyDictionary<string, MpvNodeValue> map, string key) =>
        map.TryGetValue(key, out MpvNodeValue? value) && value.Kind == MpvNodeKind.Int64
            ? value.AsInt64
            : null;

    internal static double? GetDouble(IReadOnlyDictionary<string, MpvNodeValue> map, string key) =>
        map.TryGetValue(key, out MpvNodeValue? value) && value.Kind == MpvNodeKind.Double
            ? value.AsDouble
            : null;

    internal static bool? GetFlag(IReadOnlyDictionary<string, MpvNodeValue> map, string key) =>
        map.TryGetValue(key, out MpvNodeValue? value) && value.Kind == MpvNodeKind.Flag
            ? value.AsBoolean
            : null;

    internal static string? GetTrackType(IReadOnlyDictionary<string, MpvNodeValue> map) => GetString(map, "type");

    private static string GetFullLabel(string? label, string language)
    {
        if (string.IsNullOrEmpty(label))
        {
            label = language;
        }
        else if (!string.IsNullOrEmpty(language) && language != label)
        {
            label = $"{label} ({language})";
        }

        return label ?? string.Empty;
    }
}
