using System;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class RmsNoiseGateTests
{
    private sealed class CountingMockWakeWordDetector : IWakeWordDetector
    {
        public string Name => "CountingMock";
        public string WakeWord => "джарвис";
        public event Action? OnWakeWordDetected;
        public long LastDetectionLatencyMs => 0;
        public int ProcessedFramesCount { get; private set; }

        public bool ProcessFrame(ReadOnlySpan<byte> pcmData)
        {
            ProcessedFramesCount++;
            return false;
        }

        public void Trigger() => OnWakeWordDetected?.Invoke();
        public void Reset() { }
        public void Dispose() { }
    }

    private static byte[] GenerateQuietNoise(int bytes = 1120)
    {
        byte[] buffer = new byte[bytes];
        for (int i = 0; i < bytes; i += 2)
        {
            short val = (short)(i % 100 - 50); // amplitude -50 to +50
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void QuietNoise_RmsCalculation_MustBeBelowThreshold()
    {
        byte[] silence = new byte[1120];
        double silenceRms = VoskGrammarWakeWordDetector.CalculateRms(silence);
        Assert.Equal(0.0, silenceRms);

        byte[] quietNoise = GenerateQuietNoise();
        double quietRms = VoskGrammarWakeWordDetector.CalculateRms(quietNoise);
        double listenerRms = VoiceListener.CalculateRms(quietNoise, quietNoise.Length);

        Assert.True(quietRms < 450.0, $"Quiet noise RMS ({quietRms:F1}) must be below RMS gate (450.0)!");
        Assert.True(listenerRms < 450.0, $"VoiceListener RMS ({listenerRms:F1}) must be below RMS gate (450.0)!");
    }

    [Fact]
    public void QuietNoise_MustBeRejectedByKwsGate_WithoutProcessing()
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);
        byte[] quietNoise = GenerateQuietNoise();

        bool triggered = detector.ProcessFrame(quietNoise);

        Assert.False(triggered);
        Assert.Equal(0, detector.AccumulatedPhraseDurationMs);
    }

    [Fact]
    public void QuietNoise_MustBeDiscardedByVoiceListener_WithoutCallingDetector()
    {
        var mockDetector = new CountingMockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Start in WaitingForWakeWord");

        byte[] quietNoise = GenerateQuietNoise();

        // Feed quiet noise (< 450 RMS) through VoiceListener's entry gate
        listener.SimulateAudioInput(quietNoise, quietNoise.Length);

        // Frame must be discarded by RMS Noise Gate before calling detector or Vosk
        Assert.Equal(0, mockDetector.ProcessedFramesCount);
        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);
    }
}
