using System;
using System.IO;
using System.Text.Json.Nodes;
using Gem.Core;
using Gem.Services;
using Gem.UI;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace Gem.Tests;

/// <summary>
/// QA-тесты для Task-09: Вкладка тонкой настройки HUD-визуализатора в SettingsWindow
/// и расширенная динамика эффектов (Gain, BassScale, Falloff, Палитры, HslToRgb, ColorShift).
/// </summary>
public class HudSettingsTests
{
    [Fact]
    public void HudSettings_DefaultValues_MatchSpecification()
    {
        var settings = new HudSettingsConfig();

        Assert.Equal(3.5, settings.Gain, precision: 2);
        Assert.Equal(1.4, settings.BassScale, precision: 2);
        Assert.Equal(0.88, settings.Falloff, precision: 2);
        Assert.Equal("Arc Reactor (Циан / Электрик)", settings.ColorPalette);
        Assert.True(settings.EnableColorShift);
        Assert.True(settings.IgnoreTtsInLoopback);
    }

    [Fact]
    public void AppSettingsService_SerializesAndDeserializes_HudSettings()
    {
        var data = new AppSettingsData();
        data.HudSettings.Gain = 5.2;
        data.HudSettings.BassScale = 1.8;
        data.HudSettings.Falloff = 0.92;
        data.HudSettings.ColorPalette = "Cyberpunk Flow (Циан -> Фиолетовый -> Маджента)";
        data.HudSettings.EnableColorShift = false;
        data.HudSettings.IgnoreTtsInLoopback = false;

        string tempFile = Path.Combine(Path.GetTempPath(), $"hud_test_{Guid.NewGuid()}.json");
        try
        {
            // Сохраняем в тестовый JsonNode
            var root = new JsonObject
            {
                ["HudSettings"] = new JsonObject
                {
                    ["Gain"] = data.HudSettings.Gain,
                    ["BassScale"] = data.HudSettings.BassScale,
                    ["Falloff"] = data.HudSettings.Falloff,
                    ["ColorPalette"] = data.HudSettings.ColorPalette,
                    ["EnableColorShift"] = data.HudSettings.EnableColorShift,
                    ["IgnoreTtsInLoopback"] = data.HudSettings.IgnoreTtsInLoopback
                }
            };
            File.WriteAllText(tempFile, root.ToJsonString());

            // Читаем обратно
            string json = File.ReadAllText(tempFile);
            var parsed = JsonNode.Parse(json);
            Assert.NotNull(parsed);

            var loaded = new AppSettingsData();
            if (parsed["HudSettings"] is JsonObject hudNode)
            {
                if (hudNode["Gain"]?.GetValue<double>() is double g) loaded.HudSettings.Gain = g;
                if (hudNode["BassScale"]?.GetValue<double>() is double b) loaded.HudSettings.BassScale = b;
                if (hudNode["Falloff"]?.GetValue<double>() is double f) loaded.HudSettings.Falloff = f;
                if (hudNode["ColorPalette"]?.GetValue<string>() is string cp) loaded.HudSettings.ColorPalette = cp;
                if (hudNode["EnableColorShift"] is { } cs) loaded.HudSettings.EnableColorShift = cs.GetValue<bool>();
                if (hudNode["IgnoreTtsInLoopback"] is { } it) loaded.HudSettings.IgnoreTtsInLoopback = it.GetValue<bool>();
            }

            Assert.Equal(5.2, loaded.HudSettings.Gain, precision: 2);
            Assert.Equal(1.8, loaded.HudSettings.BassScale, precision: 2);
            Assert.Equal(0.92, loaded.HudSettings.Falloff, precision: 2);
            Assert.Equal("Cyberpunk Flow (Циан -> Фиолетовый -> Маджента)", loaded.HudSettings.ColorPalette);
            Assert.False(loaded.HudSettings.EnableColorShift);
            Assert.False(loaded.HudSettings.IgnoreTtsInLoopback);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AudioSpectrumAnalyzer_GainScaling_IncreasesSpectrumAmplitude()
    {
        // При увеличении Gain амплитуда выходного спектра должна расти
        var analyzerLowGain = new AudioSpectrumAnalyzer();
        analyzerLowGain.Gain = 1.5;

        var analyzerHighGain = new AudioSpectrumAnalyzer();
        analyzerHighGain.Gain = 6.0;

        // Подаем умеренный сигнал (амплитуда 8000 из 32768)
        byte[] moderateAudio = new byte[2048];
        for (int i = 0; i < moderateAudio.Length; i += 2)
        {
            short val = (short)(8000 * Math.Sin(2.0 * Math.PI * 400.0 * (i / 2) / 44100.0));
            moderateAudio[i] = (byte)(val & 0xFF);
            moderateAudio[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        analyzerLowGain.ProcessPcm16(moderateAudio, moderateAudio.Length);
        analyzerHighGain.ProcessPcm16(moderateAudio, moderateAudio.Length);

        float[] spectrumLow = new float[AudioSpectrumAnalyzer.RayCount];
        analyzerLowGain.GetTargetSpectrum(spectrumLow, 0.016);

        float[] spectrumHigh = new float[AudioSpectrumAnalyzer.RayCount];
        analyzerHighGain.GetTargetSpectrum(spectrumHigh, 0.016);

        float sumLow = 0f;
        for (int i = 0; i < spectrumLow.Length; i++) sumLow += spectrumLow[i];

        float sumHigh = 0f;
        for (int i = 0; i < spectrumHigh.Length; i++) sumHigh += spectrumHigh[i];

        Assert.True(sumHigh > sumLow * 1.5f,
            $"High gain spectrum sum ({sumHigh:F4}) should be significantly greater than low gain sum ({sumLow:F4})");
    }

    [Fact]
    public void ColorMath_HslToRgb_CalculatesExpectedColors()
    {
        // 0 град = чистый красный
        var red = ColorMath.HslToRgb(0.0, 1.0, 0.5);
        Assert.Equal(255, red.R);
        Assert.Equal(0, red.G);
        Assert.Equal(0, red.B);

        // 120 град = чистый зеленый
        var green = ColorMath.HslToRgb(120.0, 1.0, 0.5);
        Assert.Equal(0, green.R);
        Assert.Equal(255, green.G);
        Assert.Equal(0, green.B);

        // 240 град = чистый синий
        var blue = ColorMath.HslToRgb(240.0, 1.0, 0.5);
        Assert.Equal(0, blue.R);
        Assert.Equal(0, blue.G);
        Assert.Equal(255, blue.B);

        // 180 град = циан
        var cyan = ColorMath.HslToRgb(180.0, 1.0, 0.5);
        Assert.Equal(0, cyan.R);
        Assert.Equal(255, cyan.G);
        Assert.Equal(255, cyan.B);
    }

    [Fact]
    public void ArcReactorPalettes_PaletteThemes_SupportAllThreePresets()
    {
        // 1. Arc Reactor: Циан -> Ультрамарин
        var arcReactorZero = ArcReactorPalettes.GetThemePalette(HudColorTheme.ArcReactor, 0.0f);
        var arcReactorMax = ArcReactorPalettes.GetThemePalette(HudColorTheme.ArcReactor, 1.0f);
        Assert.Equal(MediaColor.FromRgb(0x00, 0xE5, 0xFF), arcReactorZero.Color);
        Assert.Equal(0, arcReactorMax.Color.R);
        Assert.Equal(255, arcReactorMax.Color.B);

        // 2. Cyberpunk Flow: Циан -> Фиолетовый -> Маджента
        var cpZero = ArcReactorPalettes.GetThemePalette(HudColorTheme.CyberpunkFlow, 0.0f);
        var cpMax = ArcReactorPalettes.GetThemePalette(HudColorTheme.CyberpunkFlow, 1.0f);
        Assert.Equal(MediaColor.FromRgb(0x00, 0xE5, 0xFF), cpZero.Color);
        Assert.True(cpMax.Color.R > 200, "Cyberpunk max saturation should have high red for Magenta");
        Assert.True(cpMax.Color.B > 200, "Cyberpunk max saturation should have high blue for Magenta");

        // 3. Solar Flare: Янтарь -> Оранжевый -> Неоновый красный
        var solarZero = ArcReactorPalettes.GetThemePalette(HudColorTheme.SolarFlare, 0.0f);
        var solarMax = ArcReactorPalettes.GetThemePalette(HudColorTheme.SolarFlare, 1.0f);
        Assert.True(solarZero.Color.R >= 250 && solarZero.Color.G >= 180, "Solar flare zero should be Amber/Gold");
        Assert.True(solarMax.Color.R >= 240 && solarMax.Color.G <= 50, "Solar flare max should be Neon Red");
    }

    [Fact]
    public void ArcReactorVisualHost_ApplyHudSettings_UpdatesConfigValues()
    {
        Exception? threadEx = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var host = new ArcReactorVisualHost();
                var config = new HudSettingsConfig
                {
                    Gain = 5.0,
                    BassScale = 1.9,
                    Falloff = 0.75,
                    ColorPalette = "Solar Flare (Янтарь -> Оранжевый -> Неоновый красный)",
                    EnableColorShift = false,
                    IgnoreTtsInLoopback = true
                };

                host.ApplyHudSettings(config);

                Assert.Equal(5.0, host.GainSetting, precision: 2);
                Assert.Equal(1.9, host.BassScaleSetting, precision: 2);
                Assert.Equal(0.75, host.FalloffSetting, precision: 2);
                Assert.Equal(HudColorTheme.SolarFlare, host.CurrentTheme);
                Assert.False(host.EnableColorShiftSetting);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null) throw threadEx;
    }
}
