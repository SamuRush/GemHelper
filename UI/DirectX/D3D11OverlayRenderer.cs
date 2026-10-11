using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Gem.UI.DirectX;

/// <summary>
/// Top-level DirectX 11 transparent hardware overlay renderer orchestrating:
/// - Transparent Win32 Overlay Window and DXGI Premultiplied Alpha SwapChain
/// - Glass Refraction & Cracked Matrix UV displacement with chromatic aberration (3-5 px split)
/// - Procedural Plasma Lightning Arcs with dynamic vertex jitter
/// - GPU Instanced 2D Shards with 4x explosion impulse and velocity motion blur
/// - Hardware Bloom & Neon Glow postprocessing pass (#FFFFFF / #FF003C / #00E5FF)
/// </summary>
public sealed class D3D11OverlayRenderer : IDisposable
{
    private readonly D3D11OverlayWindow? _window;
    private readonly bool _ownsWindow;
    private readonly D3D11DeviceContextManager _deviceManager = new();
    private readonly D3D11ShardEmitter _shardEmitter = new(42);
    private readonly D3D11RefractionPass _refractionPass = new();
    private readonly D3D11LightningPass _lightningPass = new();
    private readonly D3D11BloomPipeline _bloomPipeline = new();

    private D3D11Shaders? _shaders;
    private ID3D11Buffer? _globalCBuffer;

    private ID3D11Texture2D? _sceneTexture;
    private ID3D11RenderTargetView? _sceneRtv;
    private ID3D11ShaderResourceView? _sceneSrv;

    private int _width;
    private int _height;
    private double _time;
    private bool _wasOverdriveActive;
    private bool _disposed;

    public D3D11OverlayWindow? Window => _window;
    public D3D11ShardEmitter ShardEmitter => _shardEmitter;
    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized { get; private set; }

    public D3D11OverlayRenderer(int width = 1920, int height = 1080)
    {
        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);
        _window = new D3D11OverlayWindow(0, 0, _width, _height);
        _ownsWindow = true;
    }

    public D3D11OverlayRenderer(D3D11OverlayWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _width = window.Width;
        _height = window.Height;
        _ownsWindow = false;
    }

    public bool Initialize()
    {
        if (IsInitialized) return true;

        if (!_deviceManager.InitializeDevice()) return false;
        var device = _deviceManager.Device!;
        var context = _deviceManager.Context!;

        IntPtr hwnd = _window != null ? _window.Handle : IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            if (!_deviceManager.InitializeSwapChain(hwnd, _width, _height)) return false;
        }

        _shaders = new D3D11Shaders(device);
        _shaders.CompileAll();

        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<GlobalCBuffer>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _globalCBuffer = device.CreateBuffer(cbDesc);

        _shardEmitter.InitializeBuffers(device);
        _refractionPass.Initialize(device, _width, _height);
        _lightningPass.Initialize(device);
        _bloomPipeline.Initialize(device, _width, _height);

        CreateSceneRenderTarget(device, _width, _height);

        IsInitialized = true;
        return true;
    }

    private void CreateSceneRenderTarget(ID3D11Device device, int width, int height)
    {
        _sceneSrv?.Dispose();
        _sceneRtv?.Dispose();
        _sceneTexture?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None
        };

        _sceneTexture = device.CreateTexture2D(desc);
        _sceneRtv = device.CreateRenderTargetView(_sceneTexture);
        _sceneSrv = device.CreateShaderResourceView(_sceneTexture);
    }

    public void RenderFrame(
        double dt,
        Point center,
        bool isOverdriveActive,
        double progress,
        float beatIntensity)
    {
        if (!IsInitialized || _deviceManager.Context == null || _shaders == null) return;

        _time += dt;
        _window?.ProcessMessages();

        var context = _deviceManager.Context;
        var rtv = _sceneRtv ?? _deviceManager.RenderTargetView;
        if (rtv == null) return;

        // Clear scene render target to transparent black (0, 0, 0, 0)
        context.ClearRenderTargetView(rtv, new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        if (_deviceManager.RenderTargetView != null && rtv != _deviceManager.RenderTargetView)
        {
            context.ClearRenderTargetView(_deviceManager.RenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        }

        // Trigger 4x explosion impulse on Overdrive activation edge
        if (isOverdriveActive && !_wasOverdriveActive)
        {
            _shardEmitter.Explode(center, beatIntensity);
        }
        _wasOverdriveActive = isOverdriveActive;

        // Update physics and procedure
        _shardEmitter.UpdatePhysics(dt, center, _width, _height, isOverdriveActive, progress, beatIntensity);
        _lightningPass.GenerateLightningArcs(center, progress, beatIntensity);

        // Upload global constant buffer
        if (_globalCBuffer != null)
        {
            var gcb = new GlobalCBuffer
            {
                Resolution = new Vector2(_width, _height),
                Time = (float)_time,
                BeatIntensity = beatIntensity,
                Center = new Vector2((float)center.X, (float)center.Y),
                Progress = (float)progress,
                WhiteoutIntensity = (float)(progress * (0.8f + beatIntensity * 0.4f))
            };
            var mapped = context.Map(_globalCBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(gcb, mapped.DataPointer, false);
            context.Unmap(_globalCBuffer, 0);

            context.VSSetConstantBuffer(0, _globalCBuffer);
            context.PSSetConstantBuffer(0, _globalCBuffer);
        }

        _deviceManager.SetViewport(_width, _height);
        context.OMSetRenderTargets(rtv);
        context.RSSetState(_deviceManager.RasterizerState);
        context.PSSetSampler(0, _deviceManager.LinearSamplerState);

        // 1. Render Glass Refraction & Cracked Matrix pass
        context.OMSetBlendState(_deviceManager.PremultipliedBlendState);
        _refractionPass.Render(context, _shaders, center, progress, _time, _width, _height);

        // 2. Render Instanced 2D Shards with Motion Blur
        if (_shardEmitter.ActiveCount > 0)
        {
            context.IASetInputLayout(_shaders.ShardInputLayout);
            context.VSSetShader(_shaders.ShardVS);
            context.PSSetShader(_shaders.ShardPS);
            _shardEmitter.Render(context);
        }

        // 3. Render Procedural Plasma Lightning Arcs
        context.OMSetBlendState(_deviceManager.AdditiveBlendState);
        _lightningPass.Render(context, _shaders);

        // 4. Hardware Postprocessing Bloom & Neon Glow pass
        if (_sceneSrv != null && _deviceManager.RenderTargetView != null && progress > 0.05)
        {
            _bloomPipeline.RenderBloom(
                context,
                _shaders,
                _sceneSrv,
                _deviceManager.RenderTargetView,
                _deviceManager.AdditiveBlendState!,
                _width,
                _height,
                (float)(progress * (0.85f + beatIntensity * 0.6f)));
        }

        // Present transparent swapchain to DWM
        _deviceManager.SwapChain?.Present(1, PresentFlags.None);
    }

    public void Resize(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = Math.Max(width, 100);
        _height = Math.Max(height, 100);

        _window?.Resize(_width, _height);
        _deviceManager.Resize(_width, _height);

        if (_deviceManager.Device != null)
        {
            CreateSceneRenderTarget(_deviceManager.Device, _width, _height);
            _bloomPipeline.Initialize(_deviceManager.Device, _width, _height);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _globalCBuffer?.Dispose();
        _sceneSrv?.Dispose();
        _sceneRtv?.Dispose();
        _sceneTexture?.Dispose();

        _bloomPipeline.Dispose();
        _lightningPass.Dispose();
        _refractionPass.Dispose();
        _shardEmitter.Dispose();
        _shaders?.Dispose();
        _deviceManager.Dispose();

        if (_ownsWindow)
        {
            _window?.Dispose();
        }
    }
}
