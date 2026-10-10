using System;
using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace Gem.UI;

/// <summary>
/// High-performance audio spectrum analyzer for the JARVIS Arc Reactor holographic visualizer.
/// Computes real-time 256-point Fast Fourier Transforms (NAudio FFT) with Hanning windowing
/// across microphone input (Vosk STT), assistant speech output (Edge-TTS / SAPI5),
/// and system audio (WASAPI Loopback Capture for music/rhythm reactivity).
/// Includes low-frequency bandpass beat detection (kick drum / bass envelope) driving HUD pulsation.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = 256;
    public const int FftOrder = 8; // 2^8 = 256
    public const int RayCount = 96;

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

    // Rhythm & Beat Detection (BPM / Bass Kick Detection)
    private const int BassHistoryLength = 32;
    private readonly float[] _bassHistory = new float[BassHistoryLength];
    private int _bassHistoryIndex = 0;
    private float _beatIntensity = 0f;
    private float _currentBassEnergy = 0f;
    private long _lastBeatTimestamp = 0;

    public float BeatIntensity
    {
        get { lock (_lock) return _beatIntensity; }
    }

    public float BassEnergy
    {
        get { lock (_lock) return _currentBassEnergy; }
    }

    public event Action<float>? OnBeatDetected;

    public AudioSpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hanningWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }
    }

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
    /// Updates current RMS amplitude level.
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
    /// </summary>
    public void ProcessPcm16(byte[] buffer, int bytesRecorded)
    {
        if (buffer == null || bytesRecorded < 2) return;

        int samples = bytesRecorded / 2;
        lock (_lock)
        {
            for (int i = 0; i < samples; i++)
            {
                short sample = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
                _sampleBuffer[_sampleIndex] = sample / 32768.0f;
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
    /// Automatically converts multi-channel audio to mono and feeds the FFT buffer.
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
                int bytesPerFrame = channels * 4;
                int frames = bytesRecorded / bytesPerFrame;
                for (int f = 0; f < frames; f++)
                {
                    int offset = f * bytesPerFrame;
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        if (offset + ch * 4 + 4 <= bytesRecorded)
                        {
                            sum += BitConverter.ToSingle(buffer, offset + ch * 4);
                        }
                    }
                    float mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
                    _sampleBuffer[_sampleIndex++] = mono;

                    if (_sampleIndex >= FftSize)
                    {
                        _sampleIndex = 0;
                        ExecuteFft();
                    }
                }
            }
            else if (waveFormat.BitsPerSample == 16)
            {
                int bytesPerFrame = channels * 2;
                int frames = bytesRecorded / bytesPerFrame;
                for (int f = 0; f < frames; f++)
                {
                    int offset = f * bytesPerFrame;
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        if (offset + ch * 2 + 2 <= bytesRecorded)
                        {
                            short s = (short)(buffer[offset + ch * 2] | (buffer[offset + ch * 2 + 1] << 8));
                            sum += s / 32768.0f;
                        }
                    }
                    float mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
                    _sampleBuffer[_sampleIndex++] = mono;

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
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * _hanningWindow[i];
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftOrder, _fftBuffer);

        // 1. Beat Detection on Bass Sub-Band (Kick Drum ~40 Hz - 180 Hz, bins 1 to 4 in 256-FFT)
        float bassSum = 0f;
        for (int b = 1; b <= 4; b++)
        {
            bassSum += MathF.Sqrt(_fftBuffer[b].X * _fftBuffer[b].X + _fftBuffer[b].Y * _fftBuffer[b].Y);
        }
        _currentBassEnergy = bassSum;

        // Calculate average bass energy over history window
        float sumHist = 0f;
        for (int i = 0; i < BassHistoryLength; i++) sumHist += _bassHistory[i];
        float avgBass = sumHist / BassHistoryLength;

        _bassHistory[_bassHistoryIndex] = bassSum;
        _bassHistoryIndex = (_bassHistoryIndex + 1) % BassHistoryLength;

        // Detect beat hit if instantaneous energy spikes above average
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedMsSinceLastBeat = _lastBeatTimestamp > 0
            ? Stopwatch.GetElapsedTime(_lastBeatTimestamp, nowTicks).TotalMilliseconds
            : 1000.0;

        if (bassSum > (avgBass * 1.35f + 0.015f) && elapsedMsSinceLastBeat > 160.0)
        {
            _beatIntensity = 1.0f;
            _lastBeatTimestamp = nowTicks;
            OnBeatDetected?.Invoke(bassSum);
        }

        // 2. Full Audio Spectrum Visualizer Bins
        int usefulBins = 60; // Focus on 80 Hz - 4000 Hz human vocal and music range
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

    /// <summary>
    /// Retrieves current target frequency bins for visualizer rendering.
    /// Called at 60 FPS from the UI render loop.
    /// </summary>
    public void GetTargetSpectrum(float[] destination, double deltaTime)
    {
        if (destination == null || destination.Length < RayCount) return;

        lock (_lock)
        {
            // Smoothly decay beat intensity over time
            float decayBeat = (float)(deltaTime * 4.8);
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

            Array.Copy(_decaySpectrum, destination, RayCount);

            // Smooth decay factor (per-frame damping)
            float decayFactor = MathF.Pow(0.82f, (float)(deltaTime * 60.0));
            for (int r = 0; r < RayCount; r++)
            {
                _decaySpectrum[r] *= decayFactor;
                _rawSpectrum[r] *= decayFactor;
            }
        }
    }
}
