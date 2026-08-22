// Screenbox.Mpv — 手工 UTF-8 封送助手（SPEC §6.1 封送约定）。
// ≤256B 走 stackalloc，否则 ArrayPool 租用 + 固定（DisableRuntimeMarshalling 下
// 无任何运行时封送）。ref struct 保证缓冲区不逃逸出调用帧。

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Screenbox.Mpv.Interop;

/// <summary>
/// 作用域内 UTF-8 NUL 结尾缓冲区。用法：
/// <code>using var s = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
/// MpvNative.mpv_xxx(handle, s.Ptr, ...);</code>
/// </summary>
internal unsafe ref struct Utf8Marshaller
{
    /// <summary>stackalloc 上限（字节）。</summary>
    public const int StackLimit = 256;

    private byte[]? _rented;
    private GCHandle _pin;
    private Span<byte> _buffer;

    public Utf8Marshaller(string value, Span<byte> stackBuffer)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount + 1 <= stackBuffer.Length)
        {
            _buffer = stackBuffer;
            _rented = null;
            _pin = default;
        }
        else
        {
            _rented = ArrayPool<byte>.Shared.Rent(byteCount + 1);
            _pin = GCHandle.Alloc(_rented, GCHandleType.Pinned);
            _buffer = _rented;
        }

        Encoding.UTF8.GetBytes(value, _buffer);
        _buffer[byteCount] = 0;
    }

    /// <summary>指向 NUL 结尾 UTF-8 字节的指针，仅在当前作用域内有效。</summary>
    public byte* Ptr
    {
        get
        {
            if (_rented != null)
                return (byte*)_pin.AddrOfPinnedObject();
            fixed (byte* p = _buffer)
                return p;
        }
    }

    public void Dispose()
    {
        if (_rented != null)
        {
            _pin.Free();
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = null;
        }
    }

    /// <summary>UTF-8 NUL 结尾指针 → 托管 string；NULL → null。</summary>
    public static string? ToString(byte* value)
    {
        return value == null
            ? null
            : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value));
    }

    /// <summary>
    /// 非托管堆分配 NUL 结尾 UTF-8 串（NativeMemory），调用方负责 <see cref="Free"/>。
    /// 供无法用 ref struct 的数组场景（mpv_command 参数表）使用。
    /// </summary>
    public static byte* AllocNullTerminated(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte* ptr = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);
        int written = Encoding.UTF8.GetBytes(value, new Span<byte>(ptr, byteCount));
        ptr[written] = 0;
        return ptr;
    }

    public static void Free(byte* value) => NativeMemory.Free(value);
}
