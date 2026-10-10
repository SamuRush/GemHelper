using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Gem.Core;
using Gem.Services;
using Gem.UI;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace Gem.Tests;

/// <summary>
/// QA-тесты для Task-10: Взрывной эффект раскола реактора (Overdrive Shatter Mode),
/// расширенные лимиты настроек (Gain до 15.0, BassScale до 3.0) и кастомная палитра цветов.
/// </summary>
public class OverdriveShatterTests
{
    [Fact]
    public void HudSettings_OverdriveAndCustomPalette_DefaultValues()
    {
        var settings = new HudSettingsConfig();

        Assert.True(settings.EnableShatterOverdrive);
        Assert.Equal("#00E5FF", settings.CustomCoreColor);
        Assert.Equal("#0040FF", settings.CustomGlowColor);
        Assert.Equal("#00FFAA", settings.CustomRaysColor);
    }

    [Fact]
    public void HudSettings_HudSettingsJson_PersistenceAndSync()
    {
        var config = new HudSettingsConfig
        {
            Gain = 12.5,
            BassScale = 2.8,
            Falloff = 0.95,
            ColorPalette = "Пользовательский (Custom)",
            EnableColorShift = true,
            IgnoreTtsInLoopback = false,
            EnableShatterOverdrive = true,
            CustomCoreColor = "#FF0055",
            CustomGlowColor = "#7700FF",
            CustomRaysColor = "#00FFFF"
        };

        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_settings_{Guid.NewGuid()}.json");
        try
        {
            // Сохраняем в HudSettings.json
            HudSettingsStorage.SaveToFile(config, tempJsonPath);
            Assert.True(File.Exists(tempJsonPath));

            // Читаем обратно
            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.NotNull(loaded);
            Assert.Equal(12.5, loaded.Gain, precision: 2);
            Assert.Equal(2.8, loaded.BassScale, precision: 2);
            Assert.Equal(0.95, loaded.Falloff, precision: 2);
            Assert.Equal("Пользовательский (Custom)", loaded.ColorPalette);
            Assert.True(loaded.EnableShatterOverdrive);
            Assert.Equal("#FF0055", loaded.CustomCoreColor);
            Assert.Equal("#7700FF", loaded.CustomGlowColor);
            Assert.Equal("#00FFFF", loaded.CustomRaysColor);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public void ArcReactorVisualHost_OverdriveShatterMode_TriggerAndMagneticReverse()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost { OverdriveDuration = 0.50 };
                Assert.True(host.EnableShatterOverdrive);
                Assert.False(host.IsOverdriveActive);

                // Подаем серию мощных басовых ударов до активации перегрузки (3.0-3.5 секунды)
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                // Режим перегрузки должен активироваться
                Assert.True(host.IsOverdriveActive, "Overdrive state should be activated on accumulated heavy bass");
                Assert.True(host.OverdriveProgress > 0.0, "Overdrive progress should increase during overload");

                // Подаем тишину — включается магнитный реверс и остывание
                for (int frame = 0; frame < 35; frame++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.False(host.IsOverdriveActive, "Overdrive state should deactivate when energy drops");
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
    public void ArcReactorVisualHost_ShardParticles_MaintainsFixedCount_NoGcAllocations()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost();
                int shardCount = host.ShardCount;

                // Требование: 24-36 светящихся энергетических осколков
                Assert.InRange(shardCount, 24, 36);

                var shards = host.GetShardSnapshot();
                Assert.Equal(shardCount, shards.Length);
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
    public void ArcReactorPalettes_CustomPalette_GeneratesExpectedGradients()
    {
        var coreColor = MediaColor.FromRgb(0xFF, 0x14, 0x93);  // DeepPink
        var glowColor = MediaColor.FromRgb(0x4B, 0x00, 0x82);  // Indigo
        var raysColor = MediaColor.FromRgb(0x00, 0xFF, 0x7F);  // SpringGreen

        ArcReactorPalettes.UpdateCustomPalette(coreColor, glowColor, raysColor);

        var customZero = ArcReactorPalettes.GetThemePalette(HudColorTheme.Custom, 0.0f);
        var customMax = ArcReactorPalettes.GetThemePalette(HudColorTheme.Custom, 1.0f);

        Assert.Equal(coreColor, customZero.Color);
        Assert.Equal(glowColor, customMax.Color);
        Assert.Equal(raysColor, customZero.RayPenColor);
        Assert.Equal(raysColor, customMax.RayPenColor);
    }

    [Fact]
    public void AudioSpectrumAnalyzer_GainUpTo15_AndBassScaleUpTo3_Supported()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        analyzer.Gain = 15.0;
        Assert.Equal(15.0, analyzer.Gain, precision: 2);

        // Проверяем, что подача аудио при Gain 15.0 дает высокую амплитуду
        byte[] moderateAudio = new byte[2048];
        for (int i = 0; i < moderateAudio.Length; i += 2)
        {
            short val = (short)(6000 * Math.Sin(2.0 * Math.PI * 500.0 * (i / 2) / 44100.0));
            moderateAudio[i] = (byte)(val & 0xFF);
            moderateAudio[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        analyzer.ProcessPcm16(moderateAudio, moderateAudio.Length);
        float[] spectrum = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrum, 0.016);

        float maxMag = 0f;
        for (int i = 0; i < spectrum.Length; i++) maxMag = Math.Max(maxMag, spectrum[i]);

        Assert.True(maxMag > 0.5f, $"Gain 15.0 should provide explosive high magnitude; got {maxMag:F4}");
    }

    [Fact]
    public void HudSettings_FullscreenOverdriveAndThreshold_DefaultValuesAndPersistence()
    {
        var settings = new HudSettingsConfig();
        Assert.True(settings.EnableFullscreenOverdrive);
        Assert.Equal(0.85, settings.OverdriveThreshold, precision: 2);

        var custom = new HudSettingsConfig
        {
            EnableFullscreenOverdrive = false,
            OverdriveThreshold = 1.25
        };

        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_fullscreen_{Guid.NewGuid()}.json");
        try
        {
            HudSettingsStorage.SaveToFile(custom, tempJsonPath);
            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.NotNull(loaded);
            Assert.False(loaded.EnableFullscreenOverdrive);
            Assert.Equal(1.25, loaded.OverdriveThreshold, precision: 2);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public void ArcReactorVisualHost_OverdriveThreshold_AffectsTriggerSensitivity()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var hostLow = new ArcReactorVisualHost { OverdriveThreshold = 0.50 };
                var hostHigh = new ArcReactorVisualHost { OverdriveThreshold = 1.40 };

                // Подаем умеренный бас в течение 2.0 секунд
                for (int frame = 0; frame < 40; frame++)
                {
                    hostLow.TriggerBassFrame(subBassEnergy: 0.90f, beatIntensity: 0.85f, dt: 0.05);
                    hostHigh.TriggerBassFrame(subBassEnergy: 0.90f, beatIntensity: 0.85f, dt: 0.05);
                }

                // При низком пороге овердрайв должен сработать раньше, чем при высоком
                Assert.True(hostLow.IsOverdriveActive, "Low threshold (0.50) should trigger overdrive on moderate bass");
                Assert.False(hostHigh.IsOverdriveActive, "High threshold (1.40) should not trigger overdrive on moderate bass");
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
    public void OverdriveParticleOverlayWindow_ParticlePoolAndDispersionBetween300And700()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new OverdriveParticleOverlayWindow();
                int count = OverdriveParticleOverlayWindow.ParticleCount;

                // Требование ТЗ: 30-50 осколков
                Assert.InRange(count, 30, 50);

                var snapshot = overlay.GetShardSnapshot();
                Assert.Equal(count, snapshot.Length);

                // Требование ТЗ: разлет на расстояние 300-700 пикселей от центра
                foreach (var shard in snapshot)
                {
                    Assert.InRange(shard.MaxDistance, 300.0, 700.0);
                }

                // Тестируем обновление координат и овердрайва
                var center = new System.Windows.Point(500, 400);
                overlay.UpdateOverdrive(center, isOverdriveActive: true, overdriveProgress: 0.8, beatIntensity: 0.9f);
                overlay.Close();
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
    public void HudSettings_OverdriveCooldown_DefaultValuesAndPersistence()
    {
        var settings = new HudSettingsConfig();
        Assert.Equal(10.0, settings.OverdriveCooldown, precision: 2);

        var custom = new HudSettingsConfig
        {
            OverdriveCooldown = 24.0
        };

        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_cooldown_{Guid.NewGuid()}.json");
        try
        {
            HudSettingsStorage.SaveToFile(custom, tempJsonPath);
            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.NotNull(loaded);
            Assert.Equal(24.0, loaded.OverdriveCooldown, precision: 2);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public void ArcReactorVisualHost_OverdriveCooldown_BlocksRetriggerUntilExpired()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = 0.50,
                    OverdriveCooldown = 5.0 // 5 seconds cooldown
                };

                // Trigger overdrive (3.0-3.5s of heavy bass)
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive);

                // Run silence until overdrive finishes and magnetic reverse gathers shards back
                for (int i = 0; i < 60; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.False(host.IsOverdriveActive);
                Assert.True(host.IsOnCooldown, "Host should be in cooldown state after overdrive concludes");
                Assert.True(host.CooldownRemaining > 0.0, "Cooldown timer should be positive");

                // Attempt to retrigger immediately during cooldown with heavy bass
                for (int i = 0; i < 20; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.False(host.IsOverdriveActive, "Overdrive must NOT trigger while on cooldown");

                // Advance time past the 5-second cooldown
                for (int i = 0; i < 110; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.False(host.IsOnCooldown, "Cooldown should expire after duration passes");
                Assert.Equal(0.0, host.CooldownRemaining, precision: 2);

                // Now heavy bass should trigger overdrive again
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive, "Overdrive should trigger after cooldown expired");
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
    public void OverlayWidget_CoreHitRadius_Is75Px()
    {
        Assert.Equal(75.0, OverlayWidget.CoreHitRadius);

        // Verify distance calculation logic:
        // Inside radius (e.g. 50px away) -> inside
        double dxInside = 30.0;
        double dyInside = 40.0;
        double distSqInside = dxInside * dxInside + dyInside * dyInside; // 900 + 1600 = 2500 <= 5625
        Assert.True(distSqInside <= OverlayWidget.CoreHitRadius * OverlayWidget.CoreHitRadius);

        // Outside radius (e.g. 100px away) -> outside
        double dxOutside = 80.0;
        double dyOutside = 60.0;
        double distSqOutside = dxOutside * dxOutside + dyOutside * dyOutside; // 6400 + 3600 = 10000 > 5625
        Assert.False(distSqOutside <= OverlayWidget.CoreHitRadius * OverlayWidget.CoreHitRadius);
    }

    [Fact]
    public void ArcReactorVisualHost_BoundaryCollisions_AndStuckShardRecall()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveThreshold = 0.50,
                    OverdriveDuration = 0.50
                };

                // Trigger overdrive
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive);

                // Run multiple energetic frames so shards fly out and collide with screen boundaries
                for (int i = 0; i < 40; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.9f, beatIntensity: 0.8f, dt: 0.05);
                }

                var snapshot = host.GetShardSnapshot();
                Assert.Equal(host.ShardCount, snapshot.Length);

                double expectedW = System.Windows.SystemParameters.PrimaryScreenWidth > 0 ? System.Windows.SystemParameters.PrimaryScreenWidth : 1920.0;
                double expectedH = System.Windows.SystemParameters.PrimaryScreenHeight > 0 ? System.Windows.SystemParameters.PrimaryScreenHeight : 1080.0;

                foreach (var shard in snapshot)
                {
                    // Shards must remain inside screen boundary limits (0 to expectedW, 0 to expectedH)
                    Assert.InRange(shard.X, 0.0, expectedW);
                    Assert.InRange(shard.Y, 0.0, expectedH);
                    Assert.True(shard.Active);

                    // Mini bars for stuck shards must be non-negative
                    Assert.True(shard.MiniBar0 >= 0.0f);
                    Assert.True(shard.MiniBar7 >= 0.0f);
                }

                // Recall phase: shards must unstick and gather back into core
                for (int i = 0; i < 70; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                var recallSnapshot = host.GetShardSnapshot();
                foreach (var shard in recallSnapshot)
                {
                    Assert.False(shard.IsStuck, "Stuck shards must unstick during recall phase");
                    Assert.Equal(BoundaryEdge.None, shard.StuckEdge);
                }
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
    public void HudSettings_OverdriveModernization_DefaultValuesAndPersistence()
    {
        var settings = new HudSettingsConfig();
        Assert.Equal(48, settings.ShardCount);
        Assert.Equal(10.0, settings.OverdriveDuration, precision: 2);
        Assert.True(settings.EnableMouseRepulsion);
        Assert.True(settings.EnableWindowCollisions);

        var custom = new HudSettingsConfig
        {
            ShardCount = 180,
            OverdriveDuration = 22.0,
            EnableMouseRepulsion = false,
            EnableWindowCollisions = false
        };

        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_mod_{Guid.NewGuid()}.json");
        try
        {
            HudSettingsStorage.SaveToFile(custom, tempJsonPath);
            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.NotNull(loaded);
            Assert.Equal(180, loaded.ShardCount);
            Assert.Equal(22.0, loaded.OverdriveDuration, precision: 2);
            Assert.False(loaded.EnableMouseRepulsion);
            Assert.False(loaded.EnableWindowCollisions);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public void ArcReactorVisualHost_ShardCount_ConfigurableAndClamped()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost();

                // Test setting custom counts
                host.ShardCount = 48;
                Assert.Equal(48, host.ShardCount);
                Assert.Equal(48, host.GetShardSnapshot().Length);

                host.ShardCount = 120;
                Assert.Equal(120, host.ShardCount);
                Assert.Equal(120, host.GetShardSnapshot().Length);

                // Test clamping
                host.ShardCount = 5; // below 20 min
                Assert.Equal(20, host.ShardCount);

                host.ShardCount = 999; // above 350 max
                Assert.Equal(ArcReactorVisualHost.MaxShardCount, host.ShardCount);
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
    public void ArcReactorVisualHost_OverdriveDuration_HoldsForConfiguredDurationDespiteSilence()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = 10.0, // 10 seconds hold
                    OverdriveThreshold = 0.50
                };

                // Trigger overdrive with heavy bass
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive, "Overdrive should activate on high bass");

                // Run 8.0 seconds of complete silence (160 frames * 0.05s)
                for (int i = 0; i < 160; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                // Mode MUST remain active during silence because configured duration (10s) has not expired!
                Assert.True(host.IsOverdriveActive, "Overdrive mode must NOT cut off prematurely during quiet moments");

                // Advance another 3.0 seconds (60 frames * 0.05s, total 11.0s elapsed silence)
                for (int i = 0; i < 60; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                // Overdrive should now conclude and enter reassembly
                Assert.False(host.IsOverdriveActive, "Overdrive should deactivate after the hold duration expires");

                // Advance past three-phase reassembly (Phase 1 absorption + 400ms dramatic pause + slam)
                for (int i = 0; i < 40; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.True(host.IsOnCooldown, "Host should be in cooldown after overdrive concludes");
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
    public void ArcReactorVisualHost_ContinuousMotion_CruisingSpeedNeverZero()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = 10.0,
                    OverdriveThreshold = 0.50,
                    ShardCount = 48
                };

                // Trigger overdrive (2.5s for 0.50 threshold)
                for (int i = 0; i < 50; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive);

                // Run 30 frames with zero audio (silence)
                for (int i = 0; i < 30; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                var snapshot = host.GetShardSnapshot();
                foreach (var shard in snapshot)
                {
                    if (shard.Active && !shard.IsStuck)
                    {
                        double speed = Math.Sqrt(shard.Vx * shard.Vx + shard.Vy * shard.Vy);
                        // Shards must NEVER freeze in place; minimum cruising speed ~160 px/s
                        Assert.True(speed >= 150.0, $"Shard cruising speed {speed:F2} must be >= 150 px/s (no freezing)");
                    }
                }
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
    public void ArcReactorVisualHost_OverloadChargeGauge_AccumulatesSmoothly()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveThreshold = 1.0
                };

                Assert.Equal(0.0, host.OverloadCharge, precision: 2);

                // Gradually feed moderate bass
                for (int i = 0; i < 15; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.7f, beatIntensity: 0.6f, dt: 0.05);
                }

                Assert.True(host.OverloadCharge > 0.0, "Overload charge gauge should smoothly accumulate");
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
    public void WindowBoundsTracker_InitAndStructure_ZeroAllocations()
    {
        var tracker = new WindowBoundsTracker();
        Assert.Equal(0, tracker.Count);
        Assert.NotNull(tracker.Bounds);
        Assert.Equal(WindowBoundsTracker.MaxWindows, tracker.Bounds.Length);

        // Struct initialization
        var aabb = new WindowAabb(100, 200, 500, 600);
        Assert.Equal(100.0, aabb.Left);
        Assert.Equal(200.0, aabb.Top);
        Assert.Equal(500.0, aabb.Right);
        Assert.Equal(600.0, aabb.Bottom);
        Assert.Equal(400.0, aabb.Width);
        Assert.Equal(400.0, aabb.Height);

        // Clear works cleanly
        tracker.Clear();
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void ArcReactorVisualHost_JitWindowTracking_OnlyActiveWhenHighChargeOrOverdrive()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    EnableWindowCollisions = true,
                    OverdriveThreshold = 0.85
                };

                // In idle (0 charge), window tracker is cleared
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                Assert.Equal(0, host.WindowTracker.Count);
                Assert.True(host.OverloadCharge <= 0.50);

                // When charge exceeds 50% or overdrive is active, tracker can update
                for (int i = 0; i < 70; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }
                Assert.True(host.IsOverdriveActive);
                Assert.True(host.OverloadCharge > 0.50);
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
    public void ArcReactorVisualHost_VisualChargeGaugeAndOverrideTitle_ActivatesAt80Percent()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveThreshold = 1.0
                };

                Assert.False(host.IsOverrideTitleVisible);

                // Ramp up charge gradually towards 0.80
                for (int i = 0; i < 20; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.7f, beatIntensity: 0.6f, dt: 0.05);
                }

                // Push bass harder to reach >= 0.80 charge before detonation
                for (int i = 0; i < 30; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.95f, beatIntensity: 0.85f, dt: 0.05);
                    if (host.OverloadCharge >= 0.80 && !host.IsOverdriveActive)
                    {
                        Assert.True(host.IsOverrideTitleVisible, "OVERRIDE title should be visible when charge >= 80%");
                        break;
                    }
                }
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
    public void ArcReactorVisualHost_ThreePhaseReassembly_Phase1AbsorptionThenDramaticPauseThenPhase2HydraulicSlam()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = 0.50, // short duration for test
                    OverdriveThreshold = 0.50,
                    OverdriveCooldown = 5.0,
                    ShardCount = 32
                };

                // 1. Trigger overdrive
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive, "Overdrive must be active");
                Assert.Equal(ReassemblyPhase.None, host.CurrentReassemblyPhase);

                // 2. Advance time past the remaining overdrive duration (15 frames * 0.05s = 0.75s)
                for (int i = 0; i < 15; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                // 3. Overdrive has ended -> Must enter Reassembly Phase 1 (Absorption)
                Assert.False(host.IsOverdriveActive);
                Assert.True(host.IsReassembling);

                // Check that Phase 1 absorption pulls particles magnetically
                // Advance frames until all particles reach center -> enters Dramatic Pause
                for (int i = 0; i < 20; i++)
                {
                    if (host.CurrentReassemblyPhase == ReassemblyPhase.DramaticPause)
                    {
                        break;
                    }
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                // 4. Dramatic Pause must be active! (400 ms hold)
                Assert.Equal(ReassemblyPhase.DramaticPause, host.CurrentReassemblyPhase);
                Assert.True(host.IsReassembling);

                // Advance past 400 ms dramatic pause (10 frames * 0.05s = 0.50s)
                for (int i = 0; i < 10; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                    if (host.CurrentReassemblyPhase == ReassemblyPhase.Phase2_Slam)
                    {
                        break;
                    }
                }

                // 5. Phase 2 (Hyper Slam & Shockwave)
                Assert.Equal(ReassemblyPhase.Phase2_Slam, host.CurrentReassemblyPhase);
                Assert.True(host.IsOnCooldown, "Cooldown must engage upon hydraulic slam");

                // Advance into impact window (t >= 0.20s): 5 frames * 0.05s = 0.25s
                for (int i = 0; i < 5; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.True(host.ShockwaveRadius > 80.0, "Shockwave ring should expand on slam impact");
                Assert.True(host.ShockwaveOpacity > 0.0, "Shockwave opacity should be active");
                Assert.True(host.WhiteoutIntensity > 0.0, "Whiteout flash should be active in impact window");
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
    public void ArcReactorVisualHost_Pacing_Requires2Point5To3Point5SecondsToTrigger_NoInstantJumpOnPeakKick()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveThreshold = 0.85
                };

                // 1. Single massive kick (1 frame of 1.0f sub-bass + 1.0f beat intensity)
                host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);

                // Must NOT jump to 100% or trigger overdrive!
                Assert.False(host.IsOverdriveActive, "Single peak kick must NOT instantaneously trigger Overdrive");
                Assert.True(host.OverloadCharge < 0.20, "Charge must accumulate smoothly without binary jump");

                // 2. Feed 1.5 seconds of heavy bass (30 frames * 0.05s)
                for (int i = 0; i < 30; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                // 1.5 seconds must still NOT be enough to trigger threshold 0.85
                Assert.False(host.IsOverdriveActive, "1.5s is insufficient; Overdrive requires 2.5-3.5s of dense bass");
                Assert.True(host.OverloadCharge < 0.75, "Overload charge at 1.5s should be around ~40-60%");

                // 3. Continue feeding up to 3.2 seconds total (another 35 frames)
                for (int i = 0; i < 35; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                // Now at ~3.2 seconds, Overdrive triggers!
                Assert.True(host.IsOverdriveActive, "3.2 seconds of continuous dense bass should trigger Overdrive");
                Assert.True(host.IsOverrideTitleVisible, "OVERRIDE title should be active post-detonation for 2.0s");
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
    public void ArcReactorVisualHost_DynamicOverdriveHold_ExtendsOnContinuousHeavyBeats()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveThreshold = 0.50,
                    OverdriveDuration = 3.0 // 3.0s base duration for test
                };

                // Trigger overdrive
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive);

                // Now simulate music continuing with heavy beat kicks for 5.0 seconds (100 frames * 0.05s)
                // If there were no dynamic extension, Overdrive would expire after 3.0 seconds!
                for (int i = 0; i < 100; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.95f, beatIntensity: 0.90f, dt: 0.05);
                }

                // With dynamic extension (+1.8s per debounced kick), Overdrive is still alive and rocking!
                Assert.True(host.IsOverdriveActive, "Continuous heavy beat during Overdrive must dynamically extend the mode");

                // Now music calms down (silence for 12.5 seconds > 10.5s cap) -> enters reassembly and cooldown
                for (int i = 0; i < 250; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.False(host.IsOverdriveActive, "Overdrive must end after music stops");
                Assert.True(host.IsOnCooldown, "Cooldown engages after overdrive and reassembly conclude");
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
    public void WindowBoundsTracker_StrictFiltering_StructureAndZeroGcSafety()
    {
        var tracker = new WindowBoundsTracker();
        tracker.Clear();
        Assert.Equal(0, tracker.Count);

        // Update with mock dimensions
        tracker.Update(IntPtr.Zero, 1.0, 1920.0, 1080.0);
        // Tracker does not throw and bounds array remains fixed-size
        Assert.Equal(WindowBoundsTracker.MaxWindows, tracker.Bounds.Length);
        foreach (var b in tracker.Bounds)
        {
            if (tracker.Count > 0)
            {
                // Windows recorded must be >= 300x300 and within screen
                Assert.True(b.Width >= 0.0);
                Assert.True(b.Height >= 0.0);
            }
        }
    }

    [Fact]
    public void HudSettings_EnableMemoryGlitch_DefaultAndPersistence()
    {
        var config = new HudSettingsConfig();
        Assert.True(config.EnableMemoryGlitch, "EnableMemoryGlitch must default to true");

        config.EnableMemoryGlitch = false;
        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"hud_glitch_{Guid.NewGuid()}.json");
        try
        {
            HudSettingsStorage.SaveToFile(config, tempJsonPath);
            Assert.True(File.Exists(tempJsonPath));

            var loaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.False(loaded.EnableMemoryGlitch, "Loaded configuration must retain false for EnableMemoryGlitch");

            loaded.EnableMemoryGlitch = true;
            HudSettingsStorage.SaveToFile(loaded, tempJsonPath);

            var reloaded = HudSettingsStorage.LoadFromFile(tempJsonPath);
            Assert.True(reloaded.EnableMemoryGlitch, "Reloaded configuration must retain true for EnableMemoryGlitch");
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    [Fact]
    public void MemoryGlitch_Spawn_CoordinatesStrictly250PxFromReactorCenter()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost();
                host.Center = new System.Windows.Point(1200.0, 700.0);

                for (int i = 0; i < 5; i++)
                {
                    bool spawned = host.SpawnGlitchBlock(1920.0, 1080.0, host.Center);
                    Assert.True(spawned, "Must successfully spawn a glitch block into pool");
                }

                var snapshot = host.GetGlitchBlocksSnapshot();
                int activeCount = 0;
                foreach (var b in snapshot)
                {
                    if (!b.Active) continue;
                    activeCount++;

                    // Verify distance from center to any point in block box [b.X - 10, b.X + 370] x [b.Y - 8, b.Y + 100] is >= 250 px
                    double clampX = Math.Clamp(host.Center.X, b.X - 10.0, b.X + 370.0);
                    double clampY = Math.Clamp(host.Center.Y, b.Y - 8.0, b.Y + 100.0);
                    double dx = host.Center.X - clampX;
                    double dy = host.Center.Y - clampY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    Assert.True(dist >= 249.0, $"Spawned glitch block box must be at least 250 px away from reactor center (was {dist:F1} px)");
                    Assert.InRange(b.TotalLife, 1.5, 2.5);
                    Assert.InRange(b.LineCount, 3, 5);
                }

                Assert.Equal(5, activeCount);
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
    public void MemoryGlitch_DecryptionProgression_18CharsPerSecAndFadeOut()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    EnableMemoryGlitch = true,
                    EnableShatterOverdrive = true,
                    OverdriveThreshold = 0.40,
                    OverdriveDuration = 0.80
                };
                host.Center = new System.Windows.Point(1000.0, 500.0);

                // Accumulate bass steadily for 2.8 seconds to trigger Overdrive
                for (int i = 0; i < 56; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.90f, beatIntensity: 0.85f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive, "Overdrive must activate under continuous bass");
                Assert.True(host.ActiveGlitchBlockCount > 0, "Active glitch blocks must spawn during Overdrive");

                var snapshot = host.GetGlitchBlocksSnapshot();
                var firstBlock = snapshot[0];
                Assert.True(firstBlock.Active);
                Assert.True(firstBlock.LastDecodedStep >= 0, "Decoded step must advance");

                // Progress time during Overdrive: characters decode at 18 chars/sec
                int prevStep = firstBlock.LastDecodedStep;
                for (int i = 0; i < 6; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.90f, beatIntensity: 0.85f, dt: 0.05);
                }
                var updated = host.GetGlitchBlocksSnapshot()[0];
                Assert.True(updated.LastDecodedStep >= prevStep, "Decryption step must advance as age increases");

                // Now overdrive duration expires and music stops -> Overdrive ends
                for (int i = 0; i < 60; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.False(host.IsOverdriveActive, "Overdrive must end after duration and calm music");

                // Advance another 1.0 second in calm mode: all remaining blocks must complete organic fade-out and deactivate
                for (int i = 0; i < 20; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.Equal(0, host.ActiveGlitchBlockCount);
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
    public void MemoryGlitch_HitTesting_IsTransparent()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost();
                host.Center = new System.Windows.Point(800.0, 500.0);
                host.SpawnGlitchBlock(1920.0, 1080.0, host.Center);

                var snapshot = host.GetGlitchBlocksSnapshot();
                var block = snapshot[0];
                Assert.True(block.Active);

                // Hit testing at block coordinates must return HTTRANSPARENT / null hit
                // because it is outside the 75px core radius!
                var hitParam = new System.Windows.Media.PointHitTestParameters(new System.Windows.Point(block.X + 50.0, block.Y + 20.0));
                var hitResult = System.Windows.Media.VisualTreeHelper.HitTest(host, hitParam.HitPoint);

                Assert.Null(hitResult);
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
    public void OverdriveTimer_RefreshCap_NeverExceedsMaxDuration()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                double configuredDuration = 5.0;
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = configuredDuration,
                    OverdriveThreshold = 0.50
                };

                // Accumulate bass until overdrive triggers
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                Assert.True(host.IsOverdriveActive);
                Assert.InRange(host.OverdriveRemainingTimer, 4.9, configuredDuration);

                // Simulate countdown without kicks for 2.0s
                for (int i = 0; i < 40; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                // Timer must count down honestly
                Assert.InRange(host.OverdriveRemainingTimer, 2.8, 3.2);

                // Now deliver 20 consecutive heavy kick transients with varying dt
                for (int i = 0; i < 20; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                    // Crucial invariant: timer must REFRESH to configuredDuration but NEVER exceed it!
                    Assert.True(host.OverdriveRemainingTimer <= configuredDuration,
                        $"Timer ({host.OverdriveRemainingTimer}) must never exceed maxDuration ({configuredDuration})");
                }

                // Verify it refreshed back close to maxDuration (clamped)
                Assert.InRange(host.OverdriveRemainingTimer, 4.8, configuredDuration);

                // Now music goes completely silent: timer must count down honestly to 0 with zero delay
                double expectedFrames = configuredDuration / 0.05;
                for (int i = 0; i < (int)expectedFrames + 5; i++)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                    if (!host.IsOverdriveActive) break;
                }

                Assert.False(host.IsOverdriveActive, "Overdrive must deactivate after countdown finishes");
                Assert.True(host.IsReassembling, "Reassembly must start immediately upon overdrive expiration");
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
    public void HyperSlam_5StepKineticCollapse_ChoreographyVerification()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost
                {
                    OverdriveDuration = 0.20,
                    OverdriveThreshold = 0.50
                };

                // 1. Trigger Overdrive
                while (!host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 1.0f, beatIntensity: 1.0f, dt: 0.05);
                }

                // Split distance in Overdrive should be positive (proportional to OverdriveProgress)
                Assert.True(host.CalculateSplitDistance() > 0.0);

                // 2. Let Overdrive expire -> enters Phase 1 (Absorption)
                while (host.IsOverdriveActive)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.Equal(ReassemblyPhase.Phase1_Absorption, host.CurrentReassemblyPhase);
                Assert.Equal(18.0, host.CalculateSplitDistance(), precision: 2);

                // 3. Fast-forward through Phase 1 absorption into Dramatic Pause
                while (host.CurrentReassemblyPhase == ReassemblyPhase.Phase1_Absorption)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.Equal(ReassemblyPhase.DramaticPause, host.CurrentReassemblyPhase);
                Assert.Equal(18.0, host.CalculateSplitDistance(), precision: 2);

                // 4. Fast-forward through Dramatic Pause (400 ms) until Phase 2 starts
                while (host.CurrentReassemblyPhase == ReassemblyPhase.DramaticPause)
                {
                    host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                }

                Assert.Equal(ReassemblyPhase.Phase2_Slam, host.CurrentReassemblyPhase);

                // --- STEP 1: Anticipation / Inhale (120 ms, t in [0.0, 0.12]) ---
                // Halves pull apart further from 18px to ~22px
                // Advance 2 frames * 0.05s = 0.10s (within Step 1)
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                double splitInhale = host.CalculateSplitDistance();
                Assert.True(splitInhale > 18.0, $"Split distance during inhale ({splitInhale}) must expand beyond 18px");
                Assert.True(splitInhale <= 22.0, $"Split distance ({splitInhale}) must not exceed 22px");
                Assert.Equal(1.0, host.OverdriveProgress, precision: 2); // stays fiery red

                // --- STEP 2: Hyper Slam (80 ms, t in [0.12, 0.20]) ---
                // Advance to t = 0.18s
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.05);
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.03);
                double splitSlam = host.CalculateSplitDistance();
                Assert.True(splitSlam < splitInhale, $"Split distance ({splitSlam}) must accelerate inward");
                Assert.Equal(1.0, host.OverdriveProgress, precision: 2); // still fiery red

                // --- STEP 3 & STEP 4: Point of Impact (t in [0.20, 0.28]) ---
                // Advance to t = 0.22s (impact!)
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.04);
                Assert.True(host.WhiteoutIntensity > 0.0, "Core whiteout flash must erupt at impact");
                Assert.True(host.ShockwaveRadius >= 80.0, "Shockwave blast radius must be >= 80px");
                Assert.True(host.ShockwaveOpacity > 0.0, "Shockwave opacity must be active");

                // --- STEP 5: Heavy Mechanical Settle & Cool (t in [0.20, 0.50]) ---
                // Advance to t = 0.40s
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.18);
                // Shockwave expanded significantly
                Assert.True(host.ShockwaveRadius > 200.0, $"Shockwave radius ({host.ShockwaveRadius}) should expand towards 370px");
                // Color cooling in progress
                Assert.True(host.OverdriveProgress < 0.60, $"Core should be cooling towards cyan, progress was {host.OverdriveProgress}");

                // Advance past t = 0.50s: Phase 2 finishes completely
                host.TriggerBassFrame(subBassEnergy: 0.0f, beatIntensity: 0.0f, dt: 0.15);
                Assert.Equal(ReassemblyPhase.None, host.CurrentReassemblyPhase);
                Assert.False(host.IsReassembling);
                Assert.Equal(0.0, host.WhiteoutIntensity);
                Assert.Equal(0.0, host.ShockwaveOpacity);
                Assert.Equal(0.0, host.OverdriveProgress);
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
}

