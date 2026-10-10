using System;
using System.Threading;
using Gem.Core;
using Gem.Services;
using Gem.UI;
using Gem.Voice;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace Gem.Tests;

public class HudModernizationTests
{
    private static byte[] GenerateSpeechSignal(int bytes = 1120)
    {
        byte[] buffer = new byte[bytes];
        for (int i = 0; i < bytes; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 3000); // loud audio ~2120 RMS
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }
        return buffer;
    }

    [Fact]
    public void VoiceListener_RaisesOnRmsLevelChanged_OnIncomingAudio()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);
        listener.TransitionToWaitingForWakeWord("Start in WaitingForWakeWord");

        float detectedRms = -1f;
        listener.OnRmsLevelChanged += (rms) =>
        {
            detectedRms = rms;
        };

        byte[] speechFrame = GenerateSpeechSignal();
        listener.SimulateAudioInput(speechFrame, speechFrame.Length);

        Assert.True(detectedRms > 0, $"Expected RMS > 0, but got {detectedRms}");
    }

    [Fact]
    public void OverlayWidget_StateGlowColors_MatchRoadmapSpecification()
    {
        // Technical requirement 2:
        // Idle: #00E5FF
        // Listening: #FFD600
        // Thinking: #D500F9
        // Action: #00E676
        Assert.Equal(MediaColor.FromRgb(0x00, 0xE5, 0xFF), OverlayWidget.GetStateGlowColor(JarvisState.Idle));
        Assert.Equal(MediaColor.FromRgb(0xFF, 0xD6, 0x00), OverlayWidget.GetStateGlowColor(JarvisState.Listening));
        Assert.Equal(MediaColor.FromRgb(0xD5, 0x00, 0xF9), OverlayWidget.GetStateGlowColor(JarvisState.Thinking));
        Assert.Equal(MediaColor.FromRgb(0x00, 0xE6, 0x76), OverlayWidget.GetStateGlowColor(JarvisState.Action));
    }

    [Fact]
    public void ActiveTimer_RemainingCountdown_FormatsAsMinutesAndSeconds()
    {
        var timer = new ActiveTimer
        {
            Duration = TimeSpan.FromSeconds(125),
            TriggerAt = DateTime.UtcNow.AddSeconds(125)
        };

        string formatted = timer.Remaining.ToString(@"mm\:ss");
        Assert.Matches(@"^\d{2}:\d{2}$", formatted);
    }
}
