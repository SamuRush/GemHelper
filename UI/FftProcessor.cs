using System;
using NAudio.Dsp;

namespace Gem.UI;

/// <summary>
/// High-performance FFT processor computing frequency spectra and logarithmic dB scaling.
/// Pre-computes Hanning windows and logarithmic frequency band bins for zero allocations.
/// </summary>
public sealed class FftProcessor
{
    public const int FftSize = 256;
    public const int FftOrder = 8; // 2^8 = 256
    public const int RayCount = 128; // 128 bilateral rays (64 frequency bands mirrored)

    private readonly float[] _hanningWindow = new float[FftSize];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly Complex[] _lpfFftBuffer = new Complex[FftSize];

    private readonly int[] _bandStartBin = new int[RayCount / 2];
    private readonly int[] _bandEndBin = new int[RayCount / 2];

    public FftProcessor()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _hanningWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }

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

    /// <summary>
    /// Computes filtered bass FFT for beat detection and sub-bass measurement.
    /// </summary>
    public void ComputeLpfFft(float[] lpfSamples, out float bassEnergy, out float subBassEnergy)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _lpfFftBuffer[i].X = lpfSamples[i] * _hanningWindow[i];
            _lpfFftBuffer[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftOrder, _lpfFftBuffer);

        float bassSum = 0f;
        for (int b = 1; b <= 4; b++)
        {
            bassSum += MathF.Sqrt(_lpfFftBuffer[b].X * _lpfFftBuffer[b].X + _lpfFftBuffer[b].Y * _lpfFftBuffer[b].Y);
        }
        bassEnergy = bassSum;

        float subBassMag = MathF.Sqrt(_lpfFftBuffer[1].X * _lpfFftBuffer[1].X + _lpfFftBuffer[1].Y * _lpfFftBuffer[1].Y);
        subBassEnergy = Math.Clamp(subBassMag * 4.5f, 0f, 1f);
    }

    /// <summary>
    /// Computes full-bandwidth raw audio FFT for honest radial equalizer spectrum.
    /// </summary>
    public float ComputeFullSpectrum(float[] rawSamples, double gain, float[] rawSpectrum)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuffer[i].X = rawSamples[i] * _hanningWindow[i];
            _fftBuffer[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftOrder, _fftBuffer);

        int halfRays = RayCount / 2;
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

            float freqBoost = 1.0f + 2.0f * ((float)r / halfRays);

            float db = 20.0f * MathF.Log10(maxMag + 1e-6f);
            const float minDb = -60.0f;
            float normalizedDb = Math.Clamp((db - minDb) / (-minDb), 0f, 1f);
            float gainFactor = (float)(gain / 3.5);
            float scaled = Math.Clamp(normalizedDb * gainFactor * freqBoost, 0f, 1f);

            rawSpectrum[r] = scaled;
            rawSpectrum[RayCount - 1 - r] = scaled;

            totalSpectralSum += scaled;
        }

        return Math.Clamp(totalSpectralSum / (halfRays * 0.28f), 0f, 1f);
    }
}
