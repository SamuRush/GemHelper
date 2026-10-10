using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Procedural glass cracking and screen shatter visualizer for the Overdrive effect.
/// Generates jagged radial cracks, spiderweb branches and concentric cross-bars from the core epicenter.
/// Geometry is frozen immediately (Freeze()) for 0 GC overhead in 60 FPS rendering.
/// Supports blinding neon-scarlet flash, beat vibration, click-through transparency,
/// and reassembly inward glass sealing.
/// </summary>
public sealed partial class HudRenderer
{
    public bool EnableScreenCracks { get; set; } = true;

    private StreamGeometry? _frozenCracksGeometry;
    private Point _cracksEpicenter;
    private double _cracksTimer = 0.0;
    private bool _hasActiveCracks = false;
    private double _cracksMaxRadius = 1000.0;

    private readonly EllipseGeometry _cracksSealClip = new();
    private readonly TranslateTransform _cracksShakeTransform = new();
    private readonly Random _cracksRand = new();

    private static readonly Pen StaticCracksFlashNeonPen = InitFrozenPen(Color.FromRgb(0xFF, 0xFF, 0xFF), 2.2);
    private static readonly Pen StaticCracksFlashScarletPen = InitFrozenPen(Color.FromRgb(0xFF, 0x00, 0x3C), 4.8);
    private static readonly Pen StaticCracksNeonPen = InitFrozenPen(Color.FromArgb(220, 0xFF, 0xFF, 0xFF), 1.2);
    private static readonly Pen StaticCracksScarletPen = InitFrozenPen(Color.FromArgb(180, 0xFF, 0x00, 0x3C), 2.6);

    private static Pen InitFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void GenerateScreenCracks(Point epicenter, double boundsW, double boundsH)
    {
        if (!EnableScreenCracks) return;

        double w = boundsW > 0 ? boundsW : 1920.0;
        double h = boundsH > 0 ? boundsH : 1080.0;
        double maxDx = Math.Max(epicenter.X, w - epicenter.X);
        double maxDy = Math.Max(epicenter.Y, h - epicenter.Y);
        _cracksMaxRadius = Math.Sqrt(maxDx * maxDx + maxDy * maxDy) + 100.0;
        _cracksEpicenter = epicenter;
        _cracksTimer = 0.0;

        int rayCount = 5 + _cracksRand.Next(3); // 5 to 7 main radial rays
        var rayNodeLists = new List<List<Point>>(rayCount);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // 1. Generate 5-7 main radial cracks with zigzag path
            for (int r = 0; r < rayCount; r++)
            {
                var nodes = new List<Point>(40);
                double baseAngle = r * (2.0 * Math.PI / rayCount) + (_cracksRand.NextDouble() - 0.5) * 0.35;
                Point curPt = new Point(
                    epicenter.X + 16.0 * Math.Cos(baseAngle),
                    epicenter.Y + 16.0 * Math.Sin(baseAngle));

                ctx.BeginFigure(curPt, false, false);
                nodes.Add(curPt);

                double travelDist = 16.0;
                while (travelDist < _cracksMaxRadius)
                {
                    double segLen = 20.0 + _cracksRand.NextDouble() * 20.0; // 20 - 40 px segment length
                    double angleDev = (_cracksRand.NextDouble() - 0.5) * (35.0 * Math.PI / 180.0); // +-17.5 deg
                    double segAngle = baseAngle + angleDev;

                    curPt = new Point(
                        curPt.X + segLen * Math.Cos(segAngle),
                        curPt.Y + segLen * Math.Sin(segAngle));

                    ctx.LineTo(curPt, true, false);
                    nodes.Add(curPt);
                    travelDist += segLen;
                }

                rayNodeLists.Add(nodes);

                // 2. Generate 2-3 secondary branches for each main ray
                int branchCount = 2 + _cracksRand.Next(2);
                for (int b = 0; b < branchCount; b++)
                {
                    if (nodes.Count < 6) continue;
                    int startIdx = 2 + _cracksRand.Next(nodes.Count - 4);
                    Point branchPt = nodes[startIdx];

                    double branchAngle = baseAngle + ((_cracksRand.Next(2) == 0 ? 1.0 : -1.0) * (0.65 + _cracksRand.NextDouble() * 0.30));
                    ctx.BeginFigure(branchPt, false, false);

                    int branchSteps = 2 + _cracksRand.Next(3);
                    for (int s = 0; s < branchSteps; s++)
                    {
                        double bLen = 18.0 + _cracksRand.NextDouble() * 16.0;
                        double bDev = (_cracksRand.NextDouble() - 0.5) * 0.35;
                        branchPt = new Point(
                            branchPt.X + bLen * Math.Cos(branchAngle + bDev),
                            branchPt.Y + bLen * Math.Sin(branchAngle + bDev));
                        ctx.LineTo(branchPt, true, false);
                    }
                }
            }

            // 3. Concentric transverse cross-bars (spiderweb broken glass)
            for (int r = 0; r < rayCount; r++)
            {
                var nodesA = rayNodeLists[r];
                var nodesB = rayNodeLists[(r + 1) % rayCount];

                int barCount = Math.Min(nodesA.Count, nodesB.Count) / 5;
                for (int c = 1; c <= barCount; c++)
                {
                    int idxA = Math.Clamp(c * 5, 0, nodesA.Count - 1);
                    int idxB = Math.Clamp(c * 5 + (_cracksRand.Next(3) - 1), 0, nodesB.Count - 1);

                    Point pA = nodesA[idxA];
                    Point pB = nodesB[idxB];

                    Point mid = new Point(
                        (pA.X + pB.X) * 0.5 + (_cracksRand.NextDouble() - 0.5) * 14.0,
                        (pA.Y + pB.Y) * 0.5 + (_cracksRand.NextDouble() - 0.5) * 14.0);

                    ctx.BeginFigure(pA, false, false);
                    ctx.LineTo(mid, true, false);
                    ctx.LineTo(pB, true, false);
                }
            }
        }

        geometry.Freeze();
        _frozenCracksGeometry = geometry;
        _hasActiveCracks = true;
    }

    public void ClearScreenCracks()
    {
        _hasActiveCracks = false;
        _frozenCracksGeometry = null;
    }

    public void RenderScreenCracks(
        DrawingContext dc,
        Point center,
        double boundsW,
        double boundsH,
        double dt,
        float beatIntensity,
        OverdriveStateMachine overdriveState)
    {
        if (!EnableScreenCracks || !_hasActiveCracks || _frozenCracksGeometry == null) return;

        _cracksTimer += dt;

        // Final shutter collapse check: cracks disappear with white flash at Phase 2 slam impact
        if (overdriveState.ReassemblyPhase == ReassemblyPhase.Phase2_Slam && overdriveState.ReassemblyPhaseTimer >= 0.20)
        {
            ClearScreenCracks();
            return;
        }

        // 1. Initial 150 ms: Blinding white neon (#FFFFFF) flash with scarlet contour (#FF003C)
        if (_cracksTimer <= 0.15)
        {
            dc.DrawGeometry(null, StaticCracksFlashScarletPen, _frozenCracksGeometry);
            dc.DrawGeometry(null, StaticCracksFlashNeonPen, _frozenCracksGeometry);
            return;
        }

        // 2. Overdrive active: Opacity 0.4 - 0.6, flashing under kick drums and vibrating with screen shake
        if (overdriveState.IsOverdriveActive)
        {
            double opacity = Math.Clamp(0.40 + 0.20 * beatIntensity, 0.40, 0.65);

            _cracksShakeTransform.X = overdriveState.ShakeOffsetX;
            _cracksShakeTransform.Y = overdriveState.ShakeOffsetY;

            dc.PushTransform(_cracksShakeTransform);
            dc.PushOpacity(opacity);
            dc.DrawGeometry(null, StaticCracksScarletPen, _frozenCracksGeometry);
            dc.DrawGeometry(null, StaticCracksNeonPen, _frozenCracksGeometry);
            dc.Pop();
            dc.Pop();
            return;
        }

        // 3. Reassembly phase (Glass Sealing): cracks heal inward from screen edges to center
        if (overdriveState.IsReassembling)
        {
            double sealProgress = 0.0;
            if (overdriveState.ReassemblyPhase == ReassemblyPhase.Phase1_Absorption)
            {
                sealProgress = Math.Clamp((1.8 - overdriveState.ReassemblyPhaseTimer) / 1.8 * 0.70, 0.0, 0.70);
            }
            else if (overdriveState.ReassemblyPhase == ReassemblyPhase.DramaticPause)
            {
                sealProgress = Math.Clamp(0.70 + (0.40 - overdriveState.ReassemblyPhaseTimer) / 0.40 * 0.30, 0.70, 1.0);
            }
            else if (overdriveState.ReassemblyPhase == ReassemblyPhase.Phase2_Slam)
            {
                sealProgress = 1.0;
            }

            double currentRadius = Math.Max(15.0, _cracksMaxRadius * (1.0 - sealProgress));
            _cracksSealClip.Center = _cracksEpicenter;
            _cracksSealClip.RadiusX = currentRadius;
            _cracksSealClip.RadiusY = currentRadius;

            double alpha = Math.Clamp((1.0 - sealProgress) * 0.50, 0.0, 0.50);
            if (alpha > 0.01)
            {
                dc.PushClip(_cracksSealClip);
                dc.PushOpacity(alpha);
                dc.DrawGeometry(null, StaticCracksScarletPen, _frozenCracksGeometry);
                dc.DrawGeometry(null, StaticCracksNeonPen, _frozenCracksGeometry);
                dc.Pop();
                dc.Pop();
            }
        }
        else
        {
            ClearScreenCracks();
        }
    }
}
