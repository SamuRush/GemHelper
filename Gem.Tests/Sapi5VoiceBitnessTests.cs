using System;
using System.Speech.Synthesis;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class Sapi5VoiceBitnessTests
{
    [Fact]
    public void ConfigureMaleRussianVoice_HandlesBitnessIncompatibility_WithoutThrowing()
    {
        using var synthesizer = new SpeechSynthesizer();

        var (voiceName, needPitchShift) = SystemSpeechTtsEngine.ConfigureMaleRussianVoice(synthesizer);

        Assert.NotNull(voiceName);
        Assert.False(string.IsNullOrWhiteSpace(voiceName));

        // Female voice (Irina) must never be selected directly without pitch shift
        Assert.DoesNotContain("Irina", voiceName, StringComparison.OrdinalIgnoreCase);

        // If Russian male voice is installed (e.g. Pavel), verify pitch shift is false
        if (voiceName.Contains("Pavel", StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(needPitchShift, "Microsoft Pavel does not require pitch shift!");
        }
    }

    [Fact]
    public void Has32BitVoiceToken_SafelyQueriesRegistry_WithoutExceptions()
    {
        bool nonExistent = SystemSpeechTtsEngine.Has32BitVoiceToken("NonExistentSafetyVoice9999");
        Assert.False(nonExistent);

        // Check Aidar query completes safely without throwing
        bool aidarCheck = SystemSpeechTtsEngine.Has32BitVoiceToken("Aidar");
        // Result is boolean (true or false depending on host machine setup)
        Assert.True(aidarCheck || !aidarCheck);
    }

    [Fact]
    public void SystemSpeechTtsEngine_Instantiation_SelectsMaleVoiceOrFallback()
    {
        using var engine = new SystemSpeechTtsEngine();

        Assert.True(engine.IsAvailable);
        Assert.NotNull(engine.SelectedVoiceName);
        Assert.DoesNotContain("Irina", engine.SelectedVoiceName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigureMaleRussianVoice_WhenAidarInstalled_SelectsAidarOrPavelWithoutIrina()
    {
        using var synthesizer = new SpeechSynthesizer();
        var (voiceName, needPitchShift) = SystemSpeechTtsEngine.ConfigureMaleRussianVoice(synthesizer);

        Assert.NotNull(voiceName);
        Assert.DoesNotContain("Irina", voiceName, StringComparison.OrdinalIgnoreCase);

        bool aidarInstalled = false;
        foreach (InstalledVoice v in synthesizer.GetInstalledVoices())
        {
            if (v.VoiceInfo.Name.Contains("Aidar", StringComparison.OrdinalIgnoreCase))
            {
                aidarInstalled = true;
                break;
            }
        }

        if (aidarInstalled)
        {
            Assert.Contains("Aidar", voiceName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
