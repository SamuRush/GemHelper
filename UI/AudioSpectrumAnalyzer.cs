using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Gem.UI;

/// <summary>
/// High-performance audio spectrum analyzer for the JARVIS Arc Reactor holographic visualizer.
/// Coordinates the 2nd-order Butterworth bass filter, transient beat detector, and full-bandwidth FFT processor.
/// Preserves zero runtime heap allocations in high-frequency audio capture and 60 FPS render loops.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = FftProcessor.FftSize;
    public const int FftOrder = FftProcessor.FftOrder;
    public const int RayCount = FftProcessor.RayCount;

    public const int BassHistoryLength = BeatDetector.BassHistoryLength;
    public const double BeatCooldownMs = BeatDetector.BeatCooldownMs;
    public const float BeatThresholdFactor = BeatDetector.BeatThresholdFactor;

    private readonly ButterworthBassFilter _bassFilter = new();
    private readonly BeatDetector _beatDetector = new();
    private readonly FftProcessor _fftProcessor = new();

    private readonly float[] _sampleBuffer = new float[FftSize];
    private readonly float[] _lpfSampleBuffer = new float[FftSize];
    private int _sampleIndex = 0;

    private readonly float[] _rawSpectrum = new float[RayCount];
    private readonly float[] _decaySpectrum = new float[RayCount];
    private readonly object _lock = new();

    private bool _isTtsSpeaking = false;
    private double _syntheticPhase = 0;
    private float _currentRms = 0;

    private float _currentBassEnergy = 0f;
    private float _currentSubBassEnergy = 0f;
    private float _currentSpectralSaturation = 0f;

    public float BeatIntensity
    {
        get { lock (_lock) return _beatDetector.BeatIntensity; }
    }

    public float BassEnergy
    {
        get { lock (_lock) return _currentBassEnergy; }
    }

    public float SubBassEnergy
    {
        get { lock (_lock) return _currentSubBassEnergy; }
    }

    public float SpectralSaturation
    {
        get { lock (_lock) return _currentSpectralSaturation; }
    }

    public double Gain { get; set; } = 3.5;
    public double Falloff { get; set; } = 0.88;

    public event Action<float>? OnBeatDetected
    {
        add { lock (_lock) _beatDetector.OnBeatDetected += value; }
        remove { lock (_lock) _beatDetector.OnBeatDetected -= value; }
    }

    public void SetTtsSpeaking(bool speaking)
    {
        lock (_lock) _isTtsSpeaking = speaking;
    }

    public void UpdateRms(float rms)
    {
        lock (_lock) _currentRms = rms;
    }

    public void ProcessPcm16(byte[] buffer, int bytesRecorded)
    {
        if (buffer == null || bytesRecorded < 2) return;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
        lock (_lock)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float raw = samples[i] / 32768.0f;
                float filtered = _bassFilter.Apply(raw);

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
                    float filtered = _bassFilter.Apply(mono);

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
                    float filtered = _bassFilter.Apply(mono);

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

    private void ExecuteFft()
    {
        _fftProcessor.ComputeLpfFft(_lpfSampleBuffer, out _currentBassEnergy, out _currentSubBassEnergy);
        _beatDetector.ProcessBassEnergy(_currentBassEnergy);
        _currentSpectralSaturation = _fftProcessor.ComputeFullSpectrum(_sampleBuffer, Gain, _rawSpectrum);
    }

    public void GetTargetSpectrum(Span<float> destination, double deltaTime)
    {
        if (destination.Length < RayCount) return;

        lock (_lock)
        {
            _beatDetector.Decay(deltaTime);

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

            float decayFactor = MathF.Pow((float)Falloff, (float)(deltaTime * 60.0));
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

    internal void ResetLpfState()
    {
        lock (_lock) _bassFilter.Reset();
    }

    internal float GetAverageBassEnergy()
    {
        lock (_lock) return _beatDetector.GetAverageBassEnergy();
    }
}
