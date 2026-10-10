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
                double bounce = 0.88 + _rand.NextDouble() * 0.08;

                if (minD == dLeft)
                {
                    shard.X = left - 1.0;
                    shard.Vx = -Math.Abs(shard.Vx) * bounce;
                    shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.20) * bounce;
                }
                else if (minD == dRight)
                {
                    shard.X = right + 1.0;
                    shard.Vx = Math.Abs(shard.Vx) * bounce;
                    shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.20) * bounce;
                }
                else if (minD == dTop)
                {
                    shard.Y = top - 1.0;
                    shard.Vy = -Math.Abs(shard.Vy) * bounce;
                    shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.20) * bounce;
                }
                else
                {
                    shard.Y = bottom + 1.0;
                    shard.Vy = Math.Abs(shard.Vy) * bounce;
                    shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.20) * bounce;
                }

                shard.IsStuck = false;
                shard.StuckEdge = BoundaryEdge.None;
                break;
            }
        }
    }

    private void CheckBoundaryCollisions(ref ShardParticle shard, double boundsW, double boundsH)
    {
        double bounce = 0.90 + _rand.NextDouble() * 0.06;

        if (shard.X <= 2.0)
        {
            shard.X = 2.0;
            shard.Vx = Math.Abs(shard.Vx) * bounce;
            shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.25) * bounce;
            shard.IsStuck = false;
        }
        else if (shard.X >= boundsW - 2.0)
        {
            shard.X = boundsW - 2.0;
            shard.Vx = -Math.Abs(shard.Vx) * bounce;
            shard.Vy = (shard.Vy + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vx) * 0.25) * bounce;
            shard.IsStuck = false;
        }

        if (shard.Y <= 2.0)
        {
            shard.Y = 2.0;
            shard.Vy = Math.Abs(shard.Vy) * bounce;
            shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.25) * bounce;
            shard.IsStuck = false;
        }
        else if (shard.Y >= boundsH - 2.0)
        {
            shard.Y = boundsH - 2.0;
            shard.Vy = -Math.Abs(shard.Vy) * bounce;
            shard.Vx = (shard.Vx + (_rand.NextDouble() - 0.5) * Math.Abs(shard.Vy) * 0.25) * bounce;
            shard.IsStuck = false;
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

    private static void UpdateSingleMiniBar(ref float bar, int bin, float decay, float[] spectrum)
    {
        float target = bin < spectrum.Length ? spectrum[bin] : 0f;
        if (target > bar) bar = target;
        else bar *= decay;
    }
}
