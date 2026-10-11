using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Gem.UI.DirectX;

/// <summary>
/// Procedural plasma lightning arcs with dynamic vertex jitter along glass fractures.
/// </summary>
public sealed class D3D11LightningPass : IDisposable
{
    private const int MaxVertices = 512;
    private readonly LightningVertex[] _vertexArray = new LightningVertex[MaxVertices];
    private ID3D11Buffer? _vertexBuffer;
    private int _vertexCount = 0;
    private readonly Random _rand = new(1337);
    private bool _disposed;

    public void Initialize(ID3D11Device device)
    {
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(Marshal.SizeOf<LightningVertex>() * MaxVertices),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _vertexBuffer = device.CreateBuffer(desc);
    }

    public void GenerateLightningArcs(Point center, double progress, float beatIntensity)
    {
        _vertexCount = 0;
        if (progress <= 0.05) return;

        int arcCount = 4 + (int)(beatIntensity * 4);
        double maxDist = 350.0 + beatIntensity * 250.0;

        for (int a = 0; a < arcCount; a++)
        {
            if (_vertexCount + 16 >= MaxVertices) break;

            double baseAngle = a * (2.0 * Math.PI / arcCount) + (_rand.NextDouble() - 0.5) * 0.4;
            Vector2 cur = new((float)center.X, (float)center.Y);

            // Alternate colors: Neon Scarlet #FF003C or Cyan #00E5FF
            Vector4 color = (a % 2 == 0)
                ? new Vector4(1.0f, 0.0f, 0.235f, 1.0f)
                : new Vector4(0.0f, 0.898f, 1.0f, 1.0f);

            int segments = 8;
            double segLen = maxDist / segments;

            for (int s = 0; s < segments; s++)
            {
                if (_vertexCount + 2 >= MaxVertices) break;

                double angle = baseAngle + (_rand.NextDouble() - 0.5) * 0.65;
                Vector2 next = cur + new Vector2((float)(segLen * Math.Cos(angle)), (float)(segLen * Math.Sin(angle)));

                float intensity = (float)(1.0f - (float)s / segments) * (1.2f + beatIntensity * 0.8f);

                _vertexArray[_vertexCount++] = new LightningVertex
                {
                    Position = cur,
                    Thickness = 3.5f,
                    Intensity = intensity,
                    Color = color
                };

                _vertexArray[_vertexCount++] = new LightningVertex
                {
                    Position = next,
                    Thickness = 2.0f,
                    Intensity = intensity,
                    Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) // Hot white core
                };

                cur = next;
            }
        }
    }

    public void Render(ID3D11DeviceContext context, D3D11Shaders shaders)
    {
        if (_vertexBuffer == null || _vertexCount < 2) return;

        var mapped = context.Map(_vertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (LightningVertex* pSrc = _vertexArray)
            {
                Buffer.MemoryCopy(
                    pSrc,
                    mapped.DataPointer.ToPointer(),
                    _vertexCount * sizeof(LightningVertex),
                    _vertexCount * sizeof(LightningVertex));
            }
        }
        context.Unmap(_vertexBuffer, 0);

        context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        context.IASetInputLayout(shaders.LightningInputLayout);

        uint stride = (uint)Marshal.SizeOf<LightningVertex>();
        uint offset = 0;
        context.IASetVertexBuffers(0, 1, new[] { _vertexBuffer }, new[] { stride }, new[] { offset });

        context.VSSetShader(shaders.LightningVS);
        context.PSSetShader(shaders.LightningPS);

        context.Draw((uint)_vertexCount, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vertexBuffer?.Dispose();
    }
}
