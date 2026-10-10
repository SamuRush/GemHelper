using System;
using System.Diagnostics;

namespace Gem.UI;

/// <summary>
/// Transient beat detector tracking rolling bass energy history (~1 sec across 43 slots).
/// Fires transients when instantaneous filtered bass exceeds historical rolling average by 1.35x.
/// </summary>
public sealed class BeatDetector
{
    public const int BassHistoryLength = 43;
    public const double BeatCooldownMs = 280.0;
    public const float BeatThresholdFactor = 1.35f;
    public const int BassFramesPerSlot = 4;

    private readonly float[] _bassHistory = new float[BassHistoryLength];
    private int _bassHistoryIndex = 0;
    private float _bassFrameAccumulator = 0f;
    private int _bassFrameCount = 0;

    private float _beatIntensity = 0f;
    private long _lastBeatTimestamp = 0;

    public float BeatIntensity => _beatIntensity;

    public event Action<float>? OnBeatDetected;

    public void ProcessBassEnergy(float bassSum)
    {
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
        for (int i = 0; i < BassHistoryLength; i++)
        {
            sumHist += _bassHistory[i];
        }
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
    }

    public void Decay(double deltaTime)
    {
        float decayBeat = (float)(deltaTime * 5.0);
        _beatIntensity = Math.Max(0f, _beatIntensity - decayBeat);
    }

    public float GetAverageBassEnergy()
    {
        float sum = 0f;
        for (int i = 0; i < BassHistoryLength; i++)
        {
            sum += _bassHistory[i];
        }
        return sum / BassHistoryLength;
    }
}
