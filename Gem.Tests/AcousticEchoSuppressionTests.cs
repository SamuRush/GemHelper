using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Gem.Services;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class AcousticEchoSuppressionTests
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

    private sealed class MockTtsEngine : ITtsEngine
    {
        public string Name { get; }
        public bool IsAvailable { get; }
        public int SpeakCount { get; private set; }

        public MockTtsEngine(string name = "MockEdge", bool isAvailable = true)
        {
            Name = name;
            IsAvailable = isAvailable;
        }

        public Task SpeakAsync(string text, CancellationToken ct = default)
        {
            SpeakCount++;
            return Task.CompletedTask;
        }
    }

    private static byte[] GenerateSpeechSignal(int bytes = 1120)
    {
        byte[] buffer = new byte[bytes];
        for (int i = 0; i < bytes; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 3000); // loud speech ~2120 RMS
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void WhenSpeaking_IncomingMicAudioFramesMustBeDiscarded()
    {
        var mockDetector = new CountingMockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Start in WaitingForWakeWord");

        // Activate TTS speech mode
        listener.NotifySpeakingStarted();
        Assert.True(listener.IsSpeaking, "VoiceListener.IsSpeaking must be true during TTS playback!");

        // Feed speech frame (loud audio, RMS > 2000)
        byte[] speechFrame = GenerateSpeechSignal();
        listener.SimulateAudioInput(speechFrame, speechFrame.Length);

        // Frame must be completely discarded by Acoustic Echo Suppression
        Assert.Equal(0, mockDetector.ProcessedFramesCount);
        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);

        // Finished speaking
        listener.NotifySpeakingFinished();
        Assert.False(listener.IsSpeaking, "VoiceListener.IsSpeaking must be false after TTS playback finished!");
    }

    [Fact]
    public async Task TtsPlayback_WithCooldown_UnblocksMicOnlyAfterCooldownElapses()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);
        listener.TransitionToWaitingForWakeWord("Start in WaitingForWakeWord");
        listener.NotifyProcessingStarted();

        var mockEdge = new MockTtsEngine("Edge", isAvailable: true);
        var mockSilero = new MockTtsEngine("Silero", isAvailable: false);
        var mockSystem = new MockTtsEngine("System.Speech", isAvailable: true);
        var composite = new CompositeVoiceFeedbackService(listener, mockEdge, mockSilero, mockSystem);

        var sw = Stopwatch.StartNew();
        await composite.SpeakAsync("Тестовое подтверждение действия.");
        sw.Stop();

        // 1. Microphone must be unblocked after SpeakAsync + cooldown
        Assert.False(listener.IsSpeaking, "IsSpeaking must be false after SpeakAsync completes!");
        Assert.False(listener.IsProcessing, "IsProcessing must be false after SpeakAsync completes!");

        // 2. Cooldown window must be at least 200 ms (nominal 250 ms)
        Assert.True(sw.ElapsedMilliseconds >= 200, $"TTS playback + cooldown took {sw.ElapsedMilliseconds} ms, expected >= 200 ms!");
    }
}
