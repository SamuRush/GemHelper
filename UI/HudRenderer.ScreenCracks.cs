using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Photorealistic 3D glass fracture stack and screen shatter visualizer for Overdrive:
/// - Layer 1: Optical refraction & displacement mapping (DesktopRefractionCapture + chromatic aberration)
/// - Layer 2: Volumetric crack facets with dual-contour specular highlights (#FFFFFF, 1.5 px)
///   and deep shadows (#05050A, 2.5 px) simulating 6-8 mm thick glass, plus polygonal plates.
/// - Layer 3: High-voltage plasma arcs (NeonLightningGenerator: Scarlet #FF003C & Cyan #00E5FF).
/// Frozen StreamGeometry and zero heap allocations maintain 60 FPS performance.
/// </summary>
public sealed partial class HudRenderer
{
    public bool EnableScreenCracks { get; set; } = true;
    public bool HasActiveCracks => _hasActiveCracks;

    private readonly ProceduralCrackGeometry _crackGeometry = new();
    private readonly DesktopRefractionCapture _refractionCapture = new();
    private readonly NeonLightningGenerator _lightningGenerator = new();

    private Point _cracksEpicenter;
    private double _cracksTimer = 0.0;
    private bool _hasActiveCracks = false;
    private double _cracksMaxRadius = 1000.0;

    private readonly EllipseGeometry _cracksSealClip = new();
    private readonly TranslateTransform _cracksShakeTransform = new();

    private static readonly Pen StaticVolumetricHighlightPen = InitFrozenPen(Color.FromArgb(245, 255, 255, 255), 1.5);
    private static readonly Pen StaticVolumetricShadowPen = InitFrozenPen(Color.FromArgb(235, 5, 5, 10), 2.5);
    private static readonly SolidColorBrush StaticShardPlateBrush = InitFrozenBrush(Color.FromArgb(22, 0, 229, 255));

    private static readonly Pen StaticCracksFlashNeonPen = InitFrozenPen(Color.FromRgb(0xFF, 0xFF, 0xFF), 2.5);
    private static readonly Pen StaticCracksFlashScarletPen = InitFrozenPen(Color.FromArgb(220, 255, 0, 60), 5.5);
    private static readonly Pen StaticCracksFlashCyanPen = InitFrozenPen(Color.FromArgb(220, 0, 229, 255), 5.5);

    private static Pen InitFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush InitFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void GenerateScreenCracks(Point epicenter, double boundsW, double boundsH)
    {
        if (!EnableScreenCracks) return;

        double w = boundsW > 0 ? boundsW : 1920.0;
        double h = boundsH > 0 ? boundsH : 1080.0;
        double maxDx = Math.Max(epicenter.X, w - epicenter.X);
        double maxDy = Math.Max(epicenter.Y, h - epicenter.Y);
        _cracksMaxRadius = Math.Sqrt(maxDx * maxDx + maxDy * maxDy) + 120.0;
        _cracksEpicenter = epicenter;
        _cracksTimer = 0.0;

        // 1. Layer 2: Volumetric crack geometry & polygonal shards
        _crackGeometry.Generate(epicenter, w, h);

        // 2. Layer 1: Capture desktop under fracture area for optical refraction
        _refractionCapture.CaptureOrFallback(epicenter, _cracksMaxRadius, w, h);

        // 3. Layer 3: High-voltage plasma arcs
        _lightningGenerator.Trigger(epicenter);

        _hasActiveCracks = true;
    }

    public void ClearScreenCracks()
    {
        _hasActiveCracks = false;
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
        if (!EnableScreenCracks || !_hasActiveCracks || !_crackGeometry.HasGeometry) return;

        _cracksTimer += dt;
        _lightningGenerator.Update(dt);

        // Instant crack clearance: all fractures vanish at Phase 2 slam impact (t >= 0.20s)
        // or whenever overdrive and reassembly are complete/inactive
        if ((overdriveState.ReassemblyPhase == ReassemblyPhase.Phase2_Slam && overdriveState.ReassemblyPhaseTimer >= 0.20) ||
            (!overdriveState.IsOverdriveActive && !overdriveState.IsReassembling))
        {
            ClearScreenCracks();
            return;
        }

        // Pulse and re-energize neon lightning arcs on audio kick beats during Overdrive
        if (overdriveState.IsOverdriveActive)
        {
            _lightningGenerator.PulseOnBeat(beatIntensity, center);
        }

        // 1. Initial 150-250 ms: High-speed neon color surge pulse along crack branches
        if (_cracksTimer <= 0.22)
        {
            double surgeFade = Math.Clamp(1.0 - (_cracksTimer / 0.22), 0.0, 1.0);
            dc.PushOpacity(surgeFade);

            if (_crackGeometry.ShadowGeometry != null)
            {
                dc.DrawGeometry(null, StaticCracksFlashScarletPen, _crackGeometry.ShadowGeometry);
                dc.DrawGeometry(null, StaticCracksFlashCyanPen, _crackGeometry.ShadowGeometry);
            }

            if (_crackGeometry.HighlightGeometry != null)
            {
                dc.DrawGeometry(null, StaticCracksFlashNeonPen, _crackGeometry.HighlightGeometry);
            }

            dc.Pop();

            // Layer 3 lightning active during detonation surge
            _lightningGenerator.Render(dc, dt, center);
            return;
        }

        // 2. Overdrive active: Render full 3-layer glass fracture stack
        if (overdriveState.IsOverdriveActive)
        {
            double opacity = Math.Clamp(0.52 + 0.30 * beatIntensity, 0.45, 0.88);

            _cracksShakeTransform.X = overdriveState.ShakeOffsetX;
            _cracksShakeTransform.Y = overdriveState.ShakeOffsetY;

            dc.PushTransform(_cracksShakeTransform);

            // Layer 1: Optical Refraction & Desktop Chromatic Aberration
            _refractionCapture.RenderRefractionLayer(
                dc,
                _cracksEpicenter,
                dt,
                _crackGeometry.CombinedClipGeometry,
                opacity);

            dc.PushOpacity(opacity);

            // Layer 2: Polygonal Shard Plates & Volumetric Facet Bevels (6-8 mm thickness)
            if (_crackGeometry.PolygonalShardsGeometry != null)
            {
                dc.DrawGeometry(StaticShardPlateBrush, null, _crackGeometry.PolygonalShardsGeometry);
            }

            if (_crackGeometry.ShadowGeometry != null)
            {
                dc.DrawGeometry(null, StaticVolumetricShadowPen, _crackGeometry.ShadowGeometry);
            }

            if (_crackGeometry.HighlightGeometry != null)
            {
                dc.DrawGeometry(null, StaticVolumetricHighlightPen, _crackGeometry.HighlightGeometry);
            }

            dc.Pop(); // Opacity

            // Layer 3: Neon Lightning Arcs
            _lightningGenerator.Render(dc, dt, center);

            dc.Pop(); // Transform
            return;
        }

        // 3. Reassembly phase (Glass Sealing): cracks seal inward towards epicenter
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

                if (_crackGeometry.ShadowGeometry != null)
                {
                    dc.DrawGeometry(null, StaticVolumetricShadowPen, _crackGeometry.ShadowGeometry);
                }

                if (_crackGeometry.HighlightGeometry != null)
                {
                    dc.DrawGeometry(null, StaticVolumetricHighlightPen, _crackGeometry.HighlightGeometry);
                }

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
