// Screenbox.VideoView — mpv 渲染宿主（SPEC §6.4 / §D3 线程模型）。
// 专用渲染线程独占：WGL 上下文（wglMakeCurrent）、mpv_render_context_* 全部调用、
// ResizeBuffers 与 Present。UI 线程仅通过 Resize()/Dispose() 置标志 + 信号量通信；
// mpv 的 update callback（FrameReady，任意 mpv 内部线程触发）只释放信号量，不碰 mpv API。
// 设备丢失（Present/ResizeBuffers 返回 DEVICE_REMOVED/DEVICE_RESET）→ 线程退出并触发
// DeviceLost，由 VideoView 在 UI 线程整体重建 device/swapchain/host。

using System;
using System.Threading;
using Screenbox.Mpv;
using Screenbox.Mpv.Interop;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Screenbox.Controls;

/// <summary>当前生效的渲染后端（诊断展示用，SPEC §D3）。</summary>
public enum MpvRenderBackendKind
{
    WglDxInterop,
    Software,
}

/// <summary>
/// 渲染后端抽象。所有方法（含 CreateRenderContext/Dispose）只在 MpvRenderHost 的
/// 渲染线程上调用。Attach/Detach 配对：Detach 必须解除对 swapchain backbuffer 的
/// 一切引用（WGL 侧为 wglDXUnregisterObjectNV），随后 ResizeBuffers 才能成功。
/// </summary>
internal unsafe interface IRenderBackend : IDisposable
{
    /// <summary>创建 mpv render 上下文（GL 后端须先备好 get_proc_address；当前线程随后独占它）。</summary>
    MpvRenderContext CreateRenderContext(MpvHandle handle);

    /// <summary>绑定 swapchain backbuffer 为渲染目标（初始与每次 ResizeBuffers 之后调用）。</summary>
    void Attach(IDXGISwapChain1* swapChain, int pixelWidth, int pixelHeight);

    /// <summary>解除渲染目标绑定（ResizeBuffers 之前必须调用）。</summary>
    void Detach();

    /// <summary>把当前帧渲染进 backbuffer（不含 Present，由宿主统一 Present + ReportSwap）。</summary>
    void RenderFrame(MpvRenderContext context);
}

/// <summary>
/// mpv 渲染宿主：持有一条专用渲染线程，驱动后端把 mpv 帧直渲到 VideoView 的
/// swapchain backbuffer 并 Present。D3D 对象（device/context/swapchain）归 VideoView
/// 所有；VideoView 保证先 Dispose 本宿主（线程已退出）再释放 D3D 对象。
/// </summary>
internal sealed unsafe class MpvRenderHost : IDisposable
{
    // DXGI_ERROR_DEVICE_REMOVED / DXGI_ERROR_DEVICE_RESET
    private const uint DxgiErrorDeviceRemoved = 0x887A0005;
    private const uint DxgiErrorDeviceReset = 0x887A0007;

    private readonly MpvHandle _handle;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly IDXGISwapChain1* _swapChain;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _resizeLock = new();

    private readonly Thread _renderThread;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _resizePending;
    private volatile bool _disposeRequested;
    private volatile MpvRenderBackendKind _backendKind;
    private volatile bool _deviceLost;
    private Exception? _renderFailure;

    /// <summary>后端选择结果（渲染线程赋值，UI 线程只读，volatile 保证可见性）。</summary>
    public MpvRenderBackendKind Backend => _backendKind;

    /// <summary>
    /// Present/ResizeBuffers 遇到设备丢失且渲染线程已完全退出后触发（在渲染线程上触发；
    /// 订阅方必须自行封送 UI 线程）。此后本宿主不再可用，应由 VideoView 重建。
    /// </summary>
    internal event Action? DeviceLost;

    /// <summary>
    /// 非设备丢失类渲染失败（双后端均无法初始化或渲染线程异常终止）。在渲染线程
    /// 退出前触发（线程上下文，订阅方必须自行封送 UI 线程）。此后本宿主不再可用。
    /// </summary>
    internal event Action<Exception>? RenderFailed;

    /// <summary>
    /// 启动渲染宿主（立即拉起渲染线程；初始渲染尺寸取自 swapchain 当前描述）。
    /// D3D 指针必须保持有效直到 <see cref="Dispose"/> 返回。
    /// </summary>
    public MpvRenderHost(MpvHandle handle, ID3D11Device* device, ID3D11DeviceContext* context,
        IDXGISwapChain1* swapChain)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _device = device;
        _context = context;
        _swapChain = swapChain;
        _renderThread = new Thread(RenderThreadMain) { IsBackground = true, Name = "MpvRender" };
        _renderThread.Start();
    }

    /// <summary>
    /// 请求把渲染尺寸改为新像素尺寸（可在任意线程调用）。实际的 ResizeBuffers 与
    /// 渲染目标重建在渲染线程内串行执行（SPEC R12），完成后强制重绘一帧。
    /// </summary>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        if (_disposeRequested || pixelWidth <= 0 || pixelHeight <= 0)
            return;

        lock (_resizeLock)
        {
            _pendingWidth = pixelWidth;
            _pendingHeight = pixelHeight;
            _resizePending = true;
        }

        _signal.Release();
    }

    /// <summary>
    /// 停止渲染线程并完成全部渲染资源清理（mpv_render_context_free 与后端销毁都在
    /// 渲染线程上执行，满足 render.h 线程亲和）。不可在渲染线程自身上调用。
    /// </summary>
    public void Dispose()
    {
        _disposeRequested = true;
        _signal.Release();
        if (Environment.CurrentManagedThreadId != _renderThread.ManagedThreadId)
            _renderThread.Join();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RenderThreadMain()
    {
        IRenderBackend? backend = null;
        MpvRenderContext? renderContext = null;
        try
        {
            SwapChainDesc1 desc = default;
            int initialW = 0, initialH = 0;
            if (_swapChain->GetDesc1(&desc) >= 0 && desc.Width > 0 && desc.Height > 0)
            {
                initialW = (int)desc.Width;
                initialH = (int)desc.Height;
            }

            // 主方案 WGL_NV_DX_interop；初始化/首次绑定的任一环节失败自动回退 SW（SPEC §D3 回退路径）
            backend = WglDxInteropBackend.TryCreate(_device);
            if (backend != null)
            {
                try
                {
                    renderContext = backend.CreateRenderContext(_handle);
                    if (initialW > 0)
                        backend.Attach(_swapChain, initialW, initialH);
                    _backendKind = MpvRenderBackendKind.WglDxInterop;
                }
                catch
                {
                    renderContext?.Dispose();
                    renderContext = null;
                    backend.Dispose();
                    backend = null;
                }
            }

            if (backend == null)
            {
                backend = new SoftwareRenderBackend(_device, _context);
                renderContext = backend.CreateRenderContext(_handle);
                if (initialW > 0)
                    backend.Attach(_swapChain, initialW, initialH);
                _backendKind = MpvRenderBackendKind.Software;
            }

            renderContext.FrameReady += OnFrameReady;

            bool forceRedraw = false;
            while (true)
            {
                _signal.Wait();
                if (_disposeRequested)
                    break;

                int resizeW = 0, resizeH = 0;
                lock (_resizeLock)
                {
                    if (_resizePending)
                    {
                        resizeW = _pendingWidth;
                        resizeH = _pendingHeight;
                        _resizePending = false;
                    }
                }

                if (resizeW > 0)
                {
                    ApplyResize(backend, resizeW, resizeH);
                    if (_deviceLost)
                        break;
                    forceRedraw = true; // 暂停/无新帧时也要把当前帧重绘到新 backbuffer
                }

                // ADVANCED_CONTROL：update 聚合自上次 render 以来的更新标志
                MpvRenderUpdateFlag flags = renderContext.Update();
                if ((flags & MpvRenderUpdateFlag.Frame) != 0 || forceRedraw)
                {
                    forceRedraw = false;
                    backend.RenderFrame(renderContext);
                    int hr = _swapChain->Present(0, 0);
                    if (IsDeviceLostHResult(hr))
                    {
                        _deviceLost = true;
                        break;
                    }

                    if (hr >= 0)
                        renderContext.ReportSwap(); // Present 成功后回报交换间隔
                }
            }
        }
        catch (Exception ex)
        {
            // 渲染线程内任何失败都不允许逃逸为未处理异常（进程级崩溃）。
            // 非设备丢失类异常不触发重建（避免持久性故障造成重建死循环），
            // 记录后经 RenderFailed 上报，由 UI 层提示用户（否则表现为静默黑屏）。
            if (!_disposeRequested && !_deviceLost)
                _renderFailure = ex;
        }
        finally
        {
            // 线程亲和：render context 与后端都必须在本线程销毁。
            if (renderContext != null)
            {
                renderContext.FrameReady -= OnFrameReady;
                renderContext.Dispose();
            }

            backend?.Dispose();
        }

        if (_deviceLost)
            DeviceLost?.Invoke();
        else if (_renderFailure != null)
            RenderFailed?.Invoke(_renderFailure);
    }

    private void ApplyResize(IRenderBackend backend, int pixelWidth, int pixelHeight)
    {
        backend.Detach(); // ResizeBuffers 要求 backbuffer 无任何外部引用
        int hr = _swapChain->ResizeBuffers(2, (uint)pixelWidth, (uint)pixelHeight, Format.FormatUnknown, 0);
        if (IsDeviceLostHResult(hr))
        {
            _deviceLost = true;
            return;
        }

        if (hr < 0)
            throw new InvalidOperationException($"IDXGISwapChain::ResizeBuffers 失败 (0x{hr:X8})。");

        backend.Attach(_swapChain, pixelWidth, pixelHeight);
    }

    /// <summary>mpv update callback 链末端：只允许发信号，禁止调用任何 mpv API。</summary>
    private void OnFrameReady(object? sender, EventArgs e)
    {
        if (!_disposeRequested)
            _signal.Release();
    }

    private static bool IsDeviceLostHResult(int hr)
    {
        uint code = unchecked((uint)hr);
        return code == DxgiErrorDeviceRemoved || code == DxgiErrorDeviceReset;
    }
}
