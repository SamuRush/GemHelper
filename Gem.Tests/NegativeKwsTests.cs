using System;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class NegativeKwsTests
{
    private static byte[] GenerateSpeechFrame(int durationMs, int sampleRate = 16000)
    {
        int bytesPerMs = sampleRate * 2 / 1000; // 32 bytes/ms for 16kHz 16-bit mono
        int totalBytes = durationMs * bytesPerMs;
        byte[] buffer = new byte[totalBytes];

        // 400 Hz sine wave with high speech-level amplitude (~3000, RMS ~2120 > 450)
        for (int i = 0; i < totalBytes; i += 2)
        {
            short sample = (short)(Math.Sin(i * 0.05) * 3000);
            buffer[i] = (byte)(sample & 0xFF);
            buffer[i + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void ShortAudioFrames_Under150ms_DoNotTriggerWakeWord()
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool eventFired = false;
        detector.OnWakeWordDetected += () => eventFired = true;

        // 1. Single 35 ms frame (< 150 ms threshold)
        byte[] frame35ms = GenerateSpeechFrame(35);
        bool triggered1 = detector.ProcessFrame(frame35ms);

        Assert.False(triggered1);
        Assert.False(eventFired);
        Assert.True(detector.AccumulatedPhraseDurationMs < 150);
        Assert.Equal(35, detector.AccumulatedPhraseDurationMs);

        // 2. Second 35 ms frame (total 70 ms < 150 ms)
        bool triggered2 = detector.ProcessFrame(frame35ms);
        Assert.False(triggered2);
        Assert.False(eventFired);
        Assert.Equal(70, detector.AccumulatedPhraseDurationMs);

        // 3. Third 35 ms frame (total 105 ms < 150 ms)
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
    public void PhoneticSnippetsAndSubwords_DoNotMatchIsolatedWakeWord(string snippet)
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool isMatch = detector.IsIsolatedTokenMatch(snippet, "джарвис");

        Assert.False(isMatch, $"Snippet '{snippet}' must NOT trigger isolated token match for 'джарвис'!");
    }

    [Theory]
    [InlineData("джарвис")]
    [InlineData("  джарвис  ")]
    [InlineData("джарвис [unk]")]
    [InlineData("[unk] джарвис")]
    [InlineData("эй джарвис")]
    [InlineData("джарвис слушай")]
    public void ValidIsolatedTokens_MatchWakeWord(string text)
    {
        using var detector = new VoskGrammarWakeWordDetector("джарвис", minDurationMs: 150, noiseGateRms: 450.0);

        bool isMatch = detector.IsIsolatedTokenMatch(text, "джарвис");

        Assert.True(isMatch, $"Valid token phrase '{text}' must match 'джарвис'!");
    }
}
