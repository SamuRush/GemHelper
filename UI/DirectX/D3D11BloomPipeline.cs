using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Gem.UI.DirectX;

/// <summary>
/// Hardware Postprocessing HDR Bloom and Neon Glow pipeline:
/// Extracts bright highlights (#FFFFFF / #FF003C / #00E5FF) and applies 2-pass separable Gaussian blur.
/// </summary>
public sealed class D3D11BloomPipeline : IDisposable
{
    private ID3D11Texture2D? _tex1;
    private ID3D11RenderTargetView? _rtv1;
    private ID3D11ShaderResourceView? _srv1;

    private ID3D11Texture2D? _tex2;
    private ID3D11RenderTargetView? _rtv2;
    private ID3D11ShaderResourceView? _srv2;

    private ID3D11Buffer? _bloomCBuffer;
    private int _bloomWidth;
    private int _bloomHeight;
    private bool _disposed;

    public void Initialize(ID3D11Device device, int screenWidth, int screenHeight)
    {
        _bloomWidth = Math.Max(screenWidth / 2, 64);
        _bloomHeight = Math.Max(screenHeight / 2, 64);

        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<BloomCBuffer>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _bloomCBuffer = device.CreateBuffer(cbDesc);

        CreateRenderTargets(device, _bloomWidth, _bloomHeight);
    }

    private void CreateRenderTargets(ID3D11Device device, int width, int height)
    {
        DisposeRenderTargets();

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

        _tex1 = device.CreateTexture2D(desc);
        _rtv1 = device.CreateRenderTargetView(_tex1);
        _srv1 = device.CreateShaderResourceView(_tex1);

        _tex2 = device.CreateTexture2D(desc);
        _rtv2 = device.CreateRenderTargetView(_tex2);
        _srv2 = device.CreateShaderResourceView(_tex2);
    }

    public void RenderBloom(
        ID3D11DeviceContext context,
        D3D11Shaders shaders,
        ID3D11ShaderResourceView sceneSrv,
        ID3D11RenderTargetView backBufferRtv,
        ID3D11BlendState additiveBlend,
        int fullWidth,
        int fullHeight,
        float intensity)
    {
        if (_rtv1 == null || _rtv2 == null || _srv1 == null || _srv2 == null || _bloomCBuffer == null) return;
        if (intensity <= 0.05f) return;

        var bloomVp = new Viewport(0, 0, _bloomWidth, _bloomHeight, 0.0f, 1.0f);
        var fullVp = new Viewport(0, 0, fullWidth, fullHeight, 0.0f, 1.0f);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.VSSetShader(shaders.BloomVS);

        // 1. Bright Pass Extraction from scene -> _rtv1
        context.RSSetViewport(bloomVp);
        context.OMSetRenderTargets(_rtv1);
        context.ClearRenderTargetView(_rtv1, new Color4(0, 0, 0, 0));

        UpdateConstantBuffer(context, 0.45f, intensity * 2.2f, new Vector2(0, 0), _bloomWidth, _bloomHeight);
        context.PSSetConstantBuffer(0, _bloomCBuffer);
        context.PSSetShader(shaders.BloomExtractPS);
        context.PSSetShaderResource(0, sceneSrv);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);

        // 2. Horizontal Gaussian Blur: _srv1 -> _rtv2
        context.OMSetRenderTargets(_rtv2);
        context.ClearRenderTargetView(_rtv2, new Color4(0, 0, 0, 0));

        UpdateConstantBuffer(context, 0.0f, 1.0f, new Vector2(2.5f, 0.0f), _bloomWidth, _bloomHeight);
        context.PSSetShader(shaders.BloomBlurPS);
        context.PSSetShaderResource(0, _srv1);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);

        // 3. Vertical Gaussian Blur: _srv2 -> _rtv1
        context.OMSetRenderTargets(_rtv1);
        context.ClearRenderTargetView(_rtv1, new Color4(0, 0, 0, 0));

        UpdateConstantBuffer(context, 0.0f, 1.0f, new Vector2(0.0f, 2.5f), _bloomWidth, _bloomHeight);
        context.PSSetShader(shaders.BloomBlurPS);
        context.PSSetShaderResource(0, _srv2);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);

        // 4. Additive Composite back into BackBuffer
        context.RSSetViewport(fullVp);
        context.OMSetRenderTargets(backBufferRtv);
        context.OMSetBlendState(additiveBlend);

        UpdateConstantBuffer(context, 0.0f, 1.0f, new Vector2(0, 0), fullWidth, fullHeight);
        context.PSSetShader(shaders.BloomBlurPS);
        context.PSSetShaderResource(0, _srv1);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);
    }

    private void UpdateConstantBuffer(
        ID3D11DeviceContext context,
        float threshold,
        float bloomIntensity,
        Vector2 direction,
        int width,
        int height)
    {
        var cb = new BloomCBuffer
        {
            Threshold = threshold,
            BloomIntensity = bloomIntensity,
            BlurDirection = direction,
            Resolution = new Vector2(width, height),
            Padding = Vector2.Zero
        };

        var mapped = context.Map(_bloomCBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(cb, mapped.DataPointer, false);
        context.Unmap(_bloomCBuffer!, 0);
    }

    private void DisposeRenderTargets()
    {
        _srv1?.Dispose();
        _rtv1?.Dispose();
        _tex1?.Dispose();
        _srv1 = null;
        _rtv1 = null;
        _tex1 = null;

        _srv2?.Dispose();
        _rtv2?.Dispose();
        _tex2?.Dispose();
        _srv2 = null;
        _rtv2 = null;
        _tex2 = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bloomCBuffer?.Dispose();
        DisposeRenderTargets();
    }
}
