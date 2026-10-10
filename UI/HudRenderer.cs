using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// High-performance vector renderer for the holographic Arc Reactor HUD.
/// Renders radial spectrograph rays, orbital concentric rings, volumetric plasma core,
/// biomorphic AI face, overload gauge arc, and shard particles via DrawingContext.
/// All brushes, pens, geometries, and transforms are frozen or pre-allocated to guarantee 0 allocations at 60 FPS.
/// </summary>
public sealed partial class HudRenderer
{
    public const int RayCount = 128;
    public const double BaseRadius = 78.0;

    private readonly double[] _cosTable = new double[RayCount];
    private readonly double[] _sinTable = new double[RayCount];

    // Pre-computed, frozen geometries for orbital rings
    private StreamGeometry _ring1TicksGeometry = null!;
    private StreamGeometry _ring2AccentsGeometry = null!;
    private StreamGeometry _ring3SectorsGeometry = null!;
    private StreamGeometry _ring3ChevronsGeometry = null!;
    private StreamGeometry _ring4TicksGeometry = null!;
    private StreamGeometry _ring5CoilsGeometry = null!;
    private StreamGeometry _coreGlyphGeometry = null!;

    // Reusable dynamic StreamGeometry instances
    private readonly StreamGeometry _dynamicRaysGeometry = new();
    private readonly StreamGeometry _dynamicTipsGeometry = new();
    private readonly StreamGeometry _biomorphicFaceThickGeometry = new();
    private readonly StreamGeometry _biomorphicFaceGeometry = new();
    private readonly StreamGeometry _dynamicCrackGeometry = new();
    private readonly StreamGeometry _dynamicSparkGeometry = new();
    private readonly StreamGeometry _dynamicTitleSparkGeometry = new();
    private readonly StreamGeometry _dynamicChargeGaugeGeometry = new();
    private readonly StreamGeometry _dynamicShardsGeometry = new();
    private readonly StreamGeometry _dynamicStuckBarsGeometry = new();

    // Reusable clip rectangles and split transforms
    private readonly RectangleGeometry _leftClipRect = new();
    private readonly RectangleGeometry _rightClipRect = new();
    private readonly TranslateTransform _transSplitLeft = new();
    private readonly TranslateTransform _transSplitRight = new();

    // Reusable transform objects
    private readonly ScaleTransform _rootScaleTransform = new();
    private readonly ScaleTransform _coreScaleTransform = new();
    private readonly RotateTransform _rotRing1 = new();
    private readonly RotateTransform _rotRing2 = new();
    private readonly RotateTransform _rotRing3 = new();
    private readonly RotateTransform _rotRing4 = new();
    private readonly RotateTransform _rotRing5 = new();
    private readonly TranslateTransform _transCenter = new();
    private readonly ScaleTransform _titleScaleTransform = new();
    private readonly TranslateTransform _titleTranslateTransform = new();
    private readonly TransformGroup _titleTransformGroup = new();

    public HudRenderer()
    {
        PrecomputeTrigonometry();
        BuildStaticGeometries();

        _titleTransformGroup.Children.Add(_titleScaleTransform);
        _titleTransformGroup.Children.Add(_titleTranslateTransform);
    }

    public void RenderFrame(
        DrawingContext dc,
        Point center,
        double actualWidth,
        double actualHeight,
        double now,
        double dt,
        double breathPhase,
        double angleRing1,
        double angleRing2,
        double angleRing3,
        double angleRing4,
        double angleRing5,
        double smoothedSubBass,
        double bassScaleSetting,
        double gainSetting,
        float[] currentSpectrum,
        float beatIntensity,
        ReactorPalette pal,
        OverdriveStateMachine overdriveState,
        ParticleEngine particleEngine)
    {
        double cx = center.X;
        double cy = center.Y;

        double baseScale = (actualWidth > 0 && actualWidth <= 300)
            ? Math.Min(actualWidth, actualHeight) / 220.0
            : 1.0;
        if (baseScale <= 0.01) baseScale = 1.0;

        double bassScaleMultiplier = bassScaleSetting / 1.4;
        double subBassScale = 1.0 + (0.15 * bassScaleMultiplier) * smoothedSubBass;
        double finalScale = baseScale * subBassScale;

        _rootScaleTransform.ScaleX = finalScale;
        _rootScaleTransform.ScaleY = finalScale;
        _rootScaleTransform.CenterX = cx;
        _rootScaleTransform.CenterY = cy;

        _rotRing1.Angle = angleRing1; _rotRing1.CenterX = cx; _rotRing1.CenterY = cy;
        _rotRing2.Angle = angleRing2; _rotRing2.CenterX = cx; _rotRing2.CenterY = cy;
        _rotRing3.Angle = angleRing3; _rotRing3.CenterX = cx; _rotRing3.CenterY = cy;
        _rotRing4.Angle = angleRing4; _rotRing4.CenterX = cx; _rotRing4.CenterY = cy;
        _rotRing5.Angle = angleRing5; _rotRing5.CenterX = cx; _rotRing5.CenterY = cy;

        _transCenter.X = cx;
        _transCenter.Y = cy;

        double shatterImpulse = overdriveState.OverdriveProgress * 14.0;
        double splitDistance = overdriveState.CalculateSplitDistance();

        dc.PushTransform(_rootScaleTransform);

        // Overload gauge arc
        dc.DrawEllipse(null, StaticOverloadGaugeTrackPen, center, 80.0, 80.0);
        if (overdriveState.SmoothedCharge > 0.02)
        {
            double sweepAngle = Math.Min(359.9, overdriveState.SmoothedCharge * 360.0);
            double sweepRad = sweepAngle * Math.PI / 180.0;
            double startAngle = -Math.PI / 2.0;
            double endAngle = startAngle + sweepRad;

            Point pStart = new Point(cx + 80.0 * Math.Cos(startAngle), cy + 80.0 * Math.Sin(startAngle));
            Point pEnd = new Point(cx + 80.0 * Math.Cos(endAngle), cy + 80.0 * Math.Sin(endAngle));
            bool isLargeArc = sweepAngle > 180.0;

            using (var gCtx = _dynamicChargeGaugeGeometry.Open())
            {
                gCtx.BeginFigure(pStart, false, false);
                gCtx.ArcTo(pEnd, new Size(80.0, 80.0), 0, isLargeArc, SweepDirection.Clockwise, true, false);
            }
            Pen gaugePen = overdriveState.SmoothedCharge > 0.80 ? StaticOverloadGaugeHotPen : pal.PrimaryPen;
            dc.DrawGeometry(null, gaugePen, _dynamicChargeGaugeGeometry);
            dc.DrawEllipse(StaticTipBrush, null, pEnd, 2.2, 2.2);
        }

        // Override title
        RenderOverrideTitle(dc, center, now, overdriveState);

        // Plasma core & split fracture (with Giant Anticipation & Supersonic Slam scaling)
        Point shakenCenter = new Point(cx + overdriveState.ShakeOffsetX, cy + overdriveState.ShakeOffsetY);
        double coreScale = overdriveState.CalculateCoreScale();
        bool hasCoreScale = Math.Abs(coreScale - 1.0) > 0.005;
        if (hasCoreScale)
        {
            _coreScaleTransform.ScaleX = coreScale;
            _coreScaleTransform.ScaleY = coreScale;
            _coreScaleTransform.CenterX = shakenCenter.X;
            _coreScaleTransform.CenterY = shakenCenter.Y;
            dc.PushTransform(_coreScaleTransform);
        }

        RenderCore(dc, center, shakenCenter, now, breathPhase, smoothedSubBass, bassScaleMultiplier, shatterImpulse, splitDistance, pal, overdriveState);

        if (hasCoreScale)
        {
            dc.Pop();
        }

        // Orbital rings
        RenderOrbitalRings(dc, center, shatterImpulse, pal);

        // Radial spectrograph
        RenderSpectrograph(dc, center, now, gainSetting, currentSpectrum, pal);

        // Containment & shockwave
        double dynamicMaxRayHeight = 28.0 + Math.Max(0.0, gainSetting - 1.0) * 8.0;
        dc.DrawEllipse(null, pal.PenContainment, center, BaseRadius + dynamicMaxRayHeight * 0.85, BaseRadius + dynamicMaxRayHeight * 0.85);

        if (overdriveState.ShockwaveOpacity > 0.01)
        {
            int penIdx = Math.Clamp((int)(overdriveState.ShockwaveOpacity * 31.0), 0, 31);
            dc.DrawEllipse(null, StaticShockwavePens[penIdx], center, overdriveState.ShockwaveRadius, overdriveState.ShockwaveRadius);
        }

        dc.Pop(); // _rootScaleTransform

        // Screen shatter / glass cracks
        RenderScreenCracks(dc, center, actualWidth, actualHeight, dt, beatIntensity, overdriveState);

        // Shards & stuck mini-equalizers
        if (overdriveState.OverdriveProgress > 0.02)
        {
            RenderShards(dc, particleEngine, beatIntensity, gainSetting, pal);
        }

        // Glitch code blocks
        if (particleEngine.EnableMemoryGlitch)
        {
            RenderGlitchBlocks(dc, particleEngine);
        }
    }

    private void RenderCore(DrawingContext dc, Point center, Point shakenCenter, double now, double breathPhase, double smoothedSubBass, double bassScaleMultiplier, double shatterImpulse, double splitDistance, ReactorPalette pal, OverdriveStateMachine overdriveState)
    {
        double idleBreath = Math.Sin(breathPhase);
        double auraRadius = (48.0 + shatterImpulse * 0.8) * (1.0 + 0.05 * idleBreath) * (1.0 + (0.24 * bassScaleMultiplier) * smoothedSubBass);
        double plasmaRadius = (30.0 + shatterImpulse * 0.5) * (1.0 + 0.07 * idleBreath) * (1.0 + (0.18 * bassScaleMultiplier) * smoothedSubBass);
        double nuclearRadius = (13.0 + shatterImpulse * 0.2) * (1.0 + 0.04 * idleBreath) * (1.0 + (0.12 * bassScaleMultiplier) * smoothedSubBass);

        if (splitDistance > 0.5)
        {
            dc.DrawEllipse(StaticMoltenRiftBrush, null, shakenCenter, splitDistance * 1.3 + 6.0, plasmaRadius * 0.95);

            // Left core half
            _leftClipRect.Rect = new Rect(shakenCenter.X - 500.0, shakenCenter.Y - 500.0, 500.0, 1000.0);
            _transSplitLeft.X = -splitDistance;
            _transSplitLeft.Y = 0;

            dc.PushTransform(_transSplitLeft);
            dc.PushClip(_leftClipRect);
            dc.DrawEllipse(pal.AuraBrush, null, shakenCenter, auraRadius, auraRadius);
            dc.DrawEllipse(pal.PlasmaBrush, null, shakenCenter, plasmaRadius, plasmaRadius);
            dc.DrawEllipse(pal.NuclearBrush, null, shakenCenter, nuclearRadius, nuclearRadius);
            RenderBiomorphicFace(dc, shakenCenter, now, (float)smoothedSubBass, pal);
            dc.Pop();
            dc.Pop();

            // Right core half
            _rightClipRect.Rect = new Rect(shakenCenter.X, shakenCenter.Y - 500.0, 500.0, 1000.0);
            _transSplitRight.X = splitDistance;
            _transSplitRight.Y = 0;

            dc.PushTransform(_transSplitRight);
            dc.PushClip(_rightClipRect);
            dc.DrawEllipse(pal.AuraBrush, null, shakenCenter, auraRadius, auraRadius);
            dc.DrawEllipse(pal.PlasmaBrush, null, shakenCenter, plasmaRadius, plasmaRadius);
            dc.DrawEllipse(pal.NuclearBrush, null, shakenCenter, nuclearRadius, nuclearRadius);
            RenderBiomorphicFace(dc, shakenCenter, now, (float)smoothedSubBass, pal);
            dc.Pop();
            dc.Pop();

            // Zigzag crack
            using (var crackCtx = _dynamicCrackGeometry.Open())
            {
                crackCtx.BeginFigure(new Point(shakenCenter.X - splitDistance * 0.2, shakenCenter.Y - 32.0), false, false);
                crackCtx.LineTo(new Point(shakenCenter.X - splitDistance * 0.8 - 2.0, shakenCenter.Y - 18.0), true, false);
                crackCtx.LineTo(new Point(shakenCenter.X + splitDistance * 0.6 + 2.5, shakenCenter.Y - 6.0), true, false);
                crackCtx.LineTo(new Point(shakenCenter.X - splitDistance * 0.5 - 2.0, shakenCenter.Y + 8.0), true, false);
                crackCtx.LineTo(new Point(shakenCenter.X + splitDistance * 0.7 + 2.5, shakenCenter.Y + 20.0), true, false);
                crackCtx.LineTo(new Point(shakenCenter.X + splitDistance * 0.1, shakenCenter.Y + 32.0), true, false);
            }
            dc.DrawGeometry(null, StaticCrackPen, _dynamicCrackGeometry);
        }
        else
        {
            dc.DrawEllipse(pal.AuraBrush, null, shakenCenter, auraRadius, auraRadius);
            dc.DrawEllipse(pal.PlasmaBrush, null, shakenCenter, plasmaRadius, plasmaRadius);
            dc.DrawEllipse(pal.NuclearBrush, null, shakenCenter, nuclearRadius, nuclearRadius);
            RenderBiomorphicFace(dc, shakenCenter, now, (float)smoothedSubBass, pal);
        }

        if (overdriveState.WhiteoutIntensity > 0.01)
        {
            dc.PushOpacity(overdriveState.WhiteoutIntensity);
            double whiteoutRadius = (plasmaRadius * 1.5) * (0.8 + 0.2 * overdriveState.WhiteoutIntensity);
            dc.DrawEllipse(StaticCoreWhiteoutBrush, null, shakenCenter, whiteoutRadius, whiteoutRadius);
            dc.Pop();
        }

        if (overdriveState.SparkFlash > 0.02)
        {
            double cx = center.X;
            double cy = center.Y;
            using (var sparkCtx = _dynamicSparkGeometry.Open())
            {
                sparkCtx.BeginFigure(new Point(cx, cy - 36.0), false, false);
                sparkCtx.LineTo(new Point(cx - 3.5, cy - 22.0), true, false);
                sparkCtx.LineTo(new Point(cx + 4.0, cy - 10.0), true, false);
                sparkCtx.LineTo(new Point(cx - 3.0, cy + 6.0), true, false);
                sparkCtx.LineTo(new Point(cx + 3.5, cy + 20.0), true, false);
                sparkCtx.LineTo(new Point(cx, cy + 36.0), true, false);
            }
            dc.DrawGeometry(null, StaticSparkPen, _dynamicSparkGeometry);
            dc.DrawEllipse(StaticSparkHaloBrush, null, center, 26.0 * overdriveState.SparkFlash, 26.0 * overdriveState.SparkFlash);
        }
    }

    private void RenderOrbitalRings(DrawingContext dc, Point center, double shatterImpulse, ReactorPalette pal)
    {
        double r5 = 34.0 + shatterImpulse * 0.3;
        dc.PushTransform(_rotRing5);
        dc.DrawEllipse(null, pal.PenRing5Coils, center, r5, r5);
        dc.PushTransform(_transCenter);
        dc.DrawGeometry(pal.PrimaryBrush, null, _ring5CoilsGeometry);
        dc.Pop();
        dc.Pop();

        double r4 = 44.0 + shatterImpulse * 0.5;
        dc.PushTransform(_rotRing4);
        dc.DrawEllipse(null, pal.PenRing4Dashed, center, r4, r4);
        dc.PushTransform(_transCenter);
        dc.DrawGeometry(null, pal.PenRing4Ticks, _ring4TicksGeometry);
        dc.Pop();
        dc.Pop();

        double r3 = 54.0 + shatterImpulse * 0.7;
        dc.PushTransform(_rotRing3);
        dc.PushTransform(_transCenter);
        dc.DrawGeometry(null, pal.PenRing3Sectors, _ring3SectorsGeometry);
        dc.DrawGeometry(pal.PrimaryBrush, null, _ring3ChevronsGeometry);
        dc.Pop();
        dc.Pop();

        double r2 = 64.0 + shatterImpulse * 0.9;
        dc.PushTransform(_rotRing2);
        dc.DrawEllipse(null, pal.PenRing2Dashed, center, r2, r2);
        dc.PushTransform(_transCenter);
        dc.DrawGeometry(null, pal.PrimaryPen, _ring2AccentsGeometry);
        dc.Pop();
        dc.Pop();

        double r1 = 72.0 + shatterImpulse * 1.1;
        dc.PushTransform(_rotRing1);
        dc.DrawEllipse(null, pal.PenRing1Track, center, r1, r1);
        dc.PushTransform(_transCenter);
        dc.DrawGeometry(null, pal.PenRing1Ticks, _ring1TicksGeometry);
        dc.Pop();
        dc.Pop();
    }

    private void RenderSpectrograph(DrawingContext dc, Point center, double now, double gainSetting, float[] currentSpectrum, ReactorPalette pal)
    {
        double cx = center.X;
        double cy = center.Y;
        double dynamicMaxRayHeight = 28.0 + Math.Max(0.0, gainSetting - 1.0) * 8.0;

        using (var rayCtx = _dynamicRaysGeometry.Open())
        {
            for (int i = 0; i < RayCount; i++)
            {
                double cos = _cosTable[i];
                double sin = _sinTable[i];
                double theta = i * (2.0 * Math.PI / RayCount);

                double idleWave = 1.2 * Math.Sin(4.0 * theta + 2.5 * now) * Math.Cos(2.0 * theta - 1.8 * now);
                double s = currentSpectrum[i];
                double shaped = Math.Pow(s, 0.75) * (0.45 + 0.55 * Math.Pow(s, 1.6));
                double audioHeight = shaped * dynamicMaxRayHeight;
                double waveBlend = Math.Max(0.0, 1.0 - s * 3.0);
                double h = Math.Clamp(2.0 + idleWave * waveBlend + audioHeight, 1.5, dynamicMaxRayHeight + 6.0);

                Point p0 = new Point(cx + BaseRadius * cos, cy + BaseRadius * sin);
                Point p1 = new Point(cx + (BaseRadius + h) * cos, cy + (BaseRadius + h) * sin);

                rayCtx.BeginFigure(p0, false, false);
                rayCtx.LineTo(p1, true, false);
            }
        }
        dc.DrawGeometry(null, pal.RayPen, _dynamicRaysGeometry);

        using (var tipCtx = _dynamicTipsGeometry.Open())
        {
            for (int i = 0; i < RayCount; i++)
            {
                double cos = _cosTable[i];
                double sin = _sinTable[i];
                double theta = i * (2.0 * Math.PI / RayCount);

                double idleWave = 1.2 * Math.Sin(4.0 * theta + 2.5 * now) * Math.Cos(2.0 * theta - 1.8 * now);
                double s = currentSpectrum[i];
                double shaped = Math.Pow(s, 0.75) * (0.45 + 0.55 * Math.Pow(s, 1.6));
                double audioHeight = shaped * dynamicMaxRayHeight;
                double waveBlend = Math.Max(0.0, 1.0 - s * 3.0);
                double h = Math.Clamp(2.0 + idleWave * waveBlend + audioHeight, 1.5, dynamicMaxRayHeight + 6.0);

                Point p1 = new Point(cx + (BaseRadius + h) * cos, cy + (BaseRadius + h) * sin);

                tipCtx.BeginFigure(new Point(p1.X - 1.0, p1.Y), true, true);
                tipCtx.ArcTo(new Point(p1.X + 1.0, p1.Y), new Size(1.0, 1.0), 0, false, SweepDirection.Clockwise, true, false);
                tipCtx.ArcTo(new Point(p1.X - 1.0, p1.Y), new Size(1.0, 1.0), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        dc.DrawGeometry(StaticTipBrush, null, _dynamicTipsGeometry);
    }

    private void RenderBiomorphicFace(DrawingContext dc, Point center, double now, float subBass, ReactorPalette pal)
    {
        double cx = center.X;
        double cy = center.Y;

        double blinkPeriod = 3.6;
        double blinkPhase = now % blinkPeriod;
        double blinkScale = blinkPhase < 0.16 ? Math.Max(0.08, 1.0 - Math.Sin((blinkPhase / 0.16) * Math.PI)) : 1.0;

        double deform = 1.0 + 0.12 * subBass;

        Point eL1 = new Point(cx - 12.0 * deform, cy - 2.5 * deform);
        Point eL2 = new Point(cx - 7.0 * deform, cy - 5.5 * deform * blinkScale);
        Point eL3 = new Point(cx - 2.5 * deform, cy - 2.5 * deform);
        Point eL4 = new Point(cx - 7.0 * deform, cy - 0.2 * deform * blinkScale);

        Point eR1 = new Point(cx + 2.5 * deform, cy - 2.5 * deform);
        Point eR2 = new Point(cx + 7.0 * deform, cy - 5.5 * deform * blinkScale);
        Point eR3 = new Point(cx + 12.0 * deform, cy - 2.5 * deform);
        Point eR4 = new Point(cx + 7.0 * deform, cy - 0.2 * deform * blinkScale);

        Point b0 = new Point(cx, cy - 13.0 * deform);
        Point bL = new Point(cx - 8.0 * deform, cy - 9.0 * deform);
        Point bR = new Point(cx + 8.0 * deform, cy - 9.0 * deform);

        Point n0 = new Point(cx, cy + 1.5 * deform);
        Point chL = new Point(cx - 14.0 * deform, cy + 5.0 * deform);
        Point chR = new Point(cx + 14.0 * deform, cy + 5.0 * deform);

        Point jL = new Point(cx - 8.0 * deform, cy + 12.0 * deform);
        Point jR = new Point(cx + 8.0 * deform, cy + 12.0 * deform);
        Point chin = new Point(cx, cy + 15.5 * deform);

        using (var ctx = _biomorphicFaceThickGeometry.Open())
        {
            ctx.BeginFigure(eL1, false, false); ctx.LineTo(eL2, true, false); ctx.LineTo(eL3, true, false);
            ctx.BeginFigure(eR1, false, false); ctx.LineTo(eR2, true, false); ctx.LineTo(eR3, true, false);
        }
        dc.DrawGeometry(null, StaticBiomorphicThickPen, _biomorphicFaceThickGeometry);

        using (var ctx = _biomorphicFaceGeometry.Open())
        {
            ctx.BeginFigure(eL3, false, false); ctx.LineTo(eL4, true, false); ctx.LineTo(eL1, true, false);
            ctx.BeginFigure(eR3, false, false); ctx.LineTo(eR4, true, false); ctx.LineTo(eR1, true, false);
            ctx.BeginFigure(bL, false, false); ctx.LineTo(b0, true, false); ctx.LineTo(bR, true, false);
            ctx.BeginFigure(b0, false, false); ctx.LineTo(n0, true, false);
            ctx.BeginFigure(eL1, false, false); ctx.LineTo(chL, true, false); ctx.LineTo(jL, true, false); ctx.LineTo(chin, true, false);
            ctx.BeginFigure(eR3, false, false); ctx.LineTo(chR, true, false); ctx.LineTo(jR, true, false); ctx.LineTo(chin, true, false);
            ctx.BeginFigure(n0, false, false); ctx.LineTo(chin, true, false);
        }
        dc.DrawGeometry(null, pal.BiomorphicPen, _biomorphicFaceGeometry);

        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, new Point(cx - 7.0 * deform, cy - 2.5 * deform), 1.1, 1.1 * blinkScale);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, new Point(cx + 7.0 * deform, cy - 2.5 * deform), 1.1, 1.1 * blinkScale);

        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, b0, 1.2, 1.2);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, n0, 1.2, 1.2);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chin, 1.4, 1.4);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chL, 1.1, 1.1);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chR, 1.1, 1.1);
    }
}
