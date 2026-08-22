// Screenbox.Mpv — libmpv 枚举（移植自 mpv libmpv/client.h 与 libmpv/render.h）。
// 数值与头文件逐一对应，改动枚举值即 ABI 破坏，禁止重排。

using System;

namespace Screenbox.Mpv.Interop;

/// <summary>mpv_error。0 与正值为成功，负值为错误。</summary>
public enum MpvError
{
    Success = 0,
    EventQueueFull = -1,
    NoMem = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    Command = -12,
    LoadingFailed = -13,
    AoInitFailed = -14,
    VoInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20,
}

/// <summary>mpv_event_id（client API 2.x；已废弃的 Idle/Tick 不收录）。</summary>
public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

/// <summary>mpv_format。</summary>
public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

/// <summary>mpv_end_file_reason（client API ≥1.9；旧值 RESTARTED=1 已移除）。</summary>
public enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

/// <summary>mpv_render_param_type。</summary>
public enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGLInitParams = 2,
    OpenGLFbo = 3,
    FlipY = 4,
    Depth = 5,
    IccProfile = 6,
    AmbientLight = 7,
    X11Display = 8,
    WlDisplay = 9,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
    DrmDisplay = 14,
    DrmDrawSurfaceSize = 15,
    DrmDisplayV2 = 16,
    SwSize = 17,
    SwFormat = 18,
    SwStride = 19,
    SwPointer = 20,
}

/// <summary>mpv_render_context_update() 返回的标志位（uint64）。</summary>
[Flags]
public enum MpvRenderUpdateFlag : ulong
{
    None = 0,

    /// <summary>MPV_RENDER_UPDATE_FRAME：有新帧，应调用 mpv_render_context_render()。</summary>
    Frame = 1 << 0,
}
