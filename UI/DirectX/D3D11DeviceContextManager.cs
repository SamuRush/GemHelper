using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Gem.UI.DirectX;

/// <summary>
/// Direct3D 11 device, immediate context, and transparent DXGI swap chain manager.
/// </summary>
public sealed class D3D11DeviceContextManager : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;

    private ID3D11BlendState? _premultipliedBlendState;
    private ID3D11BlendState? _additiveBlendState;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11SamplerState? _linearSamplerState;

    private int _width;
    private int _height;
    private bool _disposed;

    public ID3D11Device? Device => _device;
    public ID3D11DeviceContext? Context => _context;
    public IDXGISwapChain1? SwapChain => _swapChain;
    public ID3D11RenderTargetView? RenderTargetView => _renderTargetView;
    public ID3D11BlendState? PremultipliedBlendState => _premultipliedBlendState;
    public ID3D11BlendState? AdditiveBlendState => _additiveBlendState;
    public ID3D11RasterizerState? RasterizerState => _rasterizerState;
    public ID3D11SamplerState? LinearSamplerState => _linearSamplerState;

    public int Width => _width;
    public int Height => _height;

    public bool InitializeDevice()
    {
        if (_device != null) return true;

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        var flags = DeviceCreationFlags.BgraSupport;

        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;

        var hr = D3D11.D3D11CreateDevice(
            (IDXGIAdapter?)null,
            DriverType.Hardware,
            flags,
            featureLevels,
            out dev,
            out ctx);

        if (hr.Failure)
        {
            hr = D3D11.D3D11CreateDevice(
                (IDXGIAdapter?)null,
                DriverType.Warp,
                flags,
                featureLevels,
                out dev,
                out ctx);
        }

        if (hr.Failure || dev == null || ctx == null)
        {
            return false;
        }

        _device = dev;
        _context = ctx;

        CreateRenderStates();
        return true;
    }

    public SwapChainDescription1 CreateSwapChainDescription(int width, int height)
    {
        return new SwapChainDescription1
        {
            Width = (uint)Math.Max(width, 100),
            Height = (uint)Math.Max(height, 100),
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None
        };
    }

    public bool InitializeSwapChain(IntPtr hwnd, int width, int height)
    {
        if (_device == null)
        {
            if (!InitializeDevice()) return false;
        }

        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = CreateSwapChainDescription(_width, _height);
        try
        {
            _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
        }
        catch (Exception)
        {
            desc.AlphaMode = AlphaMode.Unspecified;
            _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
        }

        CreateBackBufferRtv();
        return true;
    }

    private void CreateBackBufferRtv()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        if (_swapChain == null || _device == null) return;

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);
    }

    private void CreateRenderStates()
    {
        if (_device == null) return;

        // 1. Premultiplied alpha blend state for transparent desktop compositing
        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _premultipliedBlendState = _device.CreateBlendState(blendDesc);

        // 2. Additive blend state for intense neon glow & Bloom
        var addDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        addDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.One,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _additiveBlendState = _device.CreateBlendState(addDesc);

        // 3. Rasterizer state (no backface culling)
        var rastDesc = new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            DepthClipEnable = false
        };
        _rasterizerState = _device.CreateRasterizerState(rastDesc);

        // 4. Linear sampler
        var sampDesc = new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp
        };
        _linearSamplerState = _device.CreateSamplerState(sampDesc);
    }

    public void Resize(int width, int height)
    {
        if (_swapChain == null || _disposed) return;
        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);

        _context?.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty);
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        _swapChain.ResizeBuffers(2, (uint)_width, (uint)_height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateBackBufferRtv();
    }

    public void SetViewport(int width, int height)
    {
        if (_context == null) return;
        var vp = new Viewport(0, 0, width, height, 0.0f, 1.0f);
        _context.RSSetViewport(vp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderTargetView?.Dispose();
        _premultipliedBlendState?.Dispose();
        _additiveBlendState?.Dispose();
        _rasterizerState?.Dispose();
        _linearSamplerState?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
