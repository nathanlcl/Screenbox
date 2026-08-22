// Screenbox.Mpv — 错误码 → 异常（SPEC §5.1）。消息经 mpv_error_string 取静态描述串。

using System;
using Screenbox.Mpv.Interop;

namespace Screenbox.Mpv;

/// <summary>libmpv 调用返回负值错误码时抛出。</summary>
public sealed class MpvException : Exception
{
    public MpvException(MpvError errorCode)
        : base(GetErrorString(errorCode))
    {
        ErrorCode = errorCode;
    }

    internal MpvException(MpvError errorCode, string operation)
        : base($"{operation} failed: {GetErrorString(errorCode)} ({(int)errorCode})")
    {
        ErrorCode = errorCode;
    }

    /// <summary>mpv_error。</summary>
    public MpvError ErrorCode { get; }

    /// <summary>libmpv 约定：返回值 &lt; 0 为错误。</summary>
    internal static void ThrowOnError(int error, string operation)
    {
        if (error < 0)
            throw new MpvException((MpvError)error, operation);
    }

    private static unsafe string GetErrorString(MpvError errorCode)
    {
        // mpv_error_string 返回静态串，永不 free；构造异常时原生库必然已加载。
        return Utf8Marshaller.ToString(MpvNative.mpv_error_string((int)errorCode)) ?? "unknown error";
    }
}
