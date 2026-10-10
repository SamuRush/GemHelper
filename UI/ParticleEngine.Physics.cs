using System;

namespace Gem.UI;

public sealed partial class ParticleEngine
{
    private void CheckWindowCollisions(ref ShardParticle shard, WindowBoundsTracker windowTracker)
    {
        if (windowTracker.Count == 0) return;
        int count = windowTracker.Count;
        var bounds = windowTracker.Bounds;

        for (int i = 0; i < count; i++)
        {
            double left = bounds[i].Left;
            double top = bounds[i].Top;
            double right = bounds[i].Right;
            double bottom = bounds[i].Bottom;

            if (shard.X >= left && shard.X <= right && shard.Y >= top && shard.Y <= bottom)
            {
                double dLeft = shard.X - left;
                double dRight = right - shard.X;
                double dTop = shard.Y - top;
                double dBottom = bottom - shard.Y;

                double minD = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

                if (minD == dLeft)
                {
                    shard.X = left;
                    if (_rand.NextDouble() < 0.015)
                    {
                        shard.IsStuck = true;
                        shard.StuckEdge = BoundaryEdge.Left;
                        shard.StuckX = left;
                        shard.StuckY = shard.Y;
                        shard.StuckAngle = Math.PI;
                        shard.Vx = 0; shard.Vy = 0;
                        return;
                    }
                    shard.Vx = -Math.Abs(shard.Vx) * 0.85;
                    shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.25) * 0.85;
                }
                else if (minD == dRight)
                {
                    shard.X = right;
                    if (_rand.NextDouble() < 0.015)
                    {
                        shard.IsStuck = true;
                        shard.StuckEdge = BoundaryEdge.Right;
                        shard.StuckX = right;
                        shard.StuckY = shard.Y;
                        shard.StuckAngle = 0.0;
                        shard.Vx = 0; shard.Vy = 0;
                        return;
                    }
                    shard.Vx = Math.Abs(shard.Vx) * 0.85;
                    shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.25) * 0.85;
                }
                else if (minD == dTop)
                {
                    shard.Y = top;
                    if (_rand.NextDouble() < 0.015)
                    {
                        shard.IsStuck = true;
                        shard.StuckEdge = BoundaryEdge.Top;
                        shard.StuckX = shard.X;
                        shard.StuckY = top;
                        shard.StuckAngle = -Math.PI / 2.0;
                        shard.Vx = 0; shard.Vy = 0;
                        return;
                    }
                    shard.Vy = -Math.Abs(shard.Vy) * 0.85;
                    shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.25) * 0.85;
                }
                else
                {
                    shard.Y = bottom;
                    if (_rand.NextDouble() < 0.015)
                    {
                        shard.IsStuck = true;
                        shard.StuckEdge = BoundaryEdge.Bottom;
                        shard.StuckX = shard.X;
                        shard.StuckY = bottom;
                        shard.StuckAngle = Math.PI / 2.0;
                        shard.Vx = 0; shard.Vy = 0;
                        return;
                    }
                    shard.Vy = Math.Abs(shard.Vy) * 0.85;
                    shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.25) * 0.85;
                }
                break;
            }
        }
    }

    private void CheckBoundaryCollisions(ref ShardParticle shard, double boundsW, double boundsH)
    {
        if (shard.X <= 2.0)
        {
            shard.X = 2.0;
            if (_rand.NextDouble() < 0.015)
            {
                shard.IsStuck = true;
                shard.StuckEdge = BoundaryEdge.Left;
                shard.StuckX = 2.0;
                shard.StuckY = Math.Clamp(shard.Y, 2.0, boundsH - 2.0);
                shard.StuckAngle = 0.0;
                shard.Vx = 0; shard.Vy = 0;
                return;
            }
            double bounce = 0.82 + _rand.NextDouble() * 0.10;
            shard.Vx = Math.Abs(shard.Vx) * bounce;
            shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.35) * bounce;
        }
        else if (shard.X >= boundsW - 2.0)
        {
            shard.X = boundsW - 2.0;
            if (_rand.NextDouble() < 0.015)
            {
                shard.IsStuck = true;
                shard.StuckEdge = BoundaryEdge.Right;
                shard.StuckX = boundsW - 2.0;
                shard.StuckY = Math.Clamp(shard.Y, 2.0, boundsH - 2.0);
                shard.StuckAngle = Math.PI;
                shard.Vx = 0; shard.Vy = 0;
                return;
            }
            double bounce = 0.82 + _rand.NextDouble() * 0.10;
            shard.Vx = -Math.Abs(shard.Vx) * bounce;
            shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.35) * bounce;
        }

        if (shard.Y <= 2.0)
        {
            shard.Y = 2.0;
            if (_rand.NextDouble() < 0.015)
            {
                shard.IsStuck = true;
                shard.StuckEdge = BoundaryEdge.Top;
                shard.StuckX = Math.Clamp(shard.X, 2.0, boundsW - 2.0);
                shard.StuckY = 2.0;
                shard.StuckAngle = Math.PI / 2.0;
                shard.Vx = 0; shard.Vy = 0;
                return;
            }
            double bounce = 0.82 + _rand.NextDouble() * 0.10;
            shard.Vy = Math.Abs(shard.Vy) * bounce;
            shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.35) * bounce;
        }
        else if (shard.Y >= boundsH - 2.0)
        {
            shard.Y = boundsH - 2.0;
            if (_rand.NextDouble() < 0.015)
            {
                shard.IsStuck = true;
                shard.StuckEdge = BoundaryEdge.Bottom;
                shard.StuckX = Math.Clamp(shard.X, 2.0, boundsW - 2.0);
                shard.StuckY = boundsH - 2.0;
                shard.StuckAngle = -Math.PI / 2.0;
                shard.Vx = 0; shard.Vy = 0;
                return;
            }
            double bounce = 0.82 + _rand.NextDouble() * 0.10;
            shard.Vy = -Math.Abs(shard.Vy) * bounce;
            shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.35) * bounce;
        }
    }

    private void UpdateMiniBars(ref ShardParticle shard, float decay, float[] currentSpectrum)
    {
        UpdateSingleMiniBar(ref shard.MiniBar0, 2, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar1, 6, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar2, 12, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar3, 20, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar4, 28, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar5, 38, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar6, 48, decay, currentSpectrum);
        UpdateSingleMiniBar(ref shard.MiniBar7, 58, decay, currentSpectrum);
    }

    private void UpdateSingleMiniBar(ref float bar, int freqIdx, float decay, float[] currentSpectrum)
    {
        freqIdx = Math.Clamp(freqIdx, 0, currentSpectrum.Length - 1);
        float target = currentSpectrum[freqIdx];
        if (target > bar) bar = target;
        else bar = Math.Max(target, bar * decay);
    }
}
