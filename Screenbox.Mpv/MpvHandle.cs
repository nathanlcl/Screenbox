// Screenbox.Mpv — libmpv client API 公开门面（SPEC §6.2）。
// 线程模型（SPEC §D3/§7）：事件泵跑在专用后台线程，mpv_set_wakeup_callback 仅
// 释放信号量（回调内禁止调用任何 mpv API），泵线程用 mpv_wait_event(0) 排空队列
// 后派发事件。stream_cb 回调在 mpv 内部线程执行，禁止抛异常跨 native 边界。

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Screenbox.Mpv.Interop;
using Screenbox.Mpv.Streams;

namespace Screenbox.Mpv;

/// <summary>
/// libmpv 客户端句柄封装。构造即 mpv_create；<see cref="Initialize"/> 前用
/// <see cref="SetOption"/> 配置初始选项。所有事件在事件泵后台线程触发。
/// </summary>
public sealed unsafe class MpvHandle : IDisposable
{
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly List<GCHandle> _streamFactories = new();
    private readonly object _syncLock = new();
    private MpvHandleNative* _handle;
    private GCHandle _thisHandle;
    private Thread? _eventPump;
    private long _nextObserveId;
    private volatile bool _disposed;
    private volatile bool _shutdownReceived;
    private bool _initialized;

    static MpvHandle()
    {
        // SPEC §8.5：本地开发未跑 fetch 脚本时给出明确指引，而不是笼统的 DllNotFoundException。
        if (!NativeLibrary.TryLoad("libmpv-2.dll", typeof(MpvHandle).Assembly, null, out _))
        {
            throw new DllNotFoundException(
                "无法加载 libmpv-2.dll。请先运行 scripts\\fetch-libmpv.ps1 -Platform <x86|x64|arm64> " +
                "下载原生库（MSIX 打包后位于包根、与 exe 同级），详见 Screenbox.Mpv/README.md。");
        }
    }

    /// <summary>mpv_create。</summary>
    public MpvHandle()
    {
        _handle = MpvNative.mpv_create();
        if (_handle == null)
            throw new MpvException(MpvError.NoMem, "mpv_create");
        _thisHandle = GCHandle.Alloc(this);
    }

    /// <summary>Initialize 前调用（mpv_set_option_string）。</summary>
    public void SetOption(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("SetOption 必须在 Initialize 之前调用。");

        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        using var valueUtf8 = new Utf8Marshaller(value, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_set_option_string(_handle, nameUtf8.Ptr, valueUtf8.Ptr), $"set option '{name}'");
    }

    /// <summary>mpv_initialize + 启动事件泵线程。</summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("MpvHandle 只能 Initialize 一次。");

        MpvNative.mpv_set_wakeup_callback(_handle, &OnWakeup, (void*)GCHandle.ToIntPtr(_thisHandle));
        try
        {
            MpvException.ThrowOnError(MpvNative.mpv_initialize(_handle), "mpv_initialize");
        }
        catch
        {
            MpvNative.mpv_set_wakeup_callback(_handle, null, null);
            throw;
        }

        _initialized = true;
        _eventPump = new Thread(EventPumpMain) { IsBackground = true, Name = "MpvEventPump" };
        _eventPump.Start();
        _eventSignal.Release(); // 排空初始化期间可能已入队的事件
    }

    /// <summary>mpv_command，MpvException 抛错。</summary>
    public void Command(params string[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
            throw new ArgumentException("至少需要一个命令名参数。", nameof(args));

        // stackalloc 默认零初始化 → 末位天然为 NULL 终止符。
        byte** argv = stackalloc byte*[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                argv[i] = Utf8Marshaller.AllocNullTerminated(args[i]);

            MpvException.ThrowOnError(MpvNative.mpv_command(_handle, argv), $"command '{args[0]}'");
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (argv[i] != null)
                    Utf8Marshaller.Free(argv[i]);
        }
    }

    /// <summary>mpv_command_node（参数作为 MPV_FORMAT_NODE_ARRAY 字符串节点）。</summary>
    public MpvNodeValue CommandNode(params string[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
            throw new ArgumentException("至少需要一个命令名参数。", nameof(args));

        // 自建节点数组必须按原生步长排布：x86 MinGW 下 sizeof(mpv_node)==12（见 MpvNodeLayout）。
        int stride = MpvNodeLayout.NodeStride;
        byte* values = stackalloc byte[args.Length * stride];
        byte** strings = stackalloc byte*[args.Length]; // 零初始化，OOM 中途安全
        MpvNodeList list = new() { Num = args.Length, Values = (MpvNode*)values, Keys = null };
        MpvNode argNode = default;
        argNode.Format = MpvFormat.NodeArray;
        argNode.List = &list;
        MpvNode result = default;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                strings[i] = Utf8Marshaller.AllocNullTerminated(args[i]);
                byte* nodePtr = values + i * stride;
                *(byte**)nodePtr = strings[i];                       // u.string @ offset 0
                *(MpvFormat*)(nodePtr + 8) = MpvFormat.String;       // format @ offset 8
            }

            MpvException.ThrowOnError(
                MpvNative.mpv_command_node(_handle, &argNode, &result), $"command '{args[0]}'");
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (strings[i] != null)
                    Utf8Marshaller.Free(strings[i]);
        }

        try
        {
            return MpvNodeReader.Copy(&result);
        }
        finally
        {
            MpvNative.mpv_free_node_contents(&result); // 归调用方所有，必须 free
        }
    }

    /// <summary>
    /// mpv_command_node：字符串参数 + 末尾追加一个 string→string 的 NODE_MAP
    /// （loadfile 的 per-file options 等场景；模块 B 增量，SPEC §6.3 Play() 映射）。
    /// </summary>
    public MpvNodeValue CommandNode(IReadOnlyDictionary<string, string> options, params string[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
            throw new ArgumentException("至少需要一个命令名参数。", nameof(args));

        int stride = MpvNodeLayout.NodeStride;
        int optionCount = options.Count;
        byte* values = stackalloc byte[(args.Length + 1) * stride];
        byte* mapValues = stackalloc byte[Math.Max(optionCount, 1) * stride];
        byte** mapKeys = stackalloc byte*[Math.Max(optionCount, 1)];
        byte** strings = stackalloc byte*[args.Length + optionCount * 2]; // 零初始化，OOM 中途安全
        MpvNodeList list = new() { Num = args.Length + 1, Values = (MpvNode*)values, Keys = null };
        MpvNodeList mapList = new() { Num = optionCount, Values = (MpvNode*)mapValues, Keys = mapKeys };
        MpvNode argNode = default;
        argNode.Format = MpvFormat.NodeArray;
        argNode.List = &list;
        MpvNode result = default;

        try
        {
            int allocIndex = 0;
            for (int i = 0; i < args.Length; i++)
            {
                byte* nodePtr = values + i * stride;
                *(byte**)nodePtr = strings[allocIndex] = Utf8Marshaller.AllocNullTerminated(args[i]);
                allocIndex++;
                *(MpvFormat*)(nodePtr + 8) = MpvFormat.String;
            }

            int optionIndex = 0;
            foreach (KeyValuePair<string, string> pair in options)
            {
                mapKeys[optionIndex] = strings[allocIndex] = Utf8Marshaller.AllocNullTerminated(pair.Key);
                allocIndex++;
                byte* nodePtr = mapValues + optionIndex * stride;
                *(byte**)nodePtr = strings[allocIndex] = Utf8Marshaller.AllocNullTerminated(pair.Value);
                allocIndex++;
                *(MpvFormat*)(nodePtr + 8) = MpvFormat.String;
                optionIndex++;
            }

            byte* mapNodePtr = values + args.Length * stride;
            *(MpvNodeList**)mapNodePtr = &mapList;
            *(MpvFormat*)(mapNodePtr + 8) = MpvFormat.NodeMap;

            MpvException.ThrowOnError(
                MpvNative.mpv_command_node(_handle, &argNode, &result), $"command '{args[0]}'");
        }
        finally
        {
            for (int i = 0; i < args.Length + optionCount * 2; i++)
                if (strings[i] != null)
                    Utf8Marshaller.Free(strings[i]);
        }

        try
        {
            return MpvNodeReader.Copy(&result);
        }
        finally
        {
            MpvNative.mpv_free_node_contents(&result); // 归调用方所有，必须 free
        }
    }

    /// <summary>mpv_get_property_string；失败返回 null。</summary>
    public string? GetPropertyString(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        byte* value = MpvNative.mpv_get_property_string(_handle, nameUtf8.Ptr);
        if (value == null)
            return null;
        try
        {
            return Utf8Marshaller.ToString(value);
        }
        finally
        {
            MpvNative.mpv_free(value); // client.h 明确要求 mpv_free
        }
    }

    public void SetPropertyString(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        using var valueUtf8 = new Utf8Marshaller(value, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_set_property_string(_handle, nameUtf8.Ptr, valueUtf8.Ptr), $"set property '{name}'");
    }

    public double GetPropertyDouble(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        double value;
        MpvException.ThrowOnError(
            MpvNative.mpv_get_property(_handle, nameUtf8.Ptr, MpvFormat.Double, &value), $"get property '{name}'");
        return value;
    }

    public void SetPropertyDouble(string name, double value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_set_property(_handle, nameUtf8.Ptr, MpvFormat.Double, &value), $"set property '{name}'");
    }

    public long GetPropertyInt64(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        long value;
        MpvException.ThrowOnError(
            MpvNative.mpv_get_property(_handle, nameUtf8.Ptr, MpvFormat.Int64, &value), $"get property '{name}'");
        return value;
    }

    public void SetPropertyInt64(string name, long value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_set_property(_handle, nameUtf8.Ptr, MpvFormat.Int64, &value), $"set property '{name}'");
    }

    public bool GetPropertyFlag(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        int value;
        MpvException.ThrowOnError(
            MpvNative.mpv_get_property(_handle, nameUtf8.Ptr, MpvFormat.Flag, &value), $"get property '{name}'");
        return value != 0;
    }

    public void SetPropertyFlag(string name, bool value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        int flag = value ? 1 : 0;
        MpvException.ThrowOnError(
            MpvNative.mpv_set_property(_handle, nameUtf8.Ptr, MpvFormat.Flag, &flag), $"set property '{name}'");
    }

    /// <summary>mpv_get_property(MPV_FORMAT_NODE)：track-list/chapter-list/metadata/playlist。</summary>
    public MpvNodeValue GetPropertyNode(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvNode node = default;
        MpvException.ThrowOnError(
            MpvNative.mpv_get_property(_handle, nameUtf8.Ptr, MpvFormat.Node, &node), $"get property '{name}'");
        try
        {
            return MpvNodeReader.Copy(&node);
        }
        finally
        {
            MpvNative.mpv_free_node_contents(&node); // client.h：读路径归调用方 free
        }
    }

    /// <summary>mpv_observe_property。返回观察 id（reply_userdata）；变化走 PropertyChanged。</summary>
    public ulong ObserveProperty(string name, MpvFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong id = (ulong)Interlocked.Increment(ref _nextObserveId);
        using var nameUtf8 = new Utf8Marshaller(name, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_observe_property(_handle, id, nameUtf8.Ptr, format), $"observe property '{name}'");
        return id;
    }

    public void UnobserveProperty(ulong id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MpvException.ThrowOnError(MpvNative.mpv_unobserve_property(_handle, id), "unobserve property");
    }

    /// <summary>mpv_request_log_messages（"no"/"fatal"/"error"/"warn"/"info"/"v"/"debug"/"trace"）。</summary>
    public void RequestLogMessages(string minLevel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var levelUtf8 = new Utf8Marshaller(minLevel, stackalloc byte[Utf8Marshaller.StackLimit]);
        MpvException.ThrowOnError(
            MpvNative.mpv_request_log_messages(_handle, levelUtf8.Ptr), "request log messages");
    }

    /// <summary>mpv_stream_cb_add_ro：注册自定义只读协议（如 screenbox://）。</summary>
    public void AddStreamProtocol(string protocol, IMpvStreamFactory factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(factory);

        GCHandle factoryHandle = GCHandle.Alloc(factory);
        try
        {
            using var protocolUtf8 = new Utf8Marshaller(protocol, stackalloc byte[Utf8Marshaller.StackLimit]);
            MpvException.ThrowOnError(
                MpvNative.mpv_stream_cb_add_ro(
                    _handle, protocolUtf8.Ptr, (void*)GCHandle.ToIntPtr(factoryHandle), &StreamOpenCallback),
                $"add stream protocol '{protocol}'");
            lock (_syncLock)
                _streamFactories.Add(factoryHandle);
        }
        catch
        {
            factoryHandle.Free();
            throw;
        }
    }

    /// <summary>观察属性变化（Name + Value）。</summary>
    public event EventHandler<MpvPropertyChangedEventArgs>? PropertyChanged;

    /// <summary>MPV_EVENT_FILE_LOADED。</summary>
    public event EventHandler<MpvFileLoadedEventArgs>? FileLoaded;

    /// <summary>MPV_EVENT_END_FILE（Reason, Error）。</summary>
    public event EventHandler<MpvEndFileEventArgs>? EndFile;

    /// <summary>MPV_EVENT_START_FILE。</summary>
    public event EventHandler<MpvEventArgs>? StartFile;

    /// <summary>MPV_EVENT_LOG_MESSAGE（Prefix, Level, Text）。</summary>
    public event EventHandler<MpvLogMessageEventArgs>? LogMessage;

    /// <summary>原生 mpv_handle*，供 MpvRenderContext 等内部使用。</summary>
    internal MpvHandleNative* Raw =>
        _handle != null ? _handle : throw new ObjectDisposedException(nameof(MpvHandle));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _eventSignal.Release();
        if (_eventPump is { IsAlive: true } pump &&
            pump.ManagedThreadId != Environment.CurrentManagedThreadId)
            pump.Join(); // 泵线程只跑 mpv_wait_event(0)，退出迅速；避免事件回调内自 Join 死锁

        // terminate_destroy 后 mpv 不会再触发任何回调，之后才能安全释放 GCHandle。
        MpvNative.mpv_set_wakeup_callback(_handle, null, null);
        MpvNative.mpv_terminate_destroy(_handle);
        _handle = null;

        lock (_syncLock)
        {
            foreach (GCHandle factoryHandle in _streamFactories)
                factoryHandle.Free();
            _streamFactories.Clear();
        }

        if (_thisHandle.IsAllocated)
            _thisHandle.Free();
        _eventSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- 事件泵 ----

    private void EventPumpMain()
    {
        while (true)
        {
            _eventSignal.Wait();
            if (_disposed)
                return;

            while (true)
            {
                MpvEvent* ev = MpvNative.mpv_wait_event(_handle, 0);
                if (ev->EventId == MpvEventId.None)
                    break;

                try
                {
                    Dispatch(ev);
                }
                catch
                {
                    // 单个事件处理（含订阅方回调）失败不应杀死事件泵。
                }

                if (ev->EventId == MpvEventId.Shutdown)
                    _shutdownReceived = true;
            }

            if (_shutdownReceived)
                return;
        }
    }

    private void Dispatch(MpvEvent* ev)
    {
        switch (ev->EventId)
        {
            case MpvEventId.PropertyChange:
            {
                if (ev->Data == null)
                    break;
                MpvEventProperty* prop = (MpvEventProperty*)ev->Data;
                string name = Utf8Marshaller.ToString(prop->Name) ?? string.Empty;
                PropertyChanged?.Invoke(this, new MpvPropertyChangedEventArgs(name, ReadPropertyValue(prop)));
                break;
            }
            case MpvEventId.FileLoaded:
                FileLoaded?.Invoke(this, new MpvFileLoadedEventArgs());
                break;
            case MpvEventId.EndFile:
            {
                MpvEventEndFile* endFile = (MpvEventEndFile*)ev->Data;
                MpvEndFileReason reason = endFile != null ? endFile->Reason : MpvEndFileReason.Error;
                MpvError error = endFile != null ? endFile->Error : MpvError.Generic;
                EndFile?.Invoke(this, new MpvEndFileEventArgs(reason, error));
                break;
            }
            case MpvEventId.StartFile:
                StartFile?.Invoke(this, new MpvEventArgs());
                break;
            case MpvEventId.LogMessage:
            {
                MpvEventLogMessage* msg = (MpvEventLogMessage*)ev->Data;
                if (msg == null)
                    break;
                string prefix = Utf8Marshaller.ToString(msg->Prefix) ?? string.Empty;
                string level = Utf8Marshaller.ToString(msg->Level) ?? string.Empty;
                string text = (Utf8Marshaller.ToString(msg->Text) ?? string.Empty).TrimEnd('\r', '\n');
                LogMessage?.Invoke(this, new MpvLogMessageEventArgs(prefix, level, text));
                break;
            }
        }
    }

    private static MpvNodeValue? ReadPropertyValue(MpvEventProperty* prop)
    {
        if (prop->Data == null)
            return null;
        return prop->Format switch
        {
            MpvFormat.Flag => MpvNodeValue.FromBoolean(*(int*)prop->Data != 0),
            MpvFormat.Int64 => MpvNodeValue.FromInt64(*(long*)prop->Data),
            MpvFormat.Double => MpvNodeValue.FromDouble(*(double*)prop->Data),
            MpvFormat.String => MpvNodeValue.FromString(Utf8Marshaller.ToString(*(byte**)prop->Data) ?? string.Empty),
            // 事件携带的 node 归 libmpv 所有：只深拷贝，禁止 mpv_free_node_contents。
            MpvFormat.Node => MpvNodeReader.Copy((MpvNode*)prop->Data),
            _ => null, // MPV_FORMAT_NONE（属性不可用）或意外格式
        };
    }

    // ---- 原生回调（全部 UCO + Cdecl，禁止抛出） ----

    /// <summary>mpv 唤醒回调：只释放信号量，禁止调用任何 mpv API。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnWakeup(void* data)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)data).Target is MpvHandle { _disposed: false } self)
                self._eventSignal.Release();
        }
        catch
        {
            // 信号量满/已释放等：忽略，绝不跨 native 边界抛异常。
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int StreamOpenCallback(void* userData, byte* uri, MpvStreamCbInfo* info)
    {
        IMpvStream? stream = null;
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is not IMpvStreamFactory factory)
                return (int)MpvError.LoadingFailed;

            stream = factory.Open(Utf8Marshaller.ToString(uri) ?? string.Empty);
            if (stream == null)
                return (int)MpvError.LoadingFailed;

            info->cookie = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(stream));
            info->read_fn = &StreamRead;
            info->seek_fn = &StreamSeek;
            info->size_fn = &StreamSize;
            info->close_fn = &StreamClose;
            info->cancel_fn = &StreamCancel;
            return 0;
        }
        catch
        {
            stream?.Dispose();
            return (int)MpvError.LoadingFailed;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long StreamRead(void* cookie, byte* buffer, ulong byteCount)
    {
        try
        {
            if (GetStream(cookie) is not { } stream)
                return -1;
            int count = (int)Math.Min(byteCount, (ulong)int.MaxValue);
            return stream.Read(new Span<byte>(buffer, count));
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long StreamSeek(void* cookie, long offset)
    {
        try
        {
            long result = GetStream(cookie)?.Seek(offset) ?? -1;
            // mpv stream_cb contract: unsupported seek must return MPV_ERROR_UNSUPPORTED, not -1
            return result < 0 ? (long)MpvError.Unsupported : result;
        }
        catch
        {
            return (long)MpvError.Unsupported;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long StreamSize(void* cookie)
    {
        try
        {
            return GetStream(cookie)?.Size ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void StreamClose(void* cookie)
    {
        try
        {
            if (cookie == null)
                return;
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)cookie);
            (handle.Target as IMpvStream)?.Dispose();
            handle.Free();
        }
        catch
        {
            // 禁止跨 native 边界抛异常。
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void StreamCancel(void* cookie)
    {
        try
        {
            GetStream(cookie)?.Cancel();
        }
        catch
        {
            // 禁止跨 native 边界抛异常。
        }
    }

    private static IMpvStream? GetStream(void* cookie) =>
        cookie == null ? null : GCHandle.FromIntPtr((IntPtr)cookie).Target as IMpvStream;
}

/// <summary>播放器实现暴露 mpv 句柄（VideoView/VM 取句柄用）。</summary>
public interface IMpvPlayer
{
    MpvHandle Handle { get; }
}
