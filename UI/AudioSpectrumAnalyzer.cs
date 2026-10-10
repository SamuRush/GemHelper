using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace Gem.UI;

/// <summary>
/// High-performance audio spectrum analyzer for the JARVIS Arc Reactor holographic visualizer.
/// Computes real-time Fast Fourier Transforms (NAudio FFT) with Hanning windowing
/// across system audio (WASAPI Loopback Capture for music/rhythm reactivity),
/// microphone input (Vosk STT), and assistant speech output (Edge-TTS / SAPI5).
///
/// Features:
///   1. Full-bandwidth honest FFT (64 distinct frequency bands mapped to 128 bilateral rays).
///   2. Gravitational falloff (Peak Falloff / Decay at 0.85-0.90 per frame) for fluid equalizer dynamics.
///   3. Continuous sub-bass energy tracking (20-150 Hz) for smooth core scale modulation via Lerp.
///   4. Continuous spectral saturation tracking for smooth color gradient morphing (Cyan -> Deep Ultramarine).
///   5. Preserved 2nd-order Butterworth LPF (fc = 150 Hz) and rolling bass history (~1 s, 43 slots, 280 ms cooldown)
///      for beat detection compatibility.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = 256;
    public const int FftOrder = 8; // 2^8 = 256
    public const int RayCount = 128; // 128 bilateral rays (64 frequency bands mirrored)

    // =========================================================================
    // 1. IIR LOW-PASS FILTER STATE — 2nd-order Butterworth at fc ≈ 150 Hz
    // =========================================================================
    private const double LpfA1 = -1.99786986602808;   // -a1
    private const double LpfA2 = 0.99787895688660;    // -a2
    private const double LpfB0 = 2.27770845847543e-5;
    private const double LpfB1 = 4.55541691695086e-5; // 2 × B0
    private const double LpfB2 = 2.27770845847543e-5;

    private double _lpfW0 = 0.0;
    private double _lpfW1 = 0.0;

    // =========================================================================
    // 2. FFT BUFFERS & SPECTRUM
    // =========================================================================
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly Complex[] _lpfFftBuffer = new Complex[FftSize];
    private readonly float[] _sampleBuffer = new float[FftSize];       // Raw unfiltered audio for honest FFT
    private readonly float[] _lpfSampleBuffer = new float[FftSize];    // LPF-filtered audio for bass detection
    private readonly float[] _hanningWindow = new float[FftSize];
    private int _sampleIndex = 0;

    // Band bin boundaries pre-computed in constructor
    private readonly int[] _bandStartBin = new int[RayCount / 2];
    private readonly int[] _bandEndBin = new int[RayCount / 2];

    private readonly float[] _rawSpectrum = new float[RayCount];
    private readonly float[] _decaySpectrum = new float[RayCount];
    private readonly object _lock = new();

    private bool _isTtsSpeaking = false;
    private double _syntheticPhase = 0;
    private float _currentRms = 0;

    // =========================================================================
    // 3. BASS BEAT DETECTION & SUB-BASS METRICS
    // =========================================================================
    public const int BassHistoryLength = 43;
    public const double BeatCooldownMs = 280.0;
    public const float BeatThresholdFactor = 1.35f;

    private readonly float[] _bassHistory = new float[BassHistoryLength];
    private int _bassHistoryIndex = 0;

    private float _bassFrameAccumulator = 0f;
    private int _bassFrameCount = 0;
    private const int BassFramesPerSlot = 4;

    private float _currentBassEnergy = 0f;
    private float _currentSubBassEnergy = 0f;
    private float _currentSpectralSaturation = 0f;
    private float _beatIntensity = 0f;
    private long _lastBeatTimestamp = 0;

    /// <summary>Current beat impulse intensity [0 .. 1]. Decays smoothly between beat events.</summary>
    public float BeatIntensity
    {
        get { lock (_lock) return _beatIntensity; }
    }

    /// <summary>Instantaneous bass energy of current FFT frame (LPF-filtered).</summary>
    public float BassEnergy
    {
        get { lock (_lock) return _currentBassEnergy; }
    }

    /// <summary>Instantaneous sub-bass energy (20-150 Hz, normalized [0 .. 1]).</summary>
    public float SubBassEnergy
    {
        get { lock (_lock) return _currentSubBassEnergy; }
    }

    /// <summary>Overall spectral saturation across all bands (normalized [0 .. 1]).</summary>
    public float SpectralSaturation
    {
        get { lock (_lock) return _currentSpectralSaturation; }
    }

    /// <summary>Fires on the audio thread when a bass beat / kick-drum transient is confirmed.</summary>
    public event Action<float>? OnBeatDetected;

    public AudioSpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hanningWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }

        // Precompute frequency band bin mapping for 64 bands (RayCount / 2):
        // Maps audible range ~20 Hz to ~20 kHz logarithmically across 64 bands.
        int halfRays = RayCount / 2;
        for (int r = 0; r < halfRays; r++)
        {
            double t0 = (double)r / halfRays;
            double t1 = (double)(r + 1) / halfRays;

            int b0 = 1 + (int)(Math.Pow(t0, 1.55) * 118.0);
            int b1 = 1 + (int)(Math.Pow(t1, 1.55) * 118.0);

            if (b1 < b0) b1 = b0;
            if (b0 > 120) b0 = 120;
            if (b1 > 120) b1 = 120;

            _bandStartBin[r] = b0;
            _bandEndBin[r] = b1;
        }
    }

    // =========================================================================
    // PUBLIC INTERFACE
    // =========================================================================

    public void SetTtsSpeaking(bool speaking)
    {
        lock (_lock)
        {
            _isTtsSpeaking = speaking;
        }
    }

    public void UpdateRms(float rms)
    {
        lock (_lock)
        {
            _currentRms = rms;
        }
    }

    /// <summary>
    /// Ingests 16-bit PCM audio bytes (from microphone NAudio WaveIn or TTS audio stream).
    /// Feeds both raw buffer for full-bandwidth FFT and filtered buffer for bass detection.
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

                _sampleBuffer[_sampleIndex] = raw;
                _lpfSampleBuffer[_sampleIndex] = filtered;
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
    /// Automatically converts multi-channel audio to mono and feeds both raw and LPF buffers with zero heap allocations.
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

                    _sampleBuffer[_sampleIndex] = mono;
                    _lpfSampleBuffer[_sampleIndex] = filtered;
                    _sampleIndex++;

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

                    _sampleBuffer[_sampleIndex] = mono;
                    _lpfSampleBuffer[_sampleIndex] = filtered;
                    _sampleIndex++;

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
    // INTERNAL: IIR Low-Pass Filter
    // =========================================================================

    private float ApplyLpf(float x)
    {
        double w = (double)x - LpfA1 * _lpfW0 - LpfA2 * _lpfW1;
        double y = LpfB0 * w + LpfB1 * _lpfW0 + LpfB2 * _lpfW1;
        _lpfW1 = _lpfW0;
        _lpfW0 = w;
        return (float)Math.Clamp(y, -1.0, 1.0);
    }

    // =========================================================================
    // INTERNAL: FFT & Frequency Bands Calculation
    // =========================================================================

    private void ExecuteFft()
    {
        // ----------------------------------------------------------------
        // 1. LPF FFT for backward-compatible bass measurement & beat detector
        // ----------------------------------------------------------------
        for (int i = 0; i < FftSize; i++)
        {
            _lpfFftBuffer[i].X = _lpfSampleBuffer[i] * _hanningWindow[i];
            _lpfFftBuffer[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftOrder, _lpfFftBuffer);

        float bassSum = 0f;
        for (int b = 1; b <= 4; b++)
        {
            bassSum += MathF.Sqrt(_lpfFftBuffer[b].X * _lpfFftBuffer[b].X + _lpfFftBuffer[b].Y * _lpfFftBuffer[b].Y);
        }
        _currentBassEnergy = bassSum;

        // Sub-bass (20-150 Hz) metric derived from LPF FFT bin 1
        float subBassMag = MathF.Sqrt(_lpfFftBuffer[1].X * _lpfFftBuffer[1].X + _lpfFftBuffer[1].Y * _lpfFftBuffer[1].Y);
        _currentSubBassEnergy = Math.Clamp(subBassMag * 4.5f, 0f, 1f);

        // Beat history tracking
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

        float sumHist = 0f;
        for (int i = 0; i < BassHistoryLength; i++) sumHist += _bassHistory[i];
        float avgBass = sumHist / BassHistoryLength;

        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedMsSinceLastBeat = _lastBeatTimestamp > 0
            ? Stopwatch.GetElapsedTime(_lastBeatTimestamp, nowTicks).TotalMilliseconds
            : 1000.0;

        if (bassSum > (avgBass * BeatThresholdFactor + 0.008f) && elapsedMsSinceLastBeat > BeatCooldownMs)
        {
            _beatIntensity = 1.0f;
            _lastBeatTimestamp = nowTicks;
            OnBeatDetected?.Invoke(bassSum);
        }

        // ----------------------------------------------------------------
        // 2. Full-Bandwidth Raw Audio FFT for Honest Radial Equalizer
        // ----------------------------------------------------------------
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * _hanningWindow[i];
            _fftBuffer[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftOrder, _fftBuffer);

        int halfRays = RayCount / 2; // 64 frequency bands
        float totalSpectralSum = 0f;

        for (int r = 0; r < halfRays; r++)
        {
            int bStart = _bandStartBin[r];
            int bEnd = _bandEndBin[r];

            float maxMag = 0f;
            for (int b = bStart; b <= bEnd; b++)
            {
                float mag = MathF.Sqrt(_fftBuffer[b].X * _fftBuffer[b].X + _fftBuffer[b].Y * _fftBuffer[b].Y);
                if (mag > maxMag) maxMag = mag;
            }

            // High-frequency compensation boost (equal loudness contour approximation)
            float freqBoost = 1.0f + 2.0f * ((float)r / halfRays);
            float scaled = Math.Clamp(maxMag * 5.6f * freqBoost, 0f, 1f);

            // Bilateral symmetry: Left hemisphere mirrors right hemisphere (Iron Man HUD aesthetic)
            _rawSpectrum[r] = scaled;
            _rawSpectrum[RayCount - 1 - r] = scaled;

            totalSpectralSum += scaled;
        }

        _currentSpectralSaturation = Math.Clamp(totalSpectralSum / (halfRays * 0.28f), 0f, 1f);
    }

    // =========================================================================
    // PUBLIC: Spectrum retrieval (called at 60 FPS from UI render loop)
    // =========================================================================

    /// <summary>
    /// Retrieves current target frequency bins for visualizer rendering.
    /// Called at 60 FPS from UI render loop using Span for zero allocations.
    /// Incorporates peak attack and smooth gravitational falloff (~0.88 per frame).
    /// </summary>
    public void GetTargetSpectrum(Span<float> destination, double deltaTime)
    {
        if (destination.Length < RayCount) return;

        lock (_lock)
        {
            float decayBeat = (float)(deltaTime * 5.0);
            _beatIntensity = Math.Max(0f, _beatIntensity - decayBeat);

            if (_isTtsSpeaking)
            {
                _syntheticPhase += deltaTime * 14.0;
                int halfRays = RayCount / 2;
                for (int r = 0; r < halfRays; r++)
                {
                    double theta = r * (2.0 * Math.PI / RayCount);
                    double speechCadence = Math.Sin(_syntheticPhase) * 0.45 + 0.55;
                    double formant1 = Math.Sin(theta * 3.0 + _syntheticPhase * 0.8) * 0.35;
                    double formant2 = Math.Cos(theta * 7.0 - _syntheticPhase * 1.2) * 0.25;
                    float syntheticVal = Math.Clamp((float)(speechCadence * (0.45 + formant1 + formant2)), 0f, 1f);

                    float blended = Math.Max(_rawSpectrum[r], syntheticVal);
                    _decaySpectrum[r] = Math.Max(_decaySpectrum[r], blended);
                    _decaySpectrum[RayCount - 1 - r] = _decaySpectrum[r];
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

            // Smooth gravitational decay falloff (~0.88 per frame at 60 FPS)
            float decayFactor = MathF.Pow(0.88f, (float)(deltaTime * 60.0));
            for (int r = 0; r < RayCount; r++)
            {
                _decaySpectrum[r] *= decayFactor;
                _rawSpectrum[r] *= decayFactor;
            }

            _currentSubBassEnergy *= decayFactor;
            _currentSpectralSaturation *= decayFactor;
        }
    }

    public void GetTargetSpectrum(float[] destination, double deltaTime)
    {
        if (destination == null || destination.Length < RayCount) return;
        GetTargetSpectrum(destination.AsSpan(), deltaTime);
    }

    // =========================================================================
    // INTERNAL TEST HELPERS
    // =========================================================================

    internal void ResetLpfState()
    {
        lock (_lock)
        {
            _lpfW0 = 0.0;
            _lpfW1 = 0.0;
        }
    }

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
