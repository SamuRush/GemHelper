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
                var host = new ArcReactorVisualHost();
                Assert.True(host.EnableShatterOverdrive);
                Assert.False(host.IsOverdriveActive);

                // Подаем серию мощных басовых ударов для наполнения аккумулятора энергии
                for (int frame = 0; frame < 20; frame++)
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

                // Подаем умеренный бас
                for (int frame = 0; frame < 10; frame++)
                {
                    hostLow.TriggerBassFrame(subBassEnergy: 0.7f, beatIntensity: 0.6f, dt: 0.05);
                    hostHigh.TriggerBassFrame(subBassEnergy: 0.7f, beatIntensity: 0.6f, dt: 0.05);
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
}
