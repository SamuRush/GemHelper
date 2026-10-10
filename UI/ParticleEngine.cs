using System;
using System.Text;
using System.Windows;
using Gem.Win32;

namespace Gem.UI;

/// <summary>
/// High-performance, zero-allocation particle and glitch simulation engine.
/// Manages shard physics, cursor repulsion, screen boundary & window AABB collisions,
/// and cyberpunk memory dump pseudo-decryption.
/// </summary>
public sealed partial class ParticleEngine
{
    public const int MaxShardCount = 350;

    private readonly ShardParticle[] _shards = new ShardParticle[MaxShardCount];
    private readonly GlitchCodeBlock[] _glitchBlocks = new GlitchCodeBlock[MaxGlitchBlocks];
    private readonly StringBuilder _glitchTextBuffer = new(512);
    private readonly Random _rand = new();

    private double _glitchSpawnTimer = 0.0;
    private bool _shardsExploded = false;

    public ShardParticle[] Shards => _shards;
    public GlitchCodeBlock[] GlitchBlocks => _glitchBlocks;
    public int ActiveShardCount { get; set; } = 32;
    public bool EnableMemoryGlitch { get; set; } = true;

    public ParticleEngine(Point center)
    {
        InitShards(center);
    }

    public void InitShards(Point center)
    {
        for (int i = 0; i < MaxShardCount; i++)
        {
            double angle = i * (2.0 * Math.PI / MaxShardCount);
            double baseRadius = 26.0 + (i % 6) * 6.0;
            double size = 3.2 + (i % 4) * 1.4;
            double rotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (1.6 + (i % 5) * 0.35);

            _shards[i] = new ShardParticle
            {
                BaseAngle = angle,
                BaseRadius = baseRadius,
                CurrentRadius = baseRadius,
                X = center.X + baseRadius * Math.Cos(angle),
                Y = center.Y + baseRadius * Math.Sin(angle),
                Vx = 0.0,
                Vy = 0.0,
                Size = size,
                Rotation = angle,
                RotSpeed = rotSpeed,
                Life = 0f,
                Active = false,
                IsStuck = false,
                StuckEdge = BoundaryEdge.None,
                StuckX = 0.0,
                StuckY = 0.0,
                StuckAngle = 0.0,
                MiniBar0 = 0f, MiniBar1 = 0f, MiniBar2 = 0f, MiniBar3 = 0f,
                MiniBar4 = 0f, MiniBar5 = 0f, MiniBar6 = 0f, MiniBar7 = 0f
            };
        }
    }

    public void ExplodeShards(Point center, float beatIntensity)
    {
        for (int i = 0; i < ActiveShardCount; i++)
        {
            // Directional cone burst: particles eject in focused fans/cones from the split core fissure
            double ejectionAngle;
            int mode = i % 4;
            if (mode == 0)
                ejectionAngle = -Math.PI / 2.0 + (_rand.NextDouble() - 0.5) * 0.70; // Upward cone
            else if (mode == 1)
                ejectionAngle = Math.PI / 2.0 + (_rand.NextDouble() - 0.5) * 0.70;  // Downward cone
            else if (mode == 2)
                ejectionAngle = ((_rand.Next(2) == 0) ? -1.0 : 1.0) * (Math.PI / 2.0 + (_rand.NextDouble() - 0.5) * 0.90);
            else
                ejectionAngle = _shards[i].BaseAngle + (_rand.NextDouble() - 0.5) * 0.50;

            double rad = _shards[i].BaseRadius * 0.4;
            _shards[i].X = center.X + rad * Math.Cos(ejectionAngle);
            _shards[i].Y = center.Y + rad * Math.Sin(ejectionAngle);

            // Initial speed increased by 4x: (550..1050 + beat*250) * 4.0 -> range 2200 to 5000 px/s
            double speed = (550.0 + _rand.NextDouble() * 500.0 + beatIntensity * 250.0) * 4.0;
            _shards[i].Vx = speed * Math.Cos(ejectionAngle);
            _shards[i].Vy = speed * Math.Sin(ejectionAngle);
            _shards[i].RotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (4.5 + _rand.NextDouble() * 6.5);
            _shards[i].IsStuck = false;
            _shards[i].StuckEdge = BoundaryEdge.None;
            _shards[i].Active = true;
            _shards[i].Life = 1.0f;
        }
        _shardsExploded = true;
    }

    public void UpdatePhysics(
        double dt,
        Point center,
        double boundsW,
        double boundsH,
        bool isOverdriveActive,
        double overdriveProgress,
        float beatIntensity,
        float subBassEnergy,
        double falloffSetting,
        float[] currentSpectrum,
        WindowBoundsTracker windowTracker,
        bool enableWindowCollisions,
        bool enableMouseRepulsion,
        Point? screenMousePos,
        bool isReassembling,
        ReassemblyPhase reassemblyPhase,
        ref double reassemblyPhaseTimer,
        out ReassemblyPhase nextReassemblyPhase)
    {
        nextReassemblyPhase = reassemblyPhase;

        if (isOverdriveActive && !_shardsExploded)
        {
            ExplodeShards(center, beatIntensity);
        }
        else if (!isOverdriveActive && !isReassembling)
        {
            _shardsExploded = false;
        }

        // Mouse repulsion physics
        if (enableMouseRepulsion && screenMousePos.HasValue)
        {
            double mx = screenMousePos.Value.X;
            double my = screenMousePos.Value.Y;
            double repRadius = 110.0;
            double repRadiusSq = repRadius * repRadius;

            for (int i = 0; i < ActiveShardCount; i++)
            {
                if (!_shards[i].Active || _shards[i].IsStuck) continue;

                double dx = _shards[i].X - mx;
                double dy = _shards[i].Y - my;
                double distSq = dx * dx + dy * dy;

                if (distSq < repRadiusSq && distSq > 1.0)
                {
                    double dist = Math.Sqrt(distSq);
                    double forceRatio = 1.0 - (dist / repRadius);
                    double forceMag = forceRatio * 950.0 * dt;
                    double nx = dx / dist;
                    double ny = dy / dist;

                    _shards[i].Vx += nx * forceMag;
                    _shards[i].Vy += ny * forceMag;
                }
            }
        }

        if (isOverdriveActive)
        {
            float decay = (float)Math.Pow(falloffSetting, dt * 60.0);
            for (int i = 0; i < ActiveShardCount; i++)
            {
                _shards[i].Life = (float)Math.Clamp(overdriveProgress * (0.65f + beatIntensity * 0.35f), 0.0, 1.0);
                _shards[i].Active = true;

                if (_shards[i].IsStuck)
                {
                    _shards[i].X = _shards[i].StuckX;
                    _shards[i].Y = _shards[i].StuckY;
                    UpdateMiniBars(ref _shards[i], decay, currentSpectrum);
                    continue;
                }

                _shards[i].Vx *= Math.Pow(0.94, dt * 60.0);
                _shards[i].Vy *= Math.Pow(0.94, dt * 60.0);

                double curSpeed = Math.Sqrt(_shards[i].Vx * _shards[i].Vx + _shards[i].Vy * _shards[i].Vy);
                double kickImpulse = (beatIntensity * 260.0 + subBassEnergy * 180.0) * dt;
                if (curSpeed > 1e-3)
                {
                    _shards[i].Vx += (_shards[i].Vx / curSpeed) * kickImpulse;
                    _shards[i].Vy += (_shards[i].Vy / curSpeed) * kickImpulse;
                }
                else
                {
                    _shards[i].Vx += kickImpulse * Math.Cos(_shards[i].Rotation);
                    _shards[i].Vy += kickImpulse * Math.Sin(_shards[i].Rotation);
                }

                double minSpeed = 160.0 + (i % 6) * 12.0;
                curSpeed = Math.Sqrt(_shards[i].Vx * _shards[i].Vx + _shards[i].Vy * _shards[i].Vy);
                if (curSpeed < minSpeed)
                {
                    double factor = curSpeed < 1e-3 ? minSpeed : (minSpeed / curSpeed);
                    _shards[i].Vx = curSpeed < 1e-3 ? (minSpeed * Math.Cos(_shards[i].Rotation)) : (_shards[i].Vx * factor);
                    _shards[i].Vy = curSpeed < 1e-3 ? (minSpeed * Math.Sin(_shards[i].Rotation)) : (_shards[i].Vy * factor);
                }
                else if (curSpeed > 5500.0)
                {
                    double factor = 5500.0 / curSpeed;
                    _shards[i].Vx *= factor;
                    _shards[i].Vy *= factor;
                }

                _shards[i].X += _shards[i].Vx * dt;
                _shards[i].Y += _shards[i].Vy * dt;
                _shards[i].Rotation += _shards[i].RotSpeed * dt * (1.0 + beatIntensity * 2.0);

                double dx = _shards[i].X - center.X;
                double dy = _shards[i].Y - center.Y;
                _shards[i].CurrentRadius = Math.Sqrt(dx * dx + dy * dy);

                CheckBoundaryCollisions(ref _shards[i], boundsW, boundsH);
                if (enableWindowCollisions && !_shards[i].IsStuck)
                {
                    CheckWindowCollisions(ref _shards[i], windowTracker);
                }

                if (!_shards[i].IsStuck)
                {
                    curSpeed = Math.Sqrt(_shards[i].Vx * _shards[i].Vx + _shards[i].Vy * _shards[i].Vy);
                    if (curSpeed < minSpeed)
                    {
                        double factor = curSpeed < 1e-3 ? minSpeed : (minSpeed / curSpeed);
                        _shards[i].Vx = curSpeed < 1e-3 ? (minSpeed * Math.Cos(_shards[i].Rotation)) : (_shards[i].Vx * factor);
                        _shards[i].Vy = curSpeed < 1e-3 ? (minSpeed * Math.Sin(_shards[i].Rotation)) : (_shards[i].Vy * factor);
                    }
                }
            }
        }
        else if (isReassembling && reassemblyPhase == ReassemblyPhase.Phase1_Absorption)
        {
            int activeCount = 0;
            double pullLerp = Math.Clamp(dt * (11.0 + (1.0 - reassemblyPhaseTimer) * 12.0), 0.0, 1.0);

            for (int i = 0; i < ActiveShardCount; i++)
            {
                if (!_shards[i].Active) continue;

                _shards[i].IsStuck = false;
                _shards[i].StuckEdge = BoundaryEdge.None;

                double dx = center.X - _shards[i].X;
                double dy = center.Y - _shards[i].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist <= 22.0)
                {
                    _shards[i].Active = false;
                    _shards[i].Life = 0f;
                    _shards[i].Vx = 0.0;
                    _shards[i].Vy = 0.0;
                }
                else
                {
                    activeCount++;
                    _shards[i].X += dx * pullLerp;
                    _shards[i].Y += dy * pullLerp;
                    _shards[i].Rotation += _shards[i].RotSpeed * dt * 2.0;
                    _shards[i].Life = (float)Math.Clamp(dist / 90.0, 0.2, 1.0);
                }
            }

            reassemblyPhaseTimer -= dt;
            if (activeCount == 0 || reassemblyPhaseTimer <= 0.0)
            {
                for (int i = 0; i < ActiveShardCount; i++)
                {
                    _shards[i].Active = false;
                    _shards[i].Life = 0f;
                }
                nextReassemblyPhase = ReassemblyPhase.DramaticPause;
                reassemblyPhaseTimer = 0.40;
            }
        }
        else
        {
            for (int i = 0; i < ActiveShardCount; i++)
            {
                _shards[i].Active = false;
                _shards[i].IsStuck = false;
            }
        }
    }
}
