// Screenbox.VideoView — mpv 渲染宿主控件（SPEC §6.4）。
// 复用现状基建：自建 D3D11 device + CreateSwapChainForComposition（B8G8R8A8、
// FlipSequential、Scaling.Stretch、MaxFrameLatency=1）+ ISwapChainPanelNative.SetSwapChain
// + CompositionScale 逆矩阵。相对 VLC 时代的变化（SPEC §5.2 / info.md §3）：
// - 删除 MediaPlayer DP 与 --winrt-d3dcontext/--winrt-swapchain 句柄选项协议；
// - 删除 private-data GUID 尺寸协议（f1b59347…/6ea976a0…），mpv 模式 FBO 尺寸即渲染尺寸，
//   SizeChanged 必须走 ResizeBuffers（由 MpvRenderHost 在渲染线程串行执行）；
// - Initialized 事件退化为无参时序信号（swapchain 就绪）；
// - 新增 PlayerHandle DP（Screenbox.Mpv.MpvHandle），句柄到位即启动 MpvRenderHost。

using System;
using Screenbox.Mpv;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Controls;

public unsafe partial class VideoView : SwapChainPanel
{
    private D3D11 _d3d11;
    private DXGI _dxgi;

    private ComPtr<ID3D11Device> _d3d11Device;
    private ComPtr<ID3D11DeviceContext> _d3d11Context;
    private ComPtr<IDXGISwapChain1> _swapChain;

    private readonly DispatcherQueue _dispatcherQueue;
    private MpvRenderHost? _renderHost;
    private bool _loaded;

    /// <summary>swapchain 就绪的时序信号（SPEC：参数清空，不再携带任何选项）。</summary>
    public event EventHandler? Initialized;

    /// <summary>渲染层非设备丢失类失败（双后端均不可用等）。已在 UI 线程封送。</summary>
    public event EventHandler<Exception>? RenderFailed;

    public static readonly DependencyProperty PlayerHandleProperty = DependencyProperty.Register(
        nameof(PlayerHandle), typeof(MpvHandle), typeof(VideoView),
        new PropertyMetadata(null, OnPlayerHandleChanged));

    /// <summary>mpv 客户端句柄。swapchain 就绪且句柄非空时启动渲染宿主；句柄变更触发重建。</summary>
    public MpvHandle? PlayerHandle
    {
        get => (MpvHandle?)GetValue(PlayerHandleProperty);
        set => SetValue(PlayerHandleProperty, value);
    }

    /// <summary>当前渲染后端（诊断用）；渲染宿主未启动时为 null。</summary>
    public MpvRenderBackendKind? ActiveBackend => _renderHost?.Backend;

    public VideoView()
    {
        _d3d11 = new D3D11(new DefaultNativeContext("d3d11"));
        _dxgi = new DXGI(new DefaultNativeContext("dxgi"));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        SizeChanged += (s, e) =>
        {
            if (_loaded) UpdateSize();
            else CreateSwapChain();
        };
        CompositionScaleChanged += (s, e) =>
        {
            if (_loaded) UpdateScale();
        };
        Unloaded += (s, e) => DestroySwapChain();
    }

    private static void OnPlayerHandleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        VideoView view = (VideoView)d;
        if (view._loaded)
            view.StartRenderHost(); // 句柄变更/清空：重建或停止渲染宿主
    }

    private void CreateSwapChain()
    {
        if (ActualHeight == 0 || ActualWidth == 0) return;

        DestroySwapChain();

        // 1. Create D3D11 Device and Context (pass null to use default feature levels)
        _d3d11.CreateDevice(
            default(ComPtr<IDXGIAdapter>),
            D3DDriverType.Hardware,
            IntPtr.Zero,
            (uint)CreateDeviceFlag.BgraSupport,
            null,
            0,
            D3D11.SdkVersion,
            ref _d3d11Device,
            null,
            ref _d3d11Context
        );

        // 2. Query DXGI Factory from D3D11 Device
        using var dxgiDevice = _d3d11Device.QueryInterface<IDXGIDevice1>();
        ComPtr<IDXGIAdapter> dxgiAdapter = default;
        try
        {
            dxgiDevice.GetAdapter(ref dxgiAdapter);
            using var dxgiAdapter1 = dxgiAdapter.QueryInterface<IDXGIAdapter1>();
            using var dxgiFactory = dxgiAdapter1.GetParent<IDXGIFactory2>();

            // 3. Define Swap Chain Description
            SwapChainDesc1 scd = new()
            {
                Width = (uint)(ActualWidth * CompositionScaleX),
                Height = (uint)(ActualHeight * CompositionScaleY),
                Format = Format.FormatB8G8R8A8Unorm,
                Stereo = false,
                SampleDesc = new SampleDesc(1, 0),
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Unspecified
            };

            // 4. Create Swap Chain for Composition (using Handle to bypass extension method ambiguities)
            IDXGISwapChain1* swapChainPtr = null;
            int hr = dxgiFactory.Handle->CreateSwapChainForComposition(
                (IUnknown*)_d3d11Device.Handle,
                &scd,
                null,
                &swapChainPtr
            );
            SilkMarshal.ThrowHResult(hr);
            _swapChain = new ComPtr<IDXGISwapChain1>(swapChainPtr);
        }
        finally
        {
            dxgiAdapter.Dispose();
        }

        dxgiDevice.SetMaximumFrameLatency(1);

        // 5. Set Swap Chain on SwapChainPanel
        this.SetSwapChain((IUnknown*)_swapChain.Handle);

        _loaded = true;
        UpdateScale();
        UpdateSize();

        Initialized?.Invoke(this, EventArgs.Empty);
        StartRenderHost();
    }

    /// <summary>swapchain 就绪且 PlayerHandle 非空时启动渲染宿主；句柄为 null 则仅停止。</summary>
    private void StartRenderHost()
    {
        StopRenderHost();
        if (!_loaded || _swapChain.Handle == null || PlayerHandle == null)
            return;

        MpvRenderHost host = new(PlayerHandle, _d3d11Device.Handle, _d3d11Context.Handle, _swapChain.Handle);
        host.DeviceLost += OnRenderDeviceLost;
        host.RenderFailed += OnRenderFailed;
        _renderHost = host;
    }

    private void StopRenderHost()
    {
        if (_renderHost == null)
            return;

        _renderHost.DeviceLost -= OnRenderDeviceLost;
        _renderHost.RenderFailed -= OnRenderFailed;
        _renderHost.Dispose(); // Join 渲染线程；mpv_render_context_free 在渲染线程完成
        _renderHost = null;
    }

    /// <summary>渲染失败上报：渲染线程回调，封送回 UI 线程转发给订阅方。</summary>
    private void OnRenderFailed(Exception ex)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_loaded)
                RenderFailed?.Invoke(this, ex);
        });
    }

    /// <summary>设备丢失重建：渲染线程已退出，回 UI 线程整体重建 device/swapchain/渲染宿主。</summary>
    private void OnRenderDeviceLost()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_loaded)
                return;
            CreateSwapChain(); // 内部先 DestroySwapChain（含 StopRenderHost）再全量重建
        });
    }

    /// <summary>
    /// mpv 模式下 FBO 尺寸即渲染尺寸：SizeChanged → 渲染线程内 ResizeBuffers +
    /// 渲染目标重建 + 强制重绘（SPEC §D3 关键机制修正）。
    /// </summary>
    private void UpdateSize()
    {
        if (!_loaded || _swapChain.Handle == null) return;

        int w = (int)(ActualWidth * CompositionScaleX);
        int h = (int)(ActualHeight * CompositionScaleY);
        _renderHost?.Resize(w, h);
    }

    private void UpdateScale()
    {
        if (!_loaded || _swapChain.Handle == null) return;

        using var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        if (swapChain2.Handle != null)
        {
            var matrix = new Matrix3X2F(
                1.0f / CompositionScaleX, 0.0f,
                0.0f, 1.0f / CompositionScaleY,
                0.0f, 0.0f
            );
            swapChain2.Handle->SetMatrixTransform(&matrix);
        }
    }

    private void DestroySwapChain()
    {
        // 顺序硬约束：先停渲染宿主（线程退出后无人再碰 D3D 对象），再拆 swapchain/设备。
        StopRenderHost();

        if (_loaded)
        {
            try
            {
                this.SetSwapChain(null);
            }
            catch (ObjectDisposedException)
            {
                // Safe to ignore ObjectDisposedException during teardown
            }
        }

        _swapChain.Dispose();
        _d3d11Context.Dispose();
        _d3d11Device.Dispose();
        _loaded = false;
    }
}
