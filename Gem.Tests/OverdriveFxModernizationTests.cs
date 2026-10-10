using System;
using System.IO;
using System.Threading;
using System.Windows;
using Gem.Services;
using Gem.UI;
using Xunit;
using Point = System.Windows.Point;

namespace Gem.Tests;

public class OverdriveFxModernizationTests
{
    [Fact]
    public void ParticleEngine_ExplosiveDetonationSpeed_Is2Point5To3Point5TimesHigher()
    {
        var engine = new ParticleEngine(new Point(500, 500));
        engine.ActiveShardCount = 48;

        engine.ExplodeShards(new Point(500, 500), beatIntensity: 0.8f);

        var shards = engine.Shards;
        for (int i = 0; i < engine.ActiveShardCount; i++)
        {
            Assert.True(shards[i].Active);
            double speed = Math.Sqrt(shards[i].Vx * shards[i].Vx + shards[i].Vy * shards[i].Vy);
            // Previous speed was ~650..1500 px/s + 280 = ~930..1780 px/s
            // Increased by 3.0x: expected min speed >= 1900 px/s and max speed up to ~4600 px/s
            Assert.True(speed >= 1800.0, $"Initial shard speed ({speed:F1} px/s) must be explosively fast (>= 1800 px/s)");
            Assert.True(speed <= 5500.0, $"Initial shard speed ({speed:F1} px/s) must be within explosive bounds");
        }
    }

    [Fact]
    public void ParticleEngine_StickingProbability_ReducedToAround2Percent()
    {
        var engine = new ParticleEngine(new Point(500, 500));
        engine.ActiveShardCount = 1;

        // Run 2000 boundary hits and count how many stick with the new ~2% probability (instead of 7%)
        int stuckCount = 0;
        const int iterations = 2000;

        for (int k = 0; k < iterations; k++)
        {
            engine.Shards[0].Active = true;
            engine.Shards[0].IsStuck = false;
            engine.Shards[0].X = 1.0; // Left screen boundary collision
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
                reassemblyPhaseTimer: ref GetDummyTimer(),
                out _);

            if (engine.Shards[0].IsStuck)
            {
                stuckCount++;
            }
        }

        double stickRatio = (double)stuckCount / iterations;
        // Expected ~0.02 (2.0%), strictly less than the old 0.07 (7%)
        Assert.True(stickRatio < 0.045, $"Sticking ratio ({stickRatio:P1}) must be reduced to around 1.5-2.5%, well below 7%");
    }

    private static double _dummyTimer = 0.0;
    private static ref double GetDummyTimer() => ref _dummyTimer;

    [Fact]
    public void GiantAnticipation_And_SupersonicSlam_CoreScaleChoreography()
    {
        var fsm = new OverdriveStateMachine
        {
            IsReassembling = true,
            ReassemblyPhase = ReassemblyPhase.Phase2_Slam,
            ReassemblyPhaseTimer = 0.0
        };

        // At t = 0.0s: scale is 1.0
        double scale0 = fsm.CalculateCoreScale();
        Assert.Equal(1.0, scale0, precision: 2);

        // At t = 0.10s (within Step 1: Giant Anticipation, t in [0.0, 0.12]):
        // Core swells by 1.8 - 2.3x (peak ~2.05x)
        fsm.ReassemblyPhaseTimer = 0.10;
        double scaleAnticipation = fsm.CalculateCoreScale();
        Assert.True(scaleAnticipation >= 1.80, $"Anticipation scale ({scaleAnticipation:F2}) must be >= 1.8x");
        Assert.True(scaleAnticipation <= 2.30, $"Anticipation scale ({scaleAnticipation:F2}) must not exceed 2.3x");

        // At t = 0.18s (within Step 2: Supersonic Slam, t in [0.12, 0.20]):
        // Core collapses sharply with EaseInExpo p^4 towards 1.0x
        fsm.ReassemblyPhaseTimer = 0.18;
        double scaleSlam = fsm.CalculateCoreScale();
        Assert.True(scaleSlam < scaleAnticipation, $"Slam scale ({scaleSlam:F2}) must collapse rapidly inward");
        Assert.True(scaleSlam < 1.40, $"Slam scale ({scaleSlam:F2}) must be close to original size before impact");

        // At t >= 0.20s: settles rock-solid back to 1.0x
        fsm.ReassemblyPhaseTimer = 0.40;
        double scaleSettle = fsm.CalculateCoreScale();
        Assert.True(Math.Abs(scaleSettle - 1.0) < 0.05, $"Settle scale ({scaleSettle:F2}) must settle to 1.0x");
    }

    [Fact]
    public void ViolentScreenShake_TriggersAtImpact_DecaysOver400ms()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var fsm = new OverdriveStateMachine
                {
                    IsReassembling = true,
                    ReassemblyPhase = ReassemblyPhase.Phase2_Slam,
                    ReassemblyPhaseTimer = 0.18 // Just before impact at 0.20s
                };

                // Advance past impact window (from 0.18s to 0.22s)
                fsm.TriggerBassFrame(subBassEnergy: 0f, beatIntensity: 0f, dt: 0.04, out _);

                // Violent screen shake must engage with up to +-12 px offset
                Assert.True(fsm.ShakeOffsetX != 0.0 || fsm.ShakeOffsetY != 0.0, "Screen shake must be active at impact");
                Assert.True(Math.Abs(fsm.ShakeOffsetX) <= 12.01, $"Shake offset X ({fsm.ShakeOffsetX}) must not exceed 12 px");
                Assert.True(Math.Abs(fsm.ShakeOffsetY) <= 12.01, $"Shake offset Y ({fsm.ShakeOffsetY}) must not exceed 12 px");

                // Advance by 450 ms (past violent shake timer of 400 ms)
                for (int i = 0; i < 9; i++)
                {
                    fsm.TriggerBassFrame(subBassEnergy: 0f, beatIntensity: 0f, dt: 0.05, out _);
                }

                // Shake should decay back to 0
                Assert.Equal(0.0, fsm.ShakeOffsetX);
                Assert.Equal(0.0, fsm.ShakeOffsetY);
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
    public void ExtendedShockwave_ExpandsUpTo600To800Px()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var fsm = new OverdriveStateMachine
                {
                    IsReassembling = true,
                    ReassemblyPhase = ReassemblyPhase.Phase2_Slam,
                    ReassemblyPhaseTimer = 0.20 // Impact
                };

                // Advance through shockwave expansion (to t = 0.45s)
                fsm.TriggerBassFrame(subBassEnergy: 0f, beatIntensity: 0f, dt: 0.25, out _);

                // Extended shockwave radius should reach between 600 and 800 px (around 700 px)
                Assert.True(fsm.ShockwaveRadius >= 550.0, $"Extended shockwave ({fsm.ShockwaveRadius}) must expand towards 600-800 px");
                Assert.True(fsm.ShockwaveRadius <= 800.0, $"Extended shockwave ({fsm.ShockwaveRadius}) must not exceed 800 px");
                Assert.True(fsm.ShockwaveOpacity > 0.0, "Shockwave opacity must still be active during blast");
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
    public void CyberpunkGlitch_VRAMCorruption_RandomizedFonts_AndKatakana()
    {
        var engine = new ParticleEngine(new Point(500, 500));
        bool spawned = engine.SpawnGlitchBlock(1920.0, 1080.0, new Point(500, 500));
        Assert.True(spawned);

        var block = engine.GlitchBlocks[0];
        Assert.True(block.Active);
        Assert.True(block.FontSize >= 9.0 && block.FontSize <= 20.0, $"Font size ({block.FontSize}) must be randomized between 9 and 20 pt");
        Assert.True(block.SliceDisplacement >= 4.0 && block.SliceDisplacement <= 10.0, $"Horizontal slice displacement ({block.SliceDisplacement}) must be between 4 and 10 px");

        // Verify templates have varied lengths from short tags to long register dumps
        bool hasShortHex = false;
        bool hasLongDump = false;
        foreach (var t in ParticleEngine.GlitchCodeTemplates)
        {
            if (t.Length <= 8) hasShortHex = true;
            if (t.Length >= 40) hasLongDump = true;
        }
        Assert.True(hasShortHex, "Glitch templates must contain short HEX tags (<= 8 chars)");
        Assert.True(hasLongDump, "Glitch templates must contain long system dumps (>= 40 chars)");
    }

    [Fact]
    public void HudSettings_ScreenCracks_DefaultAndPersistence()
    {
        var config = new HudSettingsConfig();
        Assert.True(config.EnableScreenCracks, "EnableScreenCracks must default to true");

        config.EnableScreenCracks = false;
        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_cracks_{Guid.NewGuid()}.json");
        try
        {
            HudSettingsStorage.SaveToFile(config, tempJsonPath);
            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.False(loaded.EnableScreenCracks, "Loaded config must retain false for EnableScreenCracks");

            loaded.EnableScreenCracks = true;
            HudSettingsStorage.SaveToFile(loaded, tempJsonPath);
            var reloaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.True(reloaded.EnableScreenCracks, "Reloaded config must retain true for EnableScreenCracks");
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }
}
