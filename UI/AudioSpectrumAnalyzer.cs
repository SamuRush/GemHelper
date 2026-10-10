using System;
using NAudio.Dsp;

namespace Gem.UI;

/// <summary>
/// High-performance audio spectrum analyzer for the JARVIS Arc Reactor holographic visualizer.
/// Computes real-time 256-point Fast Fourier Transforms (NAudio FFT) with Hanning windowing
/// across microphone input (Vosk STT) and assistant speech output (Edge-TTS / SAPI5).
/// Generates 96 symmetrical frequency bins covering the human vocal spectrum (80 Hz - 4500 Hz).
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

    private void ExecuteFft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * _hanningWindow[i];
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftOrder, _fftBuffer);

        int usefulBins = 60; // Focus on 80 Hz - 4000 Hz human vocal range
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
