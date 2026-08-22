// Screenbox.Mpv — MpvHandle 事件参数（SPEC §6.2）。
// 所有事件均在 mpv 事件泵后台线程触发；订阅方须自行切回 UI 线程
// （现有 VM 层 DispatcherQueue.TryEnqueue 模式不变）。

using System;
using Screenbox.Mpv.Interop;

namespace Screenbox.Mpv;

/// <summary>mpv 事件基类。</summary>
public class MpvEventArgs : EventArgs
{
    internal MpvEventArgs() { }
}

/// <summary>MPV_EVENT_PROPERTY_CHANGE。Value 为 null 表示属性不可用（MPV_FORMAT_NONE）。</summary>
public sealed class MpvPropertyChangedEventArgs : MpvEventArgs
{
    internal MpvPropertyChangedEventArgs(string name, MpvNodeValue? value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>属性名（observe 时的名字，可含路径如 video-params）。</summary>
    public string Name { get; }

    /// <summary>属性值，已按观察格式规整为 MpvNodeValue（Flag/Int64/Double/String/Node）。</summary>
    public MpvNodeValue? Value { get; }
}

/// <summary>MPV_EVENT_FILE_LOADED：文件头已读取、解码开始。可读 track-list/metadata 快照。</summary>
public sealed class MpvFileLoadedEventArgs : MpvEventArgs
{
    internal MpvFileLoadedEventArgs() { }
}

/// <summary>MPV_EVENT_END_FILE。</summary>
public sealed class MpvEndFileEventArgs : MpvEventArgs
{
    internal MpvEndFileEventArgs(MpvEndFileReason reason, MpvError error)
    {
        Reason = reason;
        Error = error;
    }

    public MpvEndFileReason Reason { get; }

    /// <summary>仅当 <see cref="Reason"/> 为 <see cref="MpvEndFileReason.Error"/> 时有意义，否则为 Success。</summary>
    public MpvError Error { get; }
}

/// <summary>MPV_EVENT_LOG_MESSAGE（mpv_request_log_messages 后触发）。</summary>
public sealed class MpvLogMessageEventArgs : MpvEventArgs
{
    internal MpvLogMessageEventArgs(string prefix, string level, string text)
    {
        Prefix = prefix;
        Level = level;
        Text = text;
    }

    /// <summary>模块前缀，标识消息来源；缓冲溢出时为 "overflow"。</summary>
    public string Prefix { get; }

    /// <summary>日志级别字符串：fatal/error/warn/info/v/debug/trace。</summary>
    public string Level { get; }

    /// <summary>单行日志文本（已去除结尾换行）。</summary>
    public string Text { get; }
}
