using System;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class VoiceListenerTests
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
            short val = (short)(i % 100 - 50); // amplitude -50 to +50 (RMS ~ 28.8 < 450)
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    private static byte[] GenerateSpeechSignal(int durationMs = 35, int sampleRate = 16000)
    {
        // 16 kHz 16-bit mono = 32 bytes per ms
        int totalBytes = durationMs * (sampleRate * 2 / 1000);
        byte[] buffer = new byte[totalBytes];

        for (int i = 0; i < totalBytes; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 3000); // speech amplitude ~3000 (RMS ~ 2121 > 450)
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void RmsCalculation_SilenceAndQuietNoise_MustBeBelowNoiseGate()
    {
        // 1. Digital silence (0 amplitude)
        byte[] silence = new byte[1120];
        double silenceRmsListener = VoiceListener.CalculateRms(silence, silence.Length);
        double silenceRmsDetector = VoskGrammarWakeWordDetector.CalculateRms(silence);

        Assert.Equal(0.0, silenceRmsListener);
        Assert.Equal(0.0, silenceRmsDetector);

        // 2. Quiet room noise / breathing
        byte[] quietNoise = GenerateQuietNoise();
        double quietRmsListener = VoiceListener.CalculateRms(quietNoise, quietNoise.Length);
        double quietRmsDetector = VoskGrammarWakeWordDetector.CalculateRms(quietNoise);

        Assert.True(quietRmsListener < 450.0, $"VoiceListener RMS ({quietRmsListener:F1}) must be below 450.0 threshold!");
        Assert.True(quietRmsDetector < 450.0, $"Detector RMS ({quietRmsDetector:F1}) must be below 450.0 threshold!");
    }

    [Fact]
    public void RmsCalculation_SpeechLevelAudio_MustBeAboveNoiseGate()
    {
        byte[] speech = GenerateSpeechSignal(durationMs: 35);
        double speechRmsListener = VoiceListener.CalculateRms(speech, speech.Length);
        double speechRmsDetector = VoskGrammarWakeWordDetector.CalculateRms(speech);

        Assert.True(speechRmsListener >= 450.0, $"Speech RMS ({speechRmsListener:F1}) must exceed 450.0 threshold!");
        Assert.True(speechRmsDetector >= 450.0, $"Detector RMS ({speechRmsDetector:F1}) must exceed 450.0 threshold!");
    }

    [Fact]
    public void RmsNoiseGate_VoiceListener_DiscardsQuietAudioWithoutInvokingDetector()
    {
        var mockDetector = new CountingMockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Initialize for testing");

        byte[] quietNoise = GenerateQuietNoise();

        // Feed quiet noise below 450 RMS
        listener.SimulateAudioInput(quietNoise, quietNoise.Length);

        // Must be intercepted by RMS Noise Gate before reaching wake-word detector
        Assert.Equal(0, mockDetector.ProcessedFramesCount);
        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);
    }

    [Fact]
    public void RmsNoiseGate_VoiceListener_PassesSpeechAudioToDetector()
    {
        var mockDetector = new CountingMockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Initialize for testing");

        byte[] speech = GenerateSpeechSignal(durationMs: 35);

        // Feed loud speech above 450 RMS
        listener.SimulateAudioInput(speech, speech.Length);

        // Frame must pass through the RMS gate into the detector
        Assert.Equal(1, mockDetector.ProcessedFramesCount);
    }

    [Fact]
    public void RmsNoiseGate_VoiceListener_FiresRmsLevelChangedEvent()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);
        listener.TransitionToWaitingForWakeWord("Initialize for testing");

        float reportedRms = -1f;
        listener.OnRmsLevelChanged += level => reportedRms = level;

        byte[] speech = GenerateSpeechSignal(durationMs: 35);
        listener.SimulateAudioInput(speech, speech.Length);

        Assert.True(reportedRms >= 450f, $"Reported RMS level {reportedRms} should be >= 450");
    }

    [Fact]
    public void RmsNoiseGate_DetectorRejectsQuietAudioWithoutAccumulatingDuration()
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);
        byte[] quietNoise = GenerateQuietNoise();

        bool triggered = detector.ProcessFrame(quietNoise);

        Assert.False(triggered);
        Assert.Equal(0, detector.AccumulatedPhraseDurationMs);
    }

    [Fact]
    public void AcousticDurationFilter_ShortFramesUnder150Ms_DoNotTriggerWakeWord()
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool eventFired = false;
        detector.OnWakeWordDetected += () => eventFired = true;

        // Frame 1: 35 ms (< 150 ms threshold)
        byte[] frame35ms = GenerateSpeechSignal(durationMs: 35);
        bool triggered1 = detector.ProcessFrame(frame35ms);
        Assert.False(triggered1);
        Assert.False(eventFired);
        Assert.Equal(35, detector.AccumulatedPhraseDurationMs);

        // Frame 2: 35 ms (accumulated 70 ms < 150 ms)
        bool triggered2 = detector.ProcessFrame(frame35ms);
        Assert.False(triggered2);
        Assert.False(eventFired);
        Assert.Equal(70, detector.AccumulatedPhraseDurationMs);

        // Frame 3: 35 ms (accumulated 105 ms < 150 ms)
        bool triggered3 = detector.ProcessFrame(frame35ms);
        Assert.False(triggered3);
        Assert.False(eventFired);
        Assert.Equal(105, detector.AccumulatedPhraseDurationMs);
    }

    [Theory]
    [InlineData("джа")]
    [InlineData("рис")]
    [InlineData("да")]
    [InlineData("джар")]
    [InlineData("сюрприз")]
    [InlineData("вис")]
    [InlineData("джарвиса")]
    [InlineData("джарвису")]
    [InlineData("")]
    [InlineData("[unk]")]
    public void AcousticDurationFilter_PhoneticSnippetsAndSubwords_Rejected(string snippet)
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool isMatch = detector.IsIsolatedTokenMatch(snippet, "джарвис");

        Assert.False(isMatch, $"Phonetic fragment '{snippet}' must NOT trigger isolated token match!");
    }

    [Theory]
    [InlineData("джарвис")]
    [InlineData("  джарвис  ")]
    [InlineData("джарвис [unk]")]
    [InlineData("[unk] джарвис")]
    [InlineData("эй джарвис")]
    [InlineData("джарвис слушай")]
    public void AcousticDurationFilter_ValidIsolatedTokens_Accepted(string text)
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool isMatch = detector.IsIsolatedTokenMatch(text, "джарвис");

        Assert.True(isMatch, $"Target token '{text}' must match wake-word 'джарвис'!");
    }

    [Fact]
    public void VoiceListener_WakeWordTrigger_TransitionsToListeningForCommand()
    {
        var mockDetector = new CountingMockWakeWordDetector();
        using var listener = new VoiceListener(wakeWords: ["джарвис"], wakeWordDetector: mockDetector);
        listener.TransitionToWaitingForWakeWord("Start in WaitingForWakeWord");

        Assert.Equal(VoiceListenerState.WaitingForWakeWord, listener.CurrentState);

        // Trigger wake-word event
        mockDetector.Trigger();

        // Must immediately transition to ListeningForCommand
        Assert.Equal(VoiceListenerState.ListeningForCommand, listener.CurrentState);
    }
}
