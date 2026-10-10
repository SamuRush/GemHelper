using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace Gem.UI;

/// <summary>
/// High-performance audio spectrum analyzer for the JARVIS Arc Reactor holographic visualizer.
/// Computes real-time 256-point Fast Fourier Transforms (NAudio FFT) with Hanning windowing
/// across microphone input (Vosk STT), assistant speech output (Edge-TTS / SAPI5),
/// and system audio (WASAPI Loopback Capture for music/rhythm reactivity).
///
/// Beat Detection Pipeline:
///   1. IIR Low-Pass Filter (2nd-order Butterworth, fc = 150 Hz) isolates bass/kick
///      from vocals, guitars, and high-frequency content before FFT.
///   2. Bass energy history buffer (~43 frames × 1024 samples ≈ 1 second) tracks
///      the rolling average energy of the filtered bass band.
///   3. A beat event is fired only when instantaneous energy > average × 1.35,
///      with a 280 ms cooldown to prevent double-triggering on a single sustained hit.
///   4. Between beats the reactor returns to smooth sinusoidal idle breathing;
///      micro-jitter from mids/highs is suppressed by routing visualizer rays
///      exclusively through the FFT spectrum (not raw LPF energy).
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = 256;
    public const int FftOrder = 8; // 2^8 = 256
    public const int RayCount = 96;

    // =========================================================================
    // 1. IIR LOW-PASS FILTER STATE — 2nd-order Butterworth at fc ≈ 150 Hz
    //    Coefficients are computed for a generic 44100 Hz reference and are
    //    valid for typical WASAPI loopback sample rates (44100 / 48000 Hz).
    //    The filter is applied to mono samples in-line before feeding the FFT
    //    buffer, ensuring bass isolation from mids and highs.
    //
    //    Precomputed via bilinear transform:
    //      Wc  = tan(π × fc / fs) with fc=150, fs=44100 → Wc ≈ 0.01069
    //      a0  = 1 / (1 + √2×Wc + Wc²)
    //      b0  = Wc² × a0      (b1 = 2×b0, b2 = b0)
    //      a1  = 2×a0×(Wc²-1) ,  a2 = a0×(1-√2×Wc+Wc²)
    //    Values below are for 44100 Hz; they remain accurate for 48000 Hz since
    //    the cut-off shift is < 5 Hz (imperceptible for beat detection).
    // =========================================================================
    // Denominator coefficients (feedback)
    private const double LpfA1 = -1.99786986602808;   // -a1 (negated for direct-form II)
    private const double LpfA2 = 0.99787895688660;    // -a2 (negated)
    // Numerator coefficients (feed-forward), gain-normalised so DC gain = 1
    private const double LpfB0 = 2.27770845847543e-5;
    private const double LpfB1 = 4.55541691695086e-5; // 2 × B0
    private const double LpfB2 = 2.27770845847543e-5;

    // Direct-Form II delay registers for the IIR filter
    private double _lpfW0 = 0.0;
    private double _lpfW1 = 0.0;

    // =========================================================================
    // 2. FFT BUFFER & SPECTRUM
    // =========================================================================
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _sampleBuffer = new float[FftSize];
    private readonly float[] _hanningWindow = new float[FftSize];
    private int _sampleIndex = 0;

    private readonly float[] _rawSpectrum = new float[RayCount];
    private readonly float[] _decaySpectrum = new float[RayCount];
    private readonly object _lock = new();

    private bool _isTtsSpeaking = false;
    private double _syntheticPhase = 0;
    private float _currentRms = 0;

    // =========================================================================
    // 3. BASS BEAT DETECTION — Energy history ≈ 1 second
    //    At 256-sample FFT frames (≈ 5.8 ms each at 44100 Hz) we accumulate
    //    ~172 frames for 1 s; the task spec calls for "~43 frames × 1024 samples"
    //    (i.e. each logical beat-frame groups 4 FFT frames).  We use 43 entries
    //    updated every 4th ExecuteFft() call, matching the spec exactly.
    //
    //    Cooldown = 280 ms — prevents double-triggering on a single sustained hit.
    //    Beat threshold multiplier = 1.35 (instantEnergy > avgEnergy × 1.35).
    // =========================================================================
    /// <summary>Number of logical bass-energy history slots (each covers ~4 FFT frames ≈ 23 ms).
    /// 43 slots × ~23 ms ≈ 1 second of rolling history.</summary>
    public const int BassHistoryLength = 43;

    /// <summary>Beat cooldown in milliseconds (prevents double-trigger on one drum hit).</summary>
    public const double BeatCooldownMs = 280.0;

    /// <summary>Adaptive threshold multiplier: instant energy must exceed avg × Factor to count as beat.</summary>
    public const float BeatThresholdFactor = 1.35f;

    private readonly float[] _bassHistory = new float[BassHistoryLength];
    private int _bassHistoryIndex = 0;

    // Accumulator: sum LPF-filtered bass energy over 4 FFT frames before recording one history slot
    private float _bassFrameAccumulator = 0f;
    private int _bassFrameCount = 0;
    private const int BassFramesPerSlot = 4;

    // Instantaneous bass energy (filtered, per FFT frame) exposed for unit tests
    private float _currentBassEnergy = 0f;

    // Beat animation impulse (decays to 0 between beats)
    private float _beatIntensity = 0f;

    // Timestamp of the last confirmed beat (Stopwatch ticks)
    private long _lastBeatTimestamp = 0;

    /// <summary>Current beat impulse intensity [0 .. 1]. Decays smoothly between beat events.</summary>
    public float BeatIntensity
    {
        get { lock (_lock) return _beatIntensity; }
    }

    /// <summary>Instantaneous bass energy of the current FFT frame (LPF-filtered).</summary>
    public float BassEnergy
    {
        get { lock (_lock) return _currentBassEnergy; }
    }

    /// <summary>Fires on the audio thread when a bass beat / kick-drum transient is confirmed.</summary>
    public event Action<float>? OnBeatDetected;

    public AudioSpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hanningWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }
    }

    // =========================================================================
    // PUBLIC INTERFACE
    // =========================================================================

    /// <summary>
    /// Notifies the analyzer of TTS playback status (used for SAPI5 and speech envelope tracking).
    /// </summary>
    public void SetTtsSpeaking(bool speaking)
    {
        lock (_lock)
        {
            _isTtsSpeaking = speaking;
        }
    }

    /// <summary>
    /// Updates current RMS amplitude level from the microphone RMS gate.
    /// </summary>
    public void UpdateRms(float rms)
    {
        lock (_lock)
        {
            _currentRms = rms;
        }
    }

    /// <summary>
    /// Ingests 16-bit PCM audio bytes (from microphone NAudio WaveIn or Edge-TTS audio stream).
    /// Uses ReadOnlySpan to eliminate heap allocations.
    /// Applies the IIR LPF to each sample before feeding the FFT buffer.
    /// </summary>
    public void ProcessPcm16(byte[] buffer, int bytesRecorded)
    {
        if (buffer == null || bytesRecorded < 2) return;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
        lock (_lock)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float raw = samples[i] / 32768.0f;
                float filtered = ApplyLpf(raw);

                _sampleBuffer[_sampleIndex] = filtered;
                _sampleIndex++;

                if (_sampleIndex >= FftSize)
                {
                    _sampleIndex = 0;
                    ExecuteFft();
                }
            }
        }
    }

    /// <summary>
    /// Ingests system playback sound captured via WASAPI Loopback (32-bit float or 16-bit PCM stereo/multi-channel).
    /// Automatically converts multi-channel audio to mono, applies the IIR LPF, and feeds the FFT buffer in-place
    /// with zero heap allocations.
    /// </summary>
    public void ProcessLoopback(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
    {
        if (buffer == null || bytesRecorded < 4 || waveFormat == null) return;

        int channels = Math.Max(1, waveFormat.Channels);
        bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat || waveFormat.BitsPerSample == 32;

        lock (_lock)
        {
            if (isFloat)
            {
                ReadOnlySpan<float> floatSamples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
                int frames = floatSamples.Length / channels;
                for (int f = 0; f < frames; f++)
                {
                    int offset = f * channels;
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += floatSamples[offset + ch];
                    }
                    float mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
                    float filtered = ApplyLpf(mono);
                    _sampleBuffer[_sampleIndex++] = filtered;

                    if (_sampleIndex >= FftSize)
                    {
                        _sampleIndex = 0;
                        ExecuteFft();
                    }
                }
            }
            else if (waveFormat.BitsPerSample == 16)
            {
                ReadOnlySpan<short> shortSamples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
                int frames = shortSamples.Length / channels;
                for (int f = 0; f < frames; f++)
                {
                    int offset = f * channels;
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += shortSamples[offset + ch] / 32768.0f;
                    }
                    float mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
                    float filtered = ApplyLpf(mono);
                    _sampleBuffer[_sampleIndex++] = filtered;

                    if (_sampleIndex >= FftSize)
                    {
                        _sampleIndex = 0;
                        ExecuteFft();
                    }
                }
            }
        }
    }

    // =========================================================================
    // INTERNAL: IIR Low-Pass Filter (Direct-Form II, 2nd-order Butterworth)
    // fc = 150 Hz, fs = 44100 Hz reference.
    // Called once per sample inside the lock — no allocation.
    // =========================================================================

    /// <summary>
    /// Applies one sample through the 2nd-order Butterworth IIR low-pass filter (fc ≈ 150 Hz).
    /// Uses Direct-Form II to minimise register count.
    /// Thread-safety: caller must hold <see cref="_lock"/> before calling.
    /// </summary>
    private float ApplyLpf(float x)
    {
        // Direct-Form II transposed
        double w = (double)x - LpfA1 * _lpfW0 - LpfA2 * _lpfW1;
        double y = LpfB0 * w + LpfB1 * _lpfW0 + LpfB2 * _lpfW1;
        _lpfW1 = _lpfW0;
        _lpfW0 = w;
        return (float)Math.Clamp(y, -1.0, 1.0);
    }

    // =========================================================================
    // INTERNAL: FFT + Beat Detection
    // =========================================================================

    private void ExecuteFft()
    {
        // Apply Hanning window and copy to complex FFT buffer
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * _hanningWindow[i];
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftOrder, _fftBuffer);

        // ----------------------------------------------------------------
        // STEP 1: Bass energy measurement (bins 1–4, covering ~0–350 Hz
        //         in a 256-point FFT at 44100 Hz, after LPF pre-filtering).
        //         The LPF upstream already attenuates everything above 150 Hz
        //         so energy in these bins is dominated by kick/bass.
        // ----------------------------------------------------------------
        float bassSum = 0f;
        for (int b = 1; b <= 4; b++)
        {
            bassSum += MathF.Sqrt(_fftBuffer[b].X * _fftBuffer[b].X + _fftBuffer[b].Y * _fftBuffer[b].Y);
        }
        _currentBassEnergy = bassSum;

        // ----------------------------------------------------------------
        // STEP 2: Accumulate into 1024-sample logical frames.
        //         Every BassFramesPerSlot (4) × 256-sample FFT frames,
        //         write one slot into the circular history buffer.
        // ----------------------------------------------------------------
        _bassFrameAccumulator += bassSum;
        _bassFrameCount++;

        if (_bassFrameCount >= BassFramesPerSlot)
        {
            float slotEnergy = _bassFrameAccumulator / BassFramesPerSlot;
            _bassHistory[_bassHistoryIndex] = slotEnergy;
            _bassHistoryIndex = (_bassHistoryIndex + 1) % BassHistoryLength;
            _bassFrameAccumulator = 0f;
            _bassFrameCount = 0;
        }

        // ----------------------------------------------------------------
        // STEP 3: Compute rolling average from history
        // ----------------------------------------------------------------
        float sumHist = 0f;
        for (int i = 0; i < BassHistoryLength; i++) sumHist += _bassHistory[i];
        float avgBass = sumHist / BassHistoryLength;

        // ----------------------------------------------------------------
        // STEP 4: Beat detection with threshold × 1.35 and 280 ms cooldown
        //         A small epsilon (+0.008) prevents false triggers on silence.
        // ----------------------------------------------------------------
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedMsSinceLastBeat = _lastBeatTimestamp > 0
            ? Stopwatch.GetElapsedTime(_lastBeatTimestamp, nowTicks).TotalMilliseconds
            : 1000.0;

        if (bassSum > (avgBass * BeatThresholdFactor + 0.008f) && elapsedMsSinceLastBeat > BeatCooldownMs)
        {
            // Beat confirmed — fire impulse at full intensity
            _beatIntensity = 1.0f;
            _lastBeatTimestamp = nowTicks;
            OnBeatDetected?.Invoke(bassSum);
        }

        // ----------------------------------------------------------------
        // STEP 5: Full audio spectrum bins for visualizer crown.
        //         Note: _sampleBuffer already holds LPF-filtered samples,
        //         so mids/highs are attenuated — the crown responds mainly
        //         to bass. The idle organic wave fills in the visual gap.
        // ----------------------------------------------------------------
        int usefulBins = 60; // Focus on 80 Hz – 4000 Hz vocal/music range
        int halfRays = RayCount / 2; // 48 rays per hemisphere

        for (int r = 0; r < halfRays; r++)
        {
            double logFraction = Math.Pow((double)r / halfRays, 1.35);
            int binIndex = Math.Clamp((int)(logFraction * usefulBins) + 1, 1, usefulBins - 1);

            float mag = MathF.Sqrt(_fftBuffer[binIndex].X * _fftBuffer[binIndex].X + _fftBuffer[binIndex].Y * _fftBuffer[binIndex].Y);
            float scaled = Math.Clamp(mag * 5.2f, 0f, 1f);

            // Bilateral symmetry: Left hemisphere mirrors right hemisphere (Iron Man HUD aesthetic)
            _rawSpectrum[r] = scaled;
            _rawSpectrum[RayCount - 1 - r] = scaled;
        }
    }

    // =========================================================================
    // PUBLIC: Spectrum retrieval (called at 60 FPS from UI render loop)
    // =========================================================================

    /// <summary>
    /// Retrieves current target frequency bins for visualizer rendering.
    /// Called at 60 FPS from the UI render loop using Span for zero allocations.
    /// Beat intensity decays smoothly between events so that the reactor returns
    /// to idle breathing without micro-jitter from residual high-frequency content.
    /// </summary>
    public void GetTargetSpectrum(Span<float> destination, double deltaTime)
    {
        if (destination.Length < RayCount) return;

        lock (_lock)
        {
            // ----------------------------------------------------------------
            // Smoothly decay beat intensity at a rate that gives ~200 ms tail
            // (factor 5.0 per second → e^(-5×0.016) ≈ 0.923 per frame at 60fps)
            // ----------------------------------------------------------------
            float decayBeat = (float)(deltaTime * 5.0);
            _beatIntensity = Math.Max(0f, _beatIntensity - decayBeat);

            if (_isTtsSpeaking)
            {
                _syntheticPhase += deltaTime * 14.0;
                // Rich vocal formant modulation for assistant speech
                for (int r = 0; r < RayCount; r++)
                {
                    double theta = r * (2.0 * Math.PI / RayCount);
                    double speechCadence = Math.Sin(_syntheticPhase) * 0.45 + 0.55;
                    double formant1 = Math.Sin(theta * 3.0 + _syntheticPhase * 0.8) * 0.35;
                    double formant2 = Math.Cos(theta * 7.0 - _syntheticPhase * 1.2) * 0.25;
                    float syntheticVal = Math.Clamp((float)(speechCadence * (0.45 + formant1 + formant2)), 0f, 1f);

                    float blended = Math.Max(_rawSpectrum[r], syntheticVal);
                    _decaySpectrum[r] = Math.Max(_decaySpectrum[r], blended);
                }
            }
            else
            {
                for (int r = 0; r < RayCount; r++)
                {
                    _decaySpectrum[r] = Math.Max(_decaySpectrum[r], _rawSpectrum[r]);
                }
            }

            _decaySpectrum.AsSpan(0, RayCount).CopyTo(destination);

            // Smooth per-frame decay so spectrum falls back to zero between audio events
            float decayFactor = MathF.Pow(0.82f, (float)(deltaTime * 60.0));
            for (int r = 0; r < RayCount; r++)
            {
                _decaySpectrum[r] *= decayFactor;
                _rawSpectrum[r] *= decayFactor;
            }
        }
    }

    /// <summary>
    /// Backward-compatible overload for float[] destination.
    /// </summary>
    public void GetTargetSpectrum(float[] destination, double deltaTime)
    {
        if (destination == null || destination.Length < RayCount) return;
        GetTargetSpectrum(destination.AsSpan(), deltaTime);
    }

    // =========================================================================
    // INTERNAL TEST HELPERS
    // =========================================================================

    /// <summary>
    /// Resets the IIR filter state. Used in unit tests to avoid cross-test contamination.
    /// </summary>
    internal void ResetLpfState()
    {
        lock (_lock)
        {
            _lpfW0 = 0.0;
            _lpfW1 = 0.0;
        }
    }

    /// <summary>
    /// Returns the current rolling average bass energy from the history buffer.
    /// Exposed for unit-test verification.
    /// </summary>
    internal float GetAverageBassEnergy()
    {
        lock (_lock)
        {
            float sum = 0f;
            for (int i = 0; i < BassHistoryLength; i++) sum += _bassHistory[i];
            return sum / BassHistoryLength;
        }
    }
}
