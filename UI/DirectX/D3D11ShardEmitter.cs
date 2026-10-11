using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Gem.UI.DirectX;

/// <summary>
/// GPU instanced 2D shrapnel particle system with 4x explosion impulse,
/// chaotic screen boundary ricochets, and velocity-vector motion blur stretching.
/// </summary>
public sealed class D3D11ShardEmitter : IDisposable
{
    public struct ShardParticleState
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Size;
        public double Rotation;
        public double RotSpeed;
        public float Alpha;
        public bool Active;
        public Vector4 Color;
    }

    private readonly int _maxCount;
    private readonly ShardParticleState[] _shards;
    private readonly ShardInstanceData[] _instanceBufferCpu;
    private readonly Random _rand = new(42);

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _instanceBufferGpu;
    private bool _disposed;

    public int Count => _maxCount;
    public ShardParticleState[] Shards => _shards;
    public int ActiveCount { get; private set; }

    public D3D11ShardEmitter(int count = 42)
    {
        _maxCount = Math.Clamp(count, 30, 200);
        _shards = new ShardParticleState[_maxCount];
        _instanceBufferCpu = new ShardInstanceData[_maxCount];
        InitParticles();
    }

    private void InitParticles()
    {
        for (int i = 0; i < _maxCount; i++)
        {
            double angle = i * (2.0 * Math.PI / _maxCount);
            _shards[i] = new ShardParticleState
            {
                X = 960.0,
                Y = 540.0,
                Vx = 0.0,
                Vy = 0.0,
                Size = 5.0 + _rand.NextDouble() * 8.0,
                Rotation = angle,
                RotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (3.0 + _rand.NextDouble() * 5.0),
                Alpha = 0.0f,
                Active = false,
                // Neon crimson #FF003C or glowing cyan #00E5FF
                Color = (i % 5 == 0)
                    ? new Vector4(0.0f, 0.898f, 1.0f, 1.0f)
                    : new Vector4(1.0f, 0.0f, 0.235f, 1.0f)
            };
        }
    }

    public void InitializeBuffers(ID3D11Device device)
    {
        if (_vertexBuffer != null) return;

        // 4-vertex 2D diamond shrapnel polygon template (TriangleStrip: [head, right, tail, left])
        var quadVertices = new QuadVertex[]
        {
            new( 1.0f,  0.0f), // Head tip (leading forward)
            new( 0.0f,  1.0f), // Right wing
            new(-1.0f,  0.0f), // Tail (stretches along velocity)
            new( 0.0f, -1.0f)  // Left wing
        };

        var vDesc = new BufferDescription
        {
            ByteWidth = (uint)(Marshal.SizeOf<QuadVertex>() * quadVertices.Length),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.None
        };
        _vertexBuffer = device.CreateBuffer(quadVertices.AsSpan(), vDesc);

        // Dynamic instance buffer for ShardInstanceData
        var instDesc = new BufferDescription
        {
            ByteWidth = (uint)(Marshal.SizeOf<ShardInstanceData>() * _maxCount),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        _instanceBufferGpu = device.CreateBuffer(instDesc);
    }

    public void Explode(Point center, float beatIntensity)
    {
        for (int i = 0; i < _maxCount; i++)
        {
            double angle = (i * (2.0 * Math.PI / _maxCount)) + ((_rand.NextDouble() - 0.5) * 0.4);

            // Initial explosion impulse increased by 4x: speeds 2200 to 5000 px/s!
            double speed = (550.0 + _rand.NextDouble() * 550.0 + beatIntensity * 250.0) * 4.0;
            _shards[i].X = center.X;
            _shards[i].Y = center.Y;
            _shards[i].Vx = speed * Math.Cos(angle);
            _shards[i].Vy = speed * Math.Sin(angle);
            _shards[i].RotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (4.5 + _rand.NextDouble() * 7.5);
            _shards[i].Alpha = 1.0f;
            _shards[i].Active = true;
        }
    }

    public void UpdatePhysics(
        double dt,
        Point center,
        double boundsW,
        double boundsH,
        bool isOverdriveActive,
        double progress,
        float beatIntensity)
    {
        double w = boundsW > 100 ? boundsW : 1920.0;
        double h = boundsH > 100 ? boundsH : 1080.0;
        int active = 0;

        for (int i = 0; i < _maxCount; i++)
        {
            if (!_shards[i].Active) continue;

            // Velocity integration with high-speed air resistance
            _shards[i].Vx *= Math.Pow(0.965, dt * 60.0);
            _shards[i].Vy *= Math.Pow(0.965, dt * 60.0);

            _shards[i].X += _shards[i].Vx * dt;
            _shards[i].Y += _shards[i].Vy * dt;
            _shards[i].Rotation += _shards[i].RotSpeed * dt * (1.0 + beatIntensity * 2.0);

            // Chaotic screen boundary ricochets with elastic bounce
            double bounce = 0.88 + _rand.NextDouble() * 0.08;
            if (_shards[i].X <= 2.0)
            {
                _shards[i].X = 2.0;
                _shards[i].Vx = Math.Abs(_shards[i].Vx) * bounce;
                _shards[i].Vy += (_rand.NextDouble() - 0.5) * Math.Abs(_shards[i].Vx) * 0.25;
            }
            else if (_shards[i].X >= w - 2.0)
            {
                _shards[i].X = w - 2.0;
                _shards[i].Vx = -Math.Abs(_shards[i].Vx) * bounce;
                _shards[i].Vy += (_rand.NextDouble() - 0.5) * Math.Abs(_shards[i].Vx) * 0.25;
            }

            if (_shards[i].Y <= 2.0)
            {
                _shards[i].Y = 2.0;
                _shards[i].Vy = Math.Abs(_shards[i].Vy) * bounce;
                _shards[i].Vx += (_rand.NextDouble() - 0.5) * Math.Abs(_shards[i].Vy) * 0.25;
            }
            else if (_shards[i].Y >= h - 2.0)
            {
                _shards[i].Y = h - 2.0;
                _shards[i].Vy = -Math.Abs(_shards[i].Vy) * bounce;
                _shards[i].Vx += (_rand.NextDouble() - 0.5) * Math.Abs(_shards[i].Vy) * 0.25;
            }

            _shards[i].Alpha = (float)Math.Clamp(progress * (0.6f + beatIntensity * 0.4f), 0.0, 1.0);

            double speed = Math.Sqrt(_shards[i].Vx * _shards[i].Vx + _shards[i].Vy * _shards[i].Vy);
            float stretch = (float)Math.Clamp(speed * 0.015, 1.0, 5.0);

            _instanceBufferCpu[active] = new ShardInstanceData
            {
                Position = new Vector2((float)_shards[i].X, (float)_shards[i].Y),
                Velocity = new Vector2((float)_shards[i].Vx, (float)_shards[i].Vy),
                Size = (float)(_shards[i].Size * (1.0 + beatIntensity * 0.45)),
                Rotation = (float)_shards[i].Rotation,
                Alpha = _shards[i].Alpha,
                Stretch = stretch,
                Color = _shards[i].Color
            };
            active++;
        }

        ActiveCount = active;
    }

    public void Render(ID3D11DeviceContext context)
    {
        if (_vertexBuffer == null || _instanceBufferGpu == null || ActiveCount == 0) return;

        // Map and upload CPU instance array to GPU buffer
        var mapped = context.Map(_instanceBufferGpu, 0, MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            fixed (ShardInstanceData* pSrc = _instanceBufferCpu)
            {
                Buffer.MemoryCopy(
                    pSrc,
                    mapped.DataPointer.ToPointer(),
                    ActiveCount * sizeof(ShardInstanceData),
                    ActiveCount * sizeof(ShardInstanceData));
            }
        }
        context.Unmap(_instanceBufferGpu, 0);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        uint stride0 = (uint)Marshal.SizeOf<QuadVertex>();
        uint stride1 = (uint)Marshal.SizeOf<ShardInstanceData>();
        uint offset = 0;

        context.IASetVertexBuffers(0, 1, new[] { _vertexBuffer }, new[] { stride0 }, new[] { offset });
        context.IASetVertexBuffers(1, 1, new[] { _instanceBufferGpu }, new[] { stride1 }, new[] { offset });

        context.DrawInstanced(4, (uint)ActiveCount, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vertexBuffer?.Dispose();
        _instanceBufferGpu?.Dispose();
    }
}
