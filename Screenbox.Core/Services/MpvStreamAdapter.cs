// MpvStreamAdapter — IRandomAccessStream → IMpvStream 适配（SPEC §D4/§5.1）。
// 运行环境：Read/Seek 在 mpv stream_cb 内部线程同步阻塞调用；异常会被绑定层
// 吞掉并转为 -1（错误）返回给 mpv。内建 256KB 预读缓冲（顺序读合并，降低
// WinRT 调用频率），seek 后清空。mpv 在 open 后会立即 seek(0) 探测可寻址性，
// 本适配器始终支持 seek，正常响应该探测。

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace Screenbox.Core.Services;

/// <summary>
/// 把 WinRT <see cref="IRandomAccessStream"/> 包装成 mpv stream_cb 只读流。
/// 不是线程安全的：mpv 对同一流的 read/seek/close 调用串行（仅 <see cref="Cancel"/>
/// 可能跨线程，内部用 <see cref="CancellationTokenSource"/> 保证安全）。
/// </summary>
public sealed class MpvStreamAdapter : IMpvStreamAdapter
{
    /// <summary>预读缓冲大小（SPEC §D4：256KB 顺序读合并）。</summary>
    private const int ReadAheadSize = 256 * 1024;

    private readonly Stream _stream;    // IRandomAccessStream.AsStream()，本身支持 seek
    private readonly byte[] _readAhead = new byte[ReadAheadSize];
    private readonly CancellationTokenSource _cancellation = new();

    private long _position;             // mpv 视角的逻辑读位置
    private long _bufferStart;          // _readAhead[0] 对应的绝对偏移
    private int _bufferValid;           // _readAhead 中有效字节数
    private bool _disposed;

    public MpvStreamAdapter(IRandomAccessStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Size = (long)stream.Size;
        _stream = stream.AsStream();
    }

    /// <inheritdoc/>
    public long Size { get; }

    /// <inheritdoc/>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0) return 0;
        if (_position >= Size) return 0;    // EOF；position 可能因越界 seek 超过 Size

        // 预读缓冲命中
        long bufferOffset = _position - _bufferStart;
        if (bufferOffset >= 0 && bufferOffset < _bufferValid)
        {
            int buffered = (int)Math.Min(_bufferValid - bufferOffset, buffer.Length);
            _readAhead.AsSpan((int)bufferOffset, buffered).CopyTo(buffer);
            _position += buffered;
            return buffered;
        }

        // 缓冲未命中时不变量：底层流位置应同步到逻辑位置
        if (_stream.Position != _position)
            _stream.Position = _position;

        if (buffer.Length >= ReadAheadSize)
        {
            // 大读直通，不经过预读缓冲
            int read = ReadCore(buffer);
            _position += read;
            _bufferValid = 0;
            _bufferStart = _position;
            return read;
        }

        // 小读预读：填满 256KB 缓冲后再分出
        int fill = ReadCore(_readAhead.AsSpan(0, (int)Math.Min(ReadAheadSize, Size - _position)));
        _bufferStart = _position;
        _bufferValid = fill;

        int served = Math.Min(fill, buffer.Length);
        _readAhead.AsSpan(0, served).CopyTo(buffer);
        _position += served;
        return served;
    }

    /// <inheritdoc/>
    public long Seek(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) return -1;  // 负偏移不支持（绑定层会把失败转为错误码）

        _position = offset;
        _bufferValid = 0;           // seek 后清空预读缓冲
        _bufferStart = offset;
        if (offset <= Size)
            _stream.Position = offset;  // 越界 seek 不动底层流，Read 时按 EOF 处理
        return offset;
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        // cancel_fn：中止进行中的阻塞读。之后所有 Read 抛 OperationCanceledException，
        // 绑定层转为 -1 返回给 mpv。一次性语义：mpv 只在放弃该流时调用 cancel。
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _cancellation.Dispose();
    }

    private int ReadCore(Span<byte> destination)
    {
        // WinRT 读可能短读，循环补满；取消令牌让 Cancel() 能打断阻塞读。
        // 注意：Stream.ReadAsync 只接受 Memory<byte>（Span 无隐式转换），
        // 经 ArrayPool 中转，分块不超过预读缓冲大小。
        int total = 0;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, ReadAheadSize));
        try
        {
            while (total < destination.Length)
            {
                int chunk = Math.Min(scratch.Length, destination.Length - total);
                int read = _stream
                    .ReadAsync(scratch.AsMemory(0, chunk), _cancellation.Token)
                    .GetAwaiter().GetResult();
                if (read == 0) break;   // EOF
                scratch.AsSpan(0, read).CopyTo(destination.Slice(total));
                total += read;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
