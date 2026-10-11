using System;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Gem.UI.DirectX;

/// <summary>
/// Compiles and manages DirectX 11 Vertex and Pixel Shaders and Input Layouts.
/// </summary>
public sealed class D3D11Shaders : IDisposable
{
    private readonly ID3D11Device _device;
    private bool _disposed;

    public ID3D11VertexShader? RefractionVS { get; private set; }
    public ID3D11PixelShader? RefractionPS { get; private set; }

    public ID3D11VertexShader? LightningVS { get; private set; }
    public ID3D11PixelShader? LightningPS { get; private set; }
    public ID3D11InputLayout? LightningInputLayout { get; private set; }

    public ID3D11VertexShader? ShardVS { get; private set; }
    public ID3D11PixelShader? ShardPS { get; private set; }
    public ID3D11InputLayout? ShardInputLayout { get; private set; }

    public ID3D11VertexShader? BloomVS { get; private set; }
    public ID3D11PixelShader? BloomExtractPS { get; private set; }
    public ID3D11PixelShader? BloomBlurPS { get; private set; }

    public D3D11Shaders(ID3D11Device device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public void CompileAll()
    {
        // 1. Refraction
        var vsBlob = CompileShader(D3D11ShaderSources.RefractionHlsl, "VSMain", "vs_5_0");
        RefractionVS = _device.CreateVertexShader(vsBlob.AsSpan());
        vsBlob.Dispose();

        var psBlob = CompileShader(D3D11ShaderSources.RefractionHlsl, "PSMain", "ps_5_0");
        RefractionPS = _device.CreatePixelShader(psBlob.AsSpan());
        psBlob.Dispose();

        // 2. Lightning
        var lvsBlob = CompileShader(D3D11ShaderSources.LightningHlsl, "VSMain", "vs_5_0");
        LightningVS = _device.CreateVertexShader(lvsBlob.AsSpan());

        var lightningElements = new InputElementDescription[]
        {
            new("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new("THICKNESS", 0, Format.R32_Float, 8, 0, InputClassification.PerVertexData, 0),
            new("INTENSITY", 0, Format.R32_Float, 12, 0, InputClassification.PerVertexData, 0),
            new("COLOR", 0, Format.R32G32B32A32_Float, 16, 0, InputClassification.PerVertexData, 0),
        };
        LightningInputLayout = _device.CreateInputLayout(lightningElements, lvsBlob.AsSpan());
        lvsBlob.Dispose();

        var lpsBlob = CompileShader(D3D11ShaderSources.LightningHlsl, "PSMain", "ps_5_0");
        LightningPS = _device.CreatePixelShader(lpsBlob.AsSpan());
        lpsBlob.Dispose();

        // 3. Instanced Shards
        var svsBlob = CompileShader(D3D11ShaderSources.ShardHlsl, "VSMain", "vs_5_0");
        ShardVS = _device.CreateVertexShader(svsBlob.AsSpan());

        var shardElements = new InputElementDescription[]
        {
            new("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new("INSTANCE_POS", 0, Format.R32G32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_VEL", 0, Format.R32G32_Float, 8, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_SIZE", 0, Format.R32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_ROT", 0, Format.R32_Float, 20, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_ALPHA", 0, Format.R32_Float, 24, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_STRETCH", 0, Format.R32_Float, 28, 1, InputClassification.PerInstanceData, 1),
            new("INSTANCE_COLOR", 0, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
        };
        ShardInputLayout = _device.CreateInputLayout(shardElements, svsBlob.AsSpan());
        svsBlob.Dispose();

        var spsBlob = CompileShader(D3D11ShaderSources.ShardHlsl, "PSMain", "ps_5_0");
        ShardPS = _device.CreatePixelShader(spsBlob.AsSpan());
        spsBlob.Dispose();

        // 4. Bloom
        var bvsBlob = CompileShader(D3D11ShaderSources.BloomHlsl, "VSMain", "vs_5_0");
        BloomVS = _device.CreateVertexShader(bvsBlob.AsSpan());
        bvsBlob.Dispose();

        var bePsBlob = CompileShader(D3D11ShaderSources.BloomHlsl, "PSExtract", "ps_5_0");
        BloomExtractPS = _device.CreatePixelShader(bePsBlob.AsSpan());
        bePsBlob.Dispose();

        var bbPsBlob = CompileShader(D3D11ShaderSources.BloomHlsl, "PSBlur", "ps_5_0");
        BloomBlurPS = _device.CreatePixelShader(bbPsBlob.AsSpan());
        bbPsBlob.Dispose();
    }

    private static Vortice.Direct3D.Blob CompileShader(string hlsl, string entryPoint, string target)
    {
        var result = Compiler.Compile(hlsl, entryPoint, "shader.hlsl", target, out var blob, out var errorBlob);
        if (result.Failure || blob == null)
        {
            string err = errorBlob != null ? errorBlob.AsString() : "Unknown HLSL compilation error";
            errorBlob?.Dispose();
            throw new InvalidOperationException($"HLSL compilation failed for {entryPoint} [{target}]: {err}");
        }
        errorBlob?.Dispose();
        return blob;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RefractionVS?.Dispose();
        RefractionPS?.Dispose();

        LightningVS?.Dispose();
        LightningPS?.Dispose();
        LightningInputLayout?.Dispose();

        ShardVS?.Dispose();
        ShardPS?.Dispose();
        ShardInputLayout?.Dispose();

        BloomVS?.Dispose();
        BloomExtractPS?.Dispose();
        BloomBlurPS?.Dispose();
    }
}
