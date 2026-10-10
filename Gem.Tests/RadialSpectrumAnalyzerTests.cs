using System;
using Gem.Core;
using Gem.UI;
using NAudio.Wave;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace Gem.Tests;

/// <summary>
/// QA-тесты для Task-08: Честный радиальный FFT-эквалайзер (Radial Spectrum Analyzer),
/// гравитационный спад (Peak Falloff / Decay) и устранение бинарных вспышек HUD.
/// </summary>
public class RadialSpectrumAnalyzerTests
{
    private static byte[] GenerateSinePcm16(double freqHz, int sampleRate, int sampleCount, short amplitude = 28000)
    {
        byte[] buf = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            short val = (short)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate));
            buf[i * 2] = (byte)(val & 0xFF);
            buf[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buf;
    }

    private static byte[] GenerateSineLoopbackFloat(double freqHz, int sampleRate, int frameCount, float amplitude = 0.85f)
    {
        byte[] buf = new byte[frameCount * 2 * 4];
        int idx = 0;
        for (int f = 0; f < frameCount; f++)
        {
            float s = amplitude * (float)Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate);
            byte[] le = BitConverter.GetBytes(s);
            buf[idx++] = le[0]; buf[idx++] = le[1]; buf[idx++] = le[2]; buf[idx++] = le[3];
            buf[idx++] = le[0]; buf[idx++] = le[1]; buf[idx++] = le[2]; buf[idx++] = le[3];
        }
        return buf;
    }

    [Fact]
    public void AudioSpectrumAnalyzer_DoesNotCutOff_MidAndHighFrequencies()
    {
        // ARRANGE: в старой версии фильтр 150 Гц срезал весь спектр перед FFT.
        // Честный анализатор должен улавливать средние частоты (например, 1500 Гц) без затухания в 0.
        var analyzer = new AudioSpectrumAnalyzer();
        byte[] signal1500Hz = GenerateSinePcm16(1500.0, 44100, 2048, amplitude: 26000);

        // ACT
        analyzer.ProcessPcm16(signal1500Hz, signal1500Hz.Length);
        float[] spectrum = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrum, deltaTime: 0.016);

        // ASSERT: суммарная энергия и максимальная полоса для 1500 Гц должны быть четко выражены (> 0.15)
        float maxVal = 0f;
        for (int i = 0; i < spectrum.Length; i++)
        {
            if (spectrum[i] > maxVal) maxVal = spectrum[i];
        }

        Assert.True(maxVal > 0.15f, $"1500 Hz tone should generate significant magnitude in mid bands; got max={maxVal:F4}");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_ProcessLoopback_PropagatesFrequenciesToSpectrum()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        byte[] loopbackAudio = GenerateSineLoopbackFloat(1000.0, 48000, 2048, amplitude: 0.8f);

        analyzer.ProcessLoopback(loopbackAudio, loopbackAudio.Length, fmt);
        float[] spectrum = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrum, deltaTime: 0.016);

        float sum = 0f;
        for (int i = 0; i < spectrum.Length; i++) sum += spectrum[i];

        Assert.True(sum > 0.2f, $"Loopback audio at 1000 Hz should yield non-zero spectrum; got sum={sum:F4}");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_FrequencyOrdering_LowVsHighBands()
    {
        // Проверяем честную частотную привязку:
        // Низкие частоты (80 Гц) должны концентрироваться в начальных полосах,
        // а высокие частоты (4000 Гц) — в более старших полосах.
        var analyzerBass = new AudioSpectrumAnalyzer();
        byte[] bassSignal = GenerateSinePcm16(80.0, 44100, 4096, amplitude: 28000);
        analyzerBass.ProcessPcm16(bassSignal, bassSignal.Length);
        float[] spectrumBass = new float[AudioSpectrumAnalyzer.RayCount];
        analyzerBass.GetTargetSpectrum(spectrumBass, 0.016);

        var analyzerTreble = new AudioSpectrumAnalyzer();
        byte[] trebleSignal = GenerateSinePcm16(4000.0, 44100, 4096, amplitude: 28000);
        analyzerTreble.ProcessPcm16(trebleSignal, trebleSignal.Length);
        float[] spectrumTreble = new float[AudioSpectrumAnalyzer.RayCount];
        analyzerTreble.GetTargetSpectrum(spectrumTreble, 0.016);

        int halfRays = AudioSpectrumAnalyzer.RayCount / 2;

        // Ищем индекс максимальной полосы в первой полусфере
        int maxBassIndex = 0;
        float maxBassVal = -1f;
        for (int r = 0; r < halfRays; r++)
        {
            if (spectrumBass[r] > maxBassVal)
            {
                maxBassVal = spectrumBass[r];
                maxBassIndex = r;
            }
        }

        int maxTrebleIndex = 0;
        float maxTrebleVal = -1f;
        for (int r = 0; r < halfRays; r++)
        {
            if (spectrumTreble[r] > maxTrebleVal)
            {
                maxTrebleVal = spectrumTreble[r];
                maxTrebleIndex = r;
            }
        }

        Assert.True(maxBassIndex < maxTrebleIndex,
            $"Bass peak index ({maxBassIndex}) must be lower than Treble peak index ({maxTrebleIndex})");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_SubBassEnergy_DistinguishesBassFromTreble()
    {
        var analyzer = new AudioSpectrumAnalyzer();

        // 60 Гц — глубокий суб-бас
        byte[] subBass = GenerateSinePcm16(60.0, 44100, 2048, amplitude: 28000);
        analyzer.ProcessPcm16(subBass, subBass.Length);
        float subBassEnergy = analyzer.SubBassEnergy;

        Assert.True(subBassEnergy > 0.1f, $"SubBassEnergy should be significant for 60 Hz tone, got {subBassEnergy:F4}");

        // Тишина или ВЧ
        var analyzerHigh = new AudioSpectrumAnalyzer();
        byte[] highTone = GenerateSinePcm16(5000.0, 44100, 2048, amplitude: 28000);
        analyzerHigh.ProcessPcm16(highTone, highTone.Length);
        float highSubBassEnergy = analyzerHigh.SubBassEnergy;

        Assert.True(highSubBassEnergy < subBassEnergy * 0.5f,
            $"SubBassEnergy for 5000 Hz ({highSubBassEnergy:F4}) must be much lower than for 60 Hz ({subBassEnergy:F4})");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_SpectralSaturation_ReactsToAudio()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        byte[] signal = GenerateSinePcm16(440.0, 44100, 2048, amplitude: 25000);
        analyzer.ProcessPcm16(signal, signal.Length);

        float saturation = analyzer.SpectralSaturation;
        Assert.True(saturation > 0.05f, $"SpectralSaturation should be > 0.05 on audio; got {saturation:F4}");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_PeakFalloff_DecaysAtSpecifiedRate()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        byte[] burst = GenerateSinePcm16(500.0, 44100, 2048, amplitude: 30000);
        analyzer.ProcessPcm16(burst, burst.Length);

        float[] spectrumFrame1 = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrumFrame1, deltaTime: 0.016);

        float max1 = 0f;
        for (int i = 0; i < spectrumFrame1.Length; i++) max1 = Math.Max(max1, spectrumFrame1[i]);
        Assert.True(max1 > 0f, "Frame 1 must have audio peak");

        // Следующий кадр без подачи нового аудио: должен плавно спадать со скоростью ~0.85-0.9 за кадр
        float[] spectrumFrame2 = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrumFrame2, deltaTime: 0.016);

        float max2 = 0f;
        for (int i = 0; i < spectrumFrame2.Length; i++) max2 = Math.Max(max2, spectrumFrame2[i]);

        Assert.True(max2 < max1, $"Frame 2 peak ({max2:F4}) must be lower than Frame 1 ({max1:F4})");
        Assert.True(max2 > 0f, "Frame 2 peak must not instantly drop to 0 (decay falloff)");
    }

    [Fact]
    public void ArcReactorPalettes_SpectrumGradient_TransitionsCyanToUltramarine()
    {
        // При нулевой насыщенности цвет должен быть неоновым цианом (#00E5FF)
        var idleZero = ArcReactorPalettes.GetPalette(JarvisState.Idle, saturation: 0.0f);
        Assert.Equal(MediaColor.FromRgb(0x00, 0xE5, 0xFF), idleZero.Color);

        // При полной насыщенности спектра цвет плавно перетекает в глубокий ультрамарин/электрик
        var idleMax = ArcReactorPalettes.GetPalette(JarvisState.Idle, saturation: 1.0f);
        Assert.Equal(0, idleMax.Color.R);
        Assert.True(idleMax.Color.G < 100, $"Ultramarine green channel should be deep (< 100); got {idleMax.Color.G}");
        Assert.Equal(255, idleMax.Color.B);

        // Промежуточная насыщенность должна быть промежуточным цветом
        var idleMid = ArcReactorPalettes.GetPalette(JarvisState.Idle, saturation: 0.5f);
        Assert.True(idleMid.Color.G < idleZero.Color.G && idleMid.Color.G > idleMax.Color.G,
            $"Mid color G channel ({idleMid.Color.G}) must be between {idleZero.Color.G} and {idleMax.Color.G}");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_BilateralSymmetry_Maintained()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        byte[] signal = GenerateSinePcm16(300.0, 44100, 2048, amplitude: 25000);
        analyzer.ProcessPcm16(signal, signal.Length);

        float[] spectrum = new float[AudioSpectrumAnalyzer.RayCount];
        analyzer.GetTargetSpectrum(spectrum, deltaTime: 0.016);

        int halfRays = AudioSpectrumAnalyzer.RayCount / 2;
        for (int r = 0; r < halfRays; r++)
        {
            Assert.Equal(spectrum[r], spectrum[AudioSpectrumAnalyzer.RayCount - 1 - r], precision: 4);
        }
    }
}
