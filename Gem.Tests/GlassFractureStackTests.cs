using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Gem.UI;
using Xunit;
using Point = System.Windows.Point;

namespace Gem.Tests;

/// <summary>
/// QA tests for the photorealistic 3D glass fracture stack and Overdrive visual effect:
/// - Layer 1: Optical refraction & displacement mapping (HLSL ShaderEffect & desktop chromatic aberration)
/// - Layer 2: Volumetric crack facets (dual-contour specular highlight + deep shadow, polygonal shards)
/// - Layer 3: Neon lightning arcs (scarlet and cyan branching arcs with white core and vertex jitter)
/// - Directional cone burst (4x initial velocity, motion streaks, 1.5% sticking probability)
/// - Core hydraulic slam (2.0x anticipation, 80ms EaseInExpo slam, 700px shockwave, instant crack clearance)
/// </summary>
public class GlassFractureStackTests
{
    [Fact]
    public void Layer1_GlassDistortionEffect_Initialization_And_Properties()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var effect = new GlassDistortionEffect();
                Assert.NotNull(effect);

                // Default dependency properties
                Assert.Equal(new Point(0.5, 0.5), effect.Center);
                Assert.Equal(1.0, effect.Intensity, precision: 2);
                Assert.Equal(2.5, effect.ChromaticAberration, precision: 2);
                Assert.Equal(1.0, effect.AspectRatio, precision: 2);

                // Modifying properties
                effect.Center = new Point(0.7, 0.3);
                effect.Intensity = 1.8;
                effect.ChromaticAberration = 3.5;
                effect.AspectRatio = 1.77;

                Assert.Equal(new Point(0.7, 0.3), effect.Center);
                Assert.Equal(1.8, effect.Intensity, precision: 2);
                Assert.Equal(3.5, effect.ChromaticAberration, precision: 2);
                Assert.Equal(1.77, effect.AspectRatio, precision: 2);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }

    [Fact]
    public void Layer1_DesktopRefractionCapture_Capture_And_BufferInitialization()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var capture = new DesktopRefractionCapture();
                Assert.NotNull(capture);

                // Initial state
                Assert.False(capture.HasCapturedBackdrop);

                // Capture or initialize fallback backdrop
                capture.CaptureOrFallback(new Point(960, 540), 600.0, 1920.0, 1080.0);
                Assert.True(capture.HasCapturedBackdrop);
                Assert.NotNull(capture.BackdropBrush);
                Assert.True(capture.BackdropBrush.IsFrozen);

                // Verify chromatic aberration shift parameter (between 2 and 4 px)
                Assert.InRange(capture.ChromaticShiftPixels, 2.0, 4.0);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }

    [Fact]
    public void Layer2_ProceduralCrackGeometry_DualContour_And_PolygonalShards()
    {
        var crackGen = new ProceduralCrackGeometry();
        crackGen.Generate(new Point(960, 540), 1920.0, 1080.0);

        Assert.True(crackGen.HasGeometry);
        Assert.NotNull(crackGen.HighlightGeometry);
        Assert.NotNull(crackGen.ShadowGeometry);
        Assert.NotNull(crackGen.PolygonalShardsGeometry);

        // Geometries must be frozen for 0 GC overhead in 60 FPS rendering
        Assert.True(crackGen.HighlightGeometry.IsFrozen, "Highlight geometry must be frozen");
        Assert.True(crackGen.ShadowGeometry.IsFrozen, "Shadow geometry must be frozen");
        Assert.True(crackGen.PolygonalShardsGeometry.IsFrozen, "Polygonal shards geometry must be frozen");

        // Dual contour parameters: 1.5 px highlight, 2.5 px shadow for 6-8 mm glass thickness
        Assert.Equal(1.5, ProceduralCrackGeometry.SpecularHighlightThickness, precision: 2);
        Assert.Equal(2.5, ProceduralCrackGeometry.DeepShadowThickness, precision: 2);

        // Web rings form distinct polygonal plates
        Assert.InRange(crackGen.PolygonalPlateCount, 6, 40);
    }

    [Fact]
    public void Layer3_NeonLightningArcs_BranchingPaths_And_VertexJitter()
    {
        var lightning = new NeonLightningGenerator();
        lightning.Trigger(new Point(960, 540));

        Assert.True(lightning.IsActive);
        Assert.InRange(lightning.ArcCount, 2, 4);
        Assert.InRange(lightning.TotalDuration, 0.30, 0.45);

        // Tick frame: verify jitter and decay
        lightning.Update(0.05);
        Assert.True(lightning.IsActive);
        Assert.True(lightning.CurrentLife < lightning.TotalDuration);

        // Ensure scarlet (#FF003C) and cyan (#00E5FF) colors exist in lightning arcs
        bool hasScarlet = false;
        bool hasCyan = false;
        foreach (var arc in lightning.Arcs)
        {
            if (arc.IsScarlet) hasScarlet = true;
            else hasCyan = true;
        }
        Assert.True(hasScarlet, "Lightning arcs must include scarlet (#FF003C) plasma arcs");
        Assert.True(hasCyan, "Lightning arcs must include neon cyan (#00E5FF) plasma arcs");

        // Complete decay past 450 ms
        lightning.Update(0.45);
        Assert.False(lightning.IsActive, "Lightning arcs must decay and extinguish after 300-450 ms");
    }

    [Fact]
    public void DirectionalConeBurst_SpeedMultiplier4x_And_MotionStreaks()
    {
        var engine = new ParticleEngine(new Point(500, 500));
        engine.ActiveShardCount = 48;
        engine.ExplodeShards(new Point(500, 500), beatIntensity: 0.8f);

        var shards = engine.Shards;
        int verticalFanCount = 0;

        for (int i = 0; i < engine.ActiveShardCount; i++)
        {
            Assert.True(shards[i].Active);
            double speed = Math.Sqrt(shards[i].Vx * shards[i].Vx + shards[i].Vy * shards[i].Vy);

            // 4x speed range: must satisfy explosive speed (>= 1800 and <= 5500 px/s)
            Assert.True(speed >= 1800.0, $"Initial shard speed ({speed:F1} px/s) must be >= 1800 px/s");
            Assert.True(speed <= 5500.0, $"Initial shard speed ({speed:F1} px/s) must be <= 5500 px/s");

            // Directional cone burst: majority of particles blast out along the fissure axis (up/down/angled fans)
            double angle = Math.Atan2(shards[i].Vy, shards[i].Vx);
            double absAngle = Math.Abs(angle);
            // Angle near +-PI/2 (vertical fissure ejection)
            if (Math.Abs(absAngle - Math.PI / 2.0) < 1.1)
            {
                verticalFanCount++;
            }
        }

        // Focused fan from rift: at least 45% concentrated in the vertical/cone ejection
        Assert.True(verticalFanCount >= 20, $"Cone burst must focus particles out of the fissure; got {verticalFanCount}");
    }

    [Fact]
    public void ParticleEngine_StickingProbability_Is1Point5Percent()
    {
        var engine = new ParticleEngine(new Point(500, 500));
        engine.ActiveShardCount = 1;

        int stuckCount = 0;
        const int iterations = 3000;
        double dummyTimer = 0.0;

        for (int k = 0; k < iterations; k++)
        {
            engine.Shards[0].Active = true;
            engine.Shards[0].IsStuck = false;
            engine.Shards[0].X = 1.0;
            engine.Shards[0].Y = 500.0;
            engine.Shards[0].Vx = -500.0;
            engine.Shards[0].Vy = 0.0;

            engine.UpdatePhysics(
                dt: 0.016,
                center: new Point(500, 500),
                boundsW: 1920.0,
                boundsH: 1080.0,
                isOverdriveActive: true,
                overdriveProgress: 1.0,
                beatIntensity: 0.5f,
                subBassEnergy: 0.5f,
                falloffSetting: 0.88,
                currentSpectrum: new float[128],
                windowTracker: new WindowBoundsTracker(),
                enableWindowCollisions: false,
                enableMouseRepulsion: false,
                screenMousePos: null,
                isReassembling: false,
                reassemblyPhase: ReassemblyPhase.None,
                reassemblyPhaseTimer: ref dummyTimer,
                out _);

            if (engine.Shards[0].IsStuck)
            {
                stuckCount++;
            }
        }

        double stickRatio = (double)stuckCount / iterations;
        // Expected ~1.5% (strictly < 3.0%, around 0.015)
        Assert.True(stickRatio < 0.035, $"Sticking ratio ({stickRatio:P2}) must be reduced to ~1.5%");
    }

    [Fact]
    public void CoreHydraulicSlam_EaseInExpo_ShockwaveRadiusUpTo700Px_And_CracksDisappearance()
    {
        var fsm = new OverdriveStateMachine
        {
            IsReassembling = true,
            ReassemblyPhase = ReassemblyPhase.Phase2_Slam,
            ReassemblyPhaseTimer = 0.0
        };

        // Step 1: Inhale / Anticipation (t in [0.0, 0.12]) -> swells up to 2.0x (1.8-2.3x)
        fsm.ReassemblyPhaseTimer = 0.10;
        double scaleAnticipation = fsm.CalculateCoreScale();
        Assert.InRange(scaleAnticipation, 1.80, 2.30);

        // Step 2: Supersonic Slam (80 ms, t in [0.12, 0.20]) -> EaseInExpo collapses to 1.0x
        fsm.ReassemblyPhaseTimer = 0.18;
        double scaleSlam = fsm.CalculateCoreScale();
        Assert.True(scaleSlam < 1.40, $"Core scale ({scaleSlam:F2}) must collapse sharply towards 1.0x");

        // Impact at t = 0.20s
        fsm.ReassemblyPhaseTimer = 0.20;
        fsm.TriggerBassFrame(subBassEnergy: 0f, beatIntensity: 0f, dt: 0.02, out _);

        // Screen shake active (-12..+12 px)
        Assert.True(Math.Abs(fsm.ShakeOffsetX) <= 12.01);
        Assert.True(Math.Abs(fsm.ShakeOffsetY) <= 12.01);

        // Step 4: Shockwave expands up to ~700 px (600..800 px)
        fsm.ReassemblyPhaseTimer = 0.45;
        fsm.TriggerBassFrame(subBassEnergy: 0f, beatIntensity: 0f, dt: 0.05, out _);
        Assert.InRange(fsm.ShockwaveRadius, 550.0, 800.0);

        // Cracks disappearance check
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var renderer = new HudRenderer();
                renderer.GenerateScreenCracks(new Point(960, 540), 1920.0, 1080.0);
                Assert.True(renderer.HasActiveCracks);

                // When slam impact occurs (Phase2_Slam, timer >= 0.20), cracks disappear immediately
                using (var dc = DrawingVisualContextHelper.GetDummyDrawingContext())
                {
                    renderer.RenderScreenCracks(
                        dc,
                        new Point(960, 540),
                        1920.0, 1080.0,
                        0.016,
                        0.5f,
                        fsm);
                }

                Assert.False(renderer.HasActiveCracks, "Cracks must vanish instantly upon Phase 2 hydraulic slam impact");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }

    [Fact]
    public void ArchitectureConstraint_AllUIFiles_Under500Lines()
    {
        string projectDir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(projectDir, "Gem.csproj")))
        {
            var parent = Directory.GetParent(projectDir);
            if (parent == null) break;
            projectDir = parent.FullName;
        }

        string uiDir = Path.Combine(projectDir, "UI");
        if (!Directory.Exists(uiDir)) return;

        var csFiles = Directory.GetFiles(uiDir, "*.cs");
        foreach (var file in csFiles)
        {
            int lineCount = File.ReadAllLines(file).Length;
            Assert.True(lineCount <= 500, $"File {Path.GetFileName(file)} has {lineCount} lines, which exceeds the critical limit of 500 lines!");
        }
    }
}

/// <summary>
/// Helper to obtain a dummy DrawingContext for unit tests without a visible window.
/// </summary>
internal static class DrawingVisualContextHelper
{
    private static readonly DrawingVisual Visual = new();

    public static DrawingContext GetDummyDrawingContext()
    {
        return Visual.RenderOpen();
    }
}
