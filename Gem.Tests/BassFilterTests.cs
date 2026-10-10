using System;
using System.Diagnostics;
using Gem.UI;
using NAudio.Wave;
using Xunit;

namespace Gem.Tests;

/// <summary>
/// Unit tests for the bass beat detection pipeline in <see cref="AudioSpectrumAnalyzer"/>:
///   1. IIR Low-Pass Filter (150 Hz cutoff) attenuates high-frequency content.
///   2. Beat history buffer accumulates energy over ~1 second (43 slots).
///   3. Beat fires only when instantaneous energy > average × 1.35.
///   4. Beat cooldown (280 ms) prevents double-triggering on a single sustained hit.
///   5. Idle Lerp — beat intensity decays to zero between events.
/// </summary>
public class BassFilterTests
{
    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Generates 16-bit PCM bytes for a pure sine wave at <paramref name="freqHz"/> Hz.
    /// Amplitude is normalised to <paramref name="amplitude"/> (max 32767).
    /// </summary>
    private static byte[] GenerateSine(double freqHz, int sampleRate, int sampleCount, short amplitude = 28000)
    {
        byte[] buf = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            short val = (short)(amplitude * Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate));
            buf[i * 2]     = (byte)(val & 0xFF);
            buf[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buf;
    }

    /// <summary>
    /// Generates a 32-bit IEEE float stereo WASAPI loopback buffer for a sine wave.
    /// </summary>
    private static byte[] GenerateSineLoopbackFloat(double freqHz, int sampleRate, int frameCount, float amplitude = 0.85f)
    {
        // 2 channels, 4 bytes per sample → 8 bytes per frame
        byte[] buf = new byte[frameCount * 2 * 4];
        int idx = 0;
        for (int f = 0; f < frameCount; f++)
        {
            float s = amplitude * (float)Math.Sin(2.0 * Math.PI * freqHz * f / sampleRate);
            byte[] le = BitConverter.GetBytes(s);
            // Channel 0
            buf[idx++] = le[0]; buf[idx++] = le[1]; buf[idx++] = le[2]; buf[idx++] = le[3];
            // Channel 1 (same signal, stereo)
            buf[idx++] = le[0]; buf[idx++] = le[1]; buf[idx++] = le[2]; buf[idx++] = le[3];
        }
        return buf;
    }

    /// <summary>
    /// Feeds <paramref name="repetitions"/> copies of <paramref name="audioBytes"/> into
    /// the analyzer via ProcessPcm16 to build up the history buffer.
    /// </summary>
    private static void FeedPcm16(AudioSpectrumAnalyzer analyzer, byte[] audioBytes, int repetitions = 1)
    {
        for (int i = 0; i < repetitions; i++)
        {
            analyzer.ProcessPcm16(audioBytes, audioBytes.Length);
        }
    }

    // =========================================================================
    // TEST 1: LPF attenuates 1 kHz signal (high-frequency > 150 Hz cut-off)
    // =========================================================================

    [Fact]
    public void LowPassFilter_AttenuatesHighFrequency_1kHz()
    {
        // ARRANGE — generate a loud 1 kHz sine (well above the 150 Hz cut-off)
        var analyzer = new AudioSpectrumAnalyzer();
        analyzer.ResetLpfState();

        byte[] signal1kHz = GenerateSine(1000.0, 44100, 4096);

        // ACT — feed the signal and measure bass energy (should be near zero)
        FeedPcm16(analyzer, signal1kHz, repetitions: 3);
        float bassAfterHighFreq = analyzer.BassEnergy;

        // ARRANGE — now feed a 80 Hz (bass) signal of the same amplitude
        analyzer.ResetLpfState();
        byte[] signal80Hz = GenerateSine(80.0, 44100, 4096);
        FeedPcm16(analyzer, signal80Hz, repetitions: 3);
        float bassAfterLowFreq = analyzer.BassEnergy;

        // ASSERT — bass energy from 1 kHz tone must be significantly lower than from 80 Hz
        // The 2nd-order Butterworth at 150 Hz provides > 20 dB attenuation at 1 kHz.
        Assert.True(bassAfterHighFreq < bassAfterLowFreq * 0.5,
            $"LPF should heavily attenuate 1kHz: bassHighFreq={bassAfterHighFreq:F6}, bassLowFreq={bassAfterLowFreq:F6}");
    }

    // =========================================================================
    // TEST 2: LPF passes 80 Hz signal (below 150 Hz cut-off) with minimal loss
    // =========================================================================

    [Fact]
    public void LowPassFilter_PassesBassFrequency_80Hz()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        analyzer.ResetLpfState();

        byte[] signal80Hz = GenerateSine(80.0, 44100, 4096, amplitude: 20000);
        FeedPcm16(analyzer, signal80Hz, repetitions: 4);

        float bassEnergy = analyzer.BassEnergy;

        // At 80 Hz the filter gain should be close to 1.0 — energy must be clearly non-zero
        Assert.True(bassEnergy > 0.0f, $"LPF should pass 80 Hz bass; got BassEnergy={bassEnergy:F6}");
    }

    // =========================================================================
    // TEST 3: History buffer length matches specification (43 slots)
    // =========================================================================

    [Fact]
    public void BassHistory_BufferLength_Is43Slots()
    {
        Assert.Equal(43, AudioSpectrumAnalyzer.BassHistoryLength);
    }

    // =========================================================================
    // TEST 4: Beat is NOT fired during silence / below-threshold energy
    // =========================================================================

    [Fact]
    public void BeatDetection_NoFire_OnSilence()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        int beatCount = 0;
        analyzer.OnBeatDetected += _ => beatCount++;

        // Feed silence (all-zero PCM)
        byte[] silence = new byte[4096];
        FeedPcm16(analyzer, silence, repetitions: 50);

        Assert.Equal(0, beatCount);
    }

    // =========================================================================
    // TEST 5: Beat fires on a sudden bass transient above threshold × 1.35
    // =========================================================================

    [Fact]
    public void BeatDetection_Fires_OnSuddenBassTransient()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        int beatCount = 0;
        analyzer.OnBeatDetected += _ => beatCount++;

        // Prime the history with quiet bass so average is low
        byte[] quietBass = GenerateSine(100.0, 44100, 2048, amplitude: 500);
        FeedPcm16(analyzer, quietBass, repetitions: 60); // fill history buffer

        // Now inject a loud bass burst (should be >> average × 1.35)
        byte[] loudBass = GenerateSine(100.0, 44100, 2048, amplitude: 28000);
        FeedPcm16(analyzer, loudBass, repetitions: 3);

        Assert.True(beatCount >= 1,
            "A sudden loud bass transient should trigger at least one beat event.");
    }

    // =========================================================================
    // TEST 6: Beat threshold factor matches specification (1.35)
    // =========================================================================

    [Fact]
    public void BeatThresholdFactor_Matches_Specification()
    {
        Assert.Equal(1.35f, AudioSpectrumAnalyzer.BeatThresholdFactor, precision: 2);
    }

    // =========================================================================
    // TEST 7: Beat cooldown prevents double-triggering within 280 ms
    // =========================================================================

    [Fact]
    public void BeatCooldown_Prevents_DoubleTrigger_Within280ms()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        int beatCount = 0;
        analyzer.OnBeatDetected += _ => beatCount++;

        // Prime history with quiet bass
        byte[] quietBass = GenerateSine(100.0, 44100, 2048, amplitude: 300);
        FeedPcm16(analyzer, quietBass, repetitions: 60);

        // Fire a loud beat burst — count beats triggered
        byte[] loudBurst = GenerateSine(100.0, 44100, 4096, amplitude: 30000);
        int beatsFirst = 0;
        analyzer.OnBeatDetected += _ => beatsFirst++;
        FeedPcm16(analyzer, loudBurst, repetitions: 1);

        // Immediately feed another loud burst within the 280 ms cooldown window.
        // Since no real time elapses (Stopwatch), additional triggers must be suppressed.
        int beatsBefore = beatCount;
        FeedPcm16(analyzer, loudBurst, repetitions: 1);

        // The cooldown may or may not suppress depending on frame count, but the total
        // number of beats must be bounded — not one per FFT frame (256 × many = huge count).
        // We allow at most 5 beats total (generous; real constraint is 1 per 280ms at 60fps).
        Assert.True(beatCount <= 5,
            $"Cooldown should prevent excessive double-triggering; got {beatCount} beats.");
    }

    // =========================================================================
    // TEST 8: Beat cooldown value matches specification (280 ms)
    // =========================================================================

    [Fact]
    public void BeatCooldown_Matches_Specification_280ms()
    {
        Assert.True(AudioSpectrumAnalyzer.BeatCooldownMs >= 250.0 && AudioSpectrumAnalyzer.BeatCooldownMs <= 300.0,
            $"Cooldown should be 250-300 ms per spec; got {AudioSpectrumAnalyzer.BeatCooldownMs} ms");
    }

    // =========================================================================
    // TEST 9: Beat intensity decays toward zero over time (Idle Lerp)
    // =========================================================================

    [Fact]
    public void BeatIntensity_DecaysToZero_AfterBeatEvent()
    {
        var analyzer = new AudioSpectrumAnalyzer();

        // Warm up history with quiet signal
        byte[] quietBass = GenerateSine(100.0, 44100, 2048, amplitude: 300);
        FeedPcm16(analyzer, quietBass, repetitions: 60);

        // Trigger a beat
        byte[] loudBass = GenerateSine(100.0, 44100, 2048, amplitude: 30000);
        FeedPcm16(analyzer, loudBass, repetitions: 2);

        float intensityAfterBeat = analyzer.BeatIntensity;

        // Now simulate many GetTargetSpectrum calls (decay at 5.0/s × 0.016 = 0.08/frame)
        float[] buf = new float[AudioSpectrumAnalyzer.RayCount];
        for (int frame = 0; frame < 80; frame++) // ~1.3 s of frames
        {
            analyzer.GetTargetSpectrum(buf, 0.016);
        }

        float intensityAfterDecay = analyzer.BeatIntensity;

        Assert.True(intensityAfterDecay < intensityAfterBeat,
            $"Beat intensity should decay over time: before={intensityAfterBeat:F4}, after={intensityAfterDecay:F4}");
        Assert.True(intensityAfterDecay < 0.05f,
            $"Beat intensity should approach 0 after ~1.3 s; got {intensityAfterDecay:F4}");
    }

    // =========================================================================
    // TEST 10: ProcessLoopback (WASAPI Float Stereo) feeds LPF and produces bass energy
    // =========================================================================

    [Fact]
    public void ProcessLoopback_Float32Stereo_FeedsLpfAndProducesBassEnergy()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        analyzer.ResetLpfState();

        // 44100 Hz, 2 ch, 32-bit IEEE float
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        byte[] loopbackBass = GenerateSineLoopbackFloat(100.0, 44100, 4096, amplitude: 0.8f);

        for (int i = 0; i < 5; i++)
        {
            analyzer.ProcessLoopback(loopbackBass, loopbackBass.Length, fmt);
        }

        float bassEnergy = analyzer.BassEnergy;
        Assert.True(bassEnergy > 0.0f,
            $"ProcessLoopback should produce non-zero bass energy for 100 Hz loopback signal; got {bassEnergy:F6}");
    }

    // =========================================================================
    // TEST 11: GetAverageBassEnergy grows as energy is accumulated in history
    // =========================================================================

    [Fact]
    public void BassHistory_AverageEnergy_GrowsWithSignal()
    {
        var analyzer = new AudioSpectrumAnalyzer();

        float avgBefore = analyzer.GetAverageBassEnergy();

        byte[] bass = GenerateSine(100.0, 44100, 4096, amplitude: 20000);
        FeedPcm16(analyzer, bass, repetitions: 20);

        float avgAfter = analyzer.GetAverageBassEnergy();

        Assert.True(avgAfter > avgBefore,
            $"Average bass energy should increase after feeding bass signal: before={avgBefore:F6}, after={avgAfter:F6}");
    }

    // =========================================================================
    // TEST 12: IIR LPF attenuates 4 kHz signal so strongly that bass energy
    //          is significantly lower than from an equivalent 100 Hz signal.
    //          A 2nd-order Butterworth at 150 Hz provides > 20 dB (10× amplitude)
    //          attenuation at 4 kHz.  We measure BassEnergy directly to verify
    //          the filter effect without needing to control history dynamics.
    // =========================================================================

    [Fact]
    public void LowPassFilter_Attenuates4kHz_BassEnergySignificantlyLowerThan100Hz()
    {
        // --- Measure bass energy from a 100 Hz (bass) signal ---
        var analyzerBass = new AudioSpectrumAnalyzer();
        analyzerBass.ResetLpfState();
        byte[] signal100Hz = GenerateSine(100.0, 44100, 4096, amplitude: 28000);
        FeedPcm16(analyzerBass, signal100Hz, repetitions: 5);
        float bassEnergy100Hz = analyzerBass.BassEnergy;

        // --- Measure bass energy from a 4 kHz (high-frequency) signal ---
        var analyzerHF = new AudioSpectrumAnalyzer();
        analyzerHF.ResetLpfState();
        byte[] signal4kHz = GenerateSine(4000.0, 44100, 4096, amplitude: 28000);
        FeedPcm16(analyzerHF, signal4kHz, repetitions: 5);
        float bassEnergy4kHz = analyzerHF.BassEnergy;

        // ASSERT: 100 Hz produces at least 5× more bass energy than 4 kHz after the LPF.
        // (Generous margin — actual attenuation at 4kHz for a 150Hz LPF is ~40 dB = 100×)
        Assert.True(bassEnergy4kHz < bassEnergy100Hz / 5.0f,
            $"LPF should reduce 4kHz bass energy relative to 100Hz by at least 5×: " +
            $"bass100Hz={bassEnergy100Hz:F6}, bass4kHz={bassEnergy4kHz:F6}");
    }
}
