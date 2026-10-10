using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

public sealed partial class HudRenderer
{
    private void RenderOverrideTitle(DrawingContext dc, Point center, double now, OverdriveStateMachine overdriveState)
    {
        double cx = center.X;
        double cy = center.Y;
        bool showTitle = (overdriveState.SmoothedCharge >= 0.80 && !overdriveState.IsOverdriveActive) || overdriveState.DetonationTitleBurstTimer > 0.0;
        if (!showTitle) return;

        double titleScale = 1.0;
        double titleAlpha = 1.0;
        double jitterX = 0.0;
        double jitterY = 0.0;

        if (overdriveState.DetonationTitleBurstTimer > 0.0)
        {
            double burstElapsed = 2.0 - overdriveState.DetonationTitleBurstTimer;
            if (burstElapsed < 0.30)
            {
                double jumpT = burstElapsed / 0.30;
                titleScale = 1.0 + Math.Sin(jumpT * Math.PI) * 0.35;
            }
            else
            {
                titleScale = 1.0 + 0.05 * Math.Sin(now * 22.0);
            }
            titleAlpha = overdriveState.DetonationTitleBurstTimer < 0.50 ? Math.Clamp(overdriveState.DetonationTitleBurstTimer / 0.50, 0.0, 1.0) : 1.0;
        }
        else
        {
            double chargeIntensity = Math.Clamp((overdriveState.SmoothedCharge - 0.80) / 0.20, 0.0, 1.0);
            titleScale = 1.0 + chargeIntensity * 0.08 * Math.Sin(now * 15.0);
        }

        bool isFlickerVisible = (overdriveState.DetonationTitleBurstTimer > 0.0) || (Math.Sin(now * 38.0) > -0.45);
        if (isFlickerVisible && titleAlpha > 0.05)
        {
            _titleScaleTransform.ScaleX = titleScale;
            _titleScaleTransform.ScaleY = titleScale;
            _titleTranslateTransform.X = cx + jitterX;
            _titleTranslateTransform.Y = (cy - 88.0) + jitterY;

            dc.PushTransform(_titleTransformGroup);
            double glitchOffset = 2.4 + Math.Sin(now * 32.0) * 1.2;

            _titleTranslateTransform.X -= glitchOffset;
            dc.DrawGeometry(StaticCyanGlitchBrush, null, StaticOverrideTextGeometry);

            _titleTranslateTransform.X += (glitchOffset * 2.0);
            dc.DrawGeometry(StaticCrimsonGlitchBrush, null, StaticOverrideTextGeometry);

            _titleTranslateTransform.X -= glitchOffset;
            dc.DrawGeometry(StaticWhiteBrush, null, StaticOverrideTextGeometry);

            dc.Pop();
        }

        if (overdriveState.DetonationTitleBurstTimer > 1.4)
        {
            using (var sparkCtx = _dynamicTitleSparkGeometry.Open())
            {
                for (int k = 0; k < 6; k++)
                {
                    double sAngle = k * (Math.PI / 3.0) + now * 5.0;
                    double sDist0 = 12.0 * titleScale;
                    double sDist1 = 28.0 * titleScale;
                    Point sp0 = new Point(cx + sDist0 * Math.Cos(sAngle), (cy - 88.0) + sDist0 * Math.Sin(sAngle));
                    Point sp1 = new Point(cx + sDist1 * Math.Cos(sAngle), (cy - 88.0) + sDist1 * Math.Sin(sAngle));
                    sparkCtx.BeginFigure(sp0, false, false);
                    sparkCtx.LineTo(sp1, true, false);
                }
            }
            dc.DrawGeometry(null, StaticSparkPen, _dynamicTitleSparkGeometry);
        }
    }

    private void RenderShards(DrawingContext dc, ParticleEngine particleEngine, float beatIntensity, double gainSetting, ReactorPalette pal)
    {
        var shards = particleEngine.Shards;
        int activeCount = particleEngine.ActiveShardCount;

        using (var sCtx = _dynamicShardsGeometry.Open())
        {
            for (int i = 0; i < activeCount; i++)
            {
                if (!shards[i].Active) continue;

                double sx = shards[i].X;
                double sy = shards[i].Y;
                double size = shards[i].Size * (1.0 + 0.35 * beatIntensity);
                double speedSq = shards[i].Vx * shards[i].Vx + shards[i].Vy * shards[i].Vy;

                // Hypersonic Directional Cone Burst: Motion Streaks / Motion Blur needles along velocity
                if (speedSq > 220.0 * 220.0 && !shards[i].IsStuck)
                {
                    double speed = Math.Sqrt(speedSq);
                    double vxNorm = shards[i].Vx / speed;
                    double vyNorm = shards[i].Vy / speed;
                    double nx = -vyNorm;
                    double ny = vxNorm;

                    double streakLen = Math.Clamp(speed * 0.016 * 0.70, 14.0, 52.0);
                    double halfWidth = Math.Max(1.4, size * 0.32);

                    Point head = new Point(sx + vxNorm * (size * 0.6), sy + vyNorm * (size * 0.6));
                    Point side1 = new Point(sx + nx * halfWidth, sy + ny * halfWidth);
                    Point tail = new Point(sx - vxNorm * streakLen, sy - vyNorm * streakLen);
                    Point side2 = new Point(sx - nx * halfWidth, sy - ny * halfWidth);

                    sCtx.BeginFigure(head, true, true);
                    sCtx.LineTo(side1, true, false);
                    sCtx.LineTo(tail, true, false);
                    sCtx.LineTo(side2, true, false);
                }
                else
                {
                    double rot = shards[i].Rotation;
                    double cosR = Math.Cos(rot);
                    double sinR = Math.Sin(rot);
                    double tanX = -sinR;
                    double tanY = cosR;

                    Point sp0 = new Point(sx + size * cosR, sy + size * sinR);
                    Point sp1 = new Point(sx + size * 0.45 * tanX, sy + size * 0.45 * tanY);
                    Point sp2 = new Point(sx - size * 0.75 * cosR, sy - size * 0.75 * sinR);
                    Point sp3 = new Point(sx - size * 0.45 * tanX, sy - size * 0.45 * tanY);

                    sCtx.BeginFigure(sp0, true, true);
                    sCtx.LineTo(sp1, true, false);
                    sCtx.LineTo(sp2, true, false);
                    sCtx.LineTo(sp3, true, false);
                }
            }
        }
        dc.DrawGeometry(pal.PrimaryBrush, pal.PrimaryPen, _dynamicShardsGeometry);

        bool hasStuckBars = false;
        using (var barCtx = _dynamicStuckBarsGeometry.Open())
        {
            for (int i = 0; i < activeCount; i++)
            {
                if (!shards[i].Active || !shards[i].IsStuck) continue;
                hasStuckBars = true;

                double sx = shards[i].StuckX;
                double sy = shards[i].StuckY;
                double normAngle = shards[i].StuckAngle;

                for (int k = 0; k < 8; k++)
                {
                    float barVal = k switch
                    {
                        0 => shards[i].MiniBar0,
                        1 => shards[i].MiniBar1,
                        2 => shards[i].MiniBar2,
                        3 => shards[i].MiniBar3,
                        4 => shards[i].MiniBar4,
                        5 => shards[i].MiniBar5,
                        6 => shards[i].MiniBar6,
                        _ => shards[i].MiniBar7
                    };

                    double fanOffset = -0.55 + k * (1.10 / 7.0);
                    double fanAngle = normAngle + fanOffset;
                    double cosF = Math.Cos(fanAngle);
                    double sinF = Math.Sin(fanAngle);

                    double barLen = 5.0 + barVal * 32.0 * Math.Clamp(gainSetting / 3.0, 0.8, 3.5);
                    Point p0 = new Point(sx + 4.0 * cosF, sy + 4.0 * sinF);
                    Point p1 = new Point(sx + (4.0 + barLen) * cosF, sy + (4.0 + barLen) * sinF);

                    barCtx.BeginFigure(p0, false, false);
                    barCtx.LineTo(p1, true, false);
                }
            }
        }
        if (hasStuckBars)
        {
            dc.DrawGeometry(null, pal.RayPen, _dynamicStuckBarsGeometry);
        }
    }

    private void RenderGlitchBlocks(DrawingContext dc, ParticleEngine particleEngine)
    {
        var blocks = particleEngine.GlitchBlocks;
        for (int i = 0; i < ParticleEngine.MaxGlitchBlocks; i++)
        {
            if (!blocks[i].Active || blocks[i].CachedFormattedText == null) continue;

            double age = blocks[i].Age;
            double totalLife = blocks[i].TotalLife;
            double opacity = (age < 0.12) ? Math.Clamp(age / 0.12, 0.0, 1.0) : ((age > totalLife - 0.60) ? Math.Clamp((totalLife - age) / 0.60, 0.0, 1.0) : 1.0);
            if (opacity <= 0.01) continue;

            var text = blocks[i].CachedFormattedText;
            var glowText = blocks[i].CachedGlowFormattedText;
            if (text == null) continue;

            double bx = blocks[i].X;
            double by = blocks[i].Y;

            dc.PushOpacity(opacity);

            // Raw VRAM corruption: horizontal slice displacement (4 - 10 px) and RGB split
            double sliceShift = blocks[i].SliceDisplacement > 0.0 ? blocks[i].SliceDisplacement : 6.0;
            bool isGlitchFlicker = Math.Sin(blocks[i].Age * 45.0 + i) > 0.20;
            double xShift = isGlitchFlicker ? (sliceShift * ((i % 2 == 0) ? 1.0 : -1.0)) : 0.0;

            if (glowText != null)
            {
                // Chromatic aberration / Horizontal slice RGB desync
                dc.DrawText(glowText, new Point(bx + xShift - 3.2, by));
                dc.DrawText(glowText, new Point(bx + xShift + 3.2, by));
            }

            dc.DrawText(text, new Point(bx + xShift, by));
            dc.Pop();
        }
    }
}
