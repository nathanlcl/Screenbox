// Screenbox.VideoView — mpv 软渲染回退后端（SPEC §D3 路线 C）。
// MPV_RENDER_API_TYPE_SW 输出 BGR0 CPU 位图 → ID3D11DeviceContext::UpdateSubresource
// 上传到同尺寸 Default 纹理 → CopyResource 到 swapchain backbuffer（由宿主 Present）。
// 无 GL 依赖，arm64/远程桌面/无 WGL_NV_DX_interop 驱动时兜底。
// 线程约束：全部方法只在 MpvRenderHost 的渲染线程调用（MpvRenderContext 线程亲和）。

using System;
using System.Runtime.InteropServices;
using Screenbox.Mpv;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Screenbox.Controls;

internal sealed unsafe class SoftwareRenderBackend : IRenderBackend
{
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;

    private IDXGISwapChain1* _swapChain;
    private ID3D11Texture2D* _stagingTexture;
    private void* _pixelBuffer;
    private nuint _pixelBufferSize;
    private int _width;
    private int _height;
    private bool _disposed;

    internal SoftwareRenderBackend(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        _device = device;
        _context = context;
    }

    /// <inheritdoc/>
    public MpvRenderContext CreateRenderContext(MpvHandle handle) => MpvRenderContext.CreateSoftware(handle);

    /// <inheritdoc/>
    public void Attach(IDXGISwapChain1* swapChain, int pixelWidth, int pixelHeight)
    {
        Detach();

        // bgr0 内存序为 B,G,R,X，与 DXGI_FORMAT_B8G8R8A8_UNORM 逐字节一致，无需转换。
        Texture2DDesc desc = new()
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = 0,      // 仅作 UpdateSubresource 目标 + CopyResource 源
            CPUAccessFlags = 0, // Default usage 不允许 CPU 访问
            MiscFlags = 0,
        };

        ID3D11Texture2D* texture = null;
        int hr = _device->CreateTexture2D(&desc, null, &texture);
        if (hr < 0 || texture == null)
            throw new InvalidOperationException($"ID3D11Device::CreateTexture2D 失败 (0x{hr:X8})。");

        _swapChain = swapChain;
        _stagingTexture = texture;
        _width = pixelWidth;
        _height = pixelHeight;
        _pixelBufferSize = (nuint)pixelWidth * (nuint)pixelHeight * 4;
        _pixelBuffer = NativeMemory.Alloc(_pixelBufferSize);
    }

    /// <inheritdoc/>
    public void Detach()
    {
        if (_stagingTexture != null)
        {
            _stagingTexture->Release();
            _stagingTexture = null;
        }

        if (_pixelBuffer != null)
        {
            NativeMemory.Free(_pixelBuffer);
            _pixelBuffer = null;
        }

        _pixelBufferSize = 0;
        _width = _height = 0;
        _swapChain = null;
    }

    /// <inheritdoc/>
    public void RenderFrame(MpvRenderContext context)
    {
        if (_stagingTexture == null || _pixelBuffer == null || _swapChain == null)
            return;

        int stride = _width * 4;
        context.RenderSoftware((nint)_pixelBuffer, _width, _height, stride);

        // CPU 位图上传（bgr0 与 B8G8R8A8 同序，整帧一次 UpdateSubresource）
        _context->UpdateSubresource(
            (ID3D11Resource*)_stagingTexture, 0, null, _pixelBuffer, (uint)stride, 0);

        // Silk.NET GetBuffer 泛型重载为 out 参数（故不能用 using 声明，显式 try/finally 释放）。
        Silk.NET.Core.Native.ComPtr<ID3D11Texture2D> backBuffer;
        int hr = _swapChain->GetBuffer(0, out backBuffer);
        try
        {
            if (hr < 0 || backBuffer.Handle == null)
                return;

            _context->CopyResource((ID3D11Resource*)backBuffer.Handle, (ID3D11Resource*)_stagingTexture);
        }
        finally
        {
            backBuffer.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }
}
