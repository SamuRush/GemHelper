using System;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class FsmConcurrencyLockTests
{
    private sealed class MockWakeWordDetector : IWakeWordDetector
    {
        public string Name => "MockTriggerable";
        public string WakeWord => "джарвис";
        public event Action? OnWakeWordDetected;
        public long LastDetectionLatencyMs => 10;

        public bool ProcessFrame(ReadOnlySpan<byte> pcmData) => false;
        public void Trigger() => OnWakeWordDetected?.Invoke();
        public void Reset() { }
        public void Dispose() { }
    }

    private static byte[] GenerateSpeechSignal(int bytes = 1120)
    {
        byte[] buffer = new byte[bytes];
        for (int i = 0; i < bytes; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 3000); // loud speech
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void WhileProcessingCommand_WakeWordAudioTriggersAreIgnored()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);
        listener.TransitionToWaitingForWakeWord("Start test in WaitingForWakeWord");
        Assert.False(listener.IsProcessing);

        // Lock FSM into processing mode
        listener.NotifyProcessingStarted();
        Assert.True(listener.IsProcessing, "VoiceListener.IsProcessing must be true after NotifyProcessingStarted()!");

        // Feed speech chunks while _isProcessing == true
        byte[] speechSignal = GenerateSpeechSignal();
        listener.ProcessAudioChunkForTesting(speechSignal, speechSignal.Length, isSpeech: true);
        listener.SimulateAudioInput(speechSignal, speechSignal.Length);

        // Listener must NOT transition to ListeningForCommand while processing!
        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);

        // Release lock
        listener.NotifyProcessingFinished();
        Assert.False(listener.IsProcessing, "VoiceListener.IsProcessing must be false after NotifyProcessingFinished()!");
    }

    [Fact]
    public void WhileProcessingCommand_AdaptiveWakeWordEventDoesNotTransitionFsm()
    {
        var mockDetector = new MockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Start test in WaitingForWakeWord");

        // Lock processing
        listener.NotifyProcessingStarted();
        Assert.True(listener.IsProcessing);

        // Trigger detector event concurrently while processing
        mockDetector.Trigger();

        // Must remain in WaitingForWakeWord (parallel wake-word trigger locked out)
        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);

        listener.NotifyProcessingFinished();
        Assert.False(listener.IsProcessing);
    }
}
