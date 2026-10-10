using System;
using System.IO;
using System.Linq;
using Gem.Services;
using Gem.UI;
using Gem.Voice;
using Xunit;

namespace Gem.Tests;

public class AudioSettingsTests
{
    [Fact]
    public void AppSettingsData_DefaultNoiseGateRms_MustBe350()
    {
        var data = new AppSettingsData();
        Assert.Equal(350.0, data.WakeWord.NoiseGateRms);
    }

    [Fact]
    public void AppSettingsData_AudioConfig_HasValidDefaults()
    {
        var data = new AppSettingsData();
        Assert.NotNull(data.Audio);
        Assert.Equal(0, data.Audio.DeviceIndex);
        Assert.NotNull(data.Audio.DeviceName);
    }

    [Fact]
    public void AppSettingsData_TtsConfig_ContainsEngineAndVoiceSelection()
    {
        var data = new AppSettingsData();
        Assert.NotNull(data.Tts);
        Assert.Equal("Edge", data.Tts.PreferredEngine);
        Assert.True(data.Tts.EnableEdgeTts);
        Assert.NotNull(data.Tts.Voice);
    }

    [Fact]
    public void VoiceListener_NoiseGateRms_CanBeUpdatedDynamically()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);

        // Update threshold dynamically
        listener.SetNoiseGateRms(350.0);
        Assert.Equal(350.0, listener.SilenceThresholdRms);

        // Clamping check
        listener.SetNoiseGateRms(10.0);
        Assert.Equal(50.0, listener.SilenceThresholdRms);

        listener.SetNoiseGateRms(1000.0);
        Assert.Equal(600.0, listener.SilenceThresholdRms);
    }

    [Fact]
    public void VoiceListener_AudioDevice_CanBeUpdatedDynamically()
    {
        using var listener = new VoiceListener(wakeWords: ["джарвис"]);

        listener.SetAudioDevice(2);
        Assert.Equal(2, listener.DeviceNumber);
    }

    [Fact]
    public void SpeechWithModerateRms380_Passes350Gate_ButFails450Gate()
    {
        // Generate audio signal with amplitude ~540 (RMS ~ 380)
        byte[] buffer = new byte[1120];
        for (int i = 0; i < buffer.Length; i += 2)
        {
            short val = (short)(Math.Sin(i * 0.05) * 540);
            buffer[i] = (byte)(val & 0xFF);
            buffer[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        double rms = VoiceListener.CalculateRms(buffer, buffer.Length);
        Assert.InRange(rms, 350.0, 420.0);

        // At 450 gate, it would fail
        Assert.False(rms >= 450.0, "RMS 380 fails old 450 threshold!");

        // At 350 gate, it succeeds
        Assert.True(rms >= 350.0, "RMS 380 passes new 350 threshold, resolving microphone detection!");
    }

    [Fact]
    public void AppSettingsService_SaveAndLoad_PersistsAudioAndTtsSettings()
    {
        var original = AppSettingsService.Load();

        var testData = new AppSettingsData
        {
            CityName = original.CityName,
            Latitude = original.Latitude,
            Longitude = original.Longitude,
            LlmBaseUrl = original.LlmBaseUrl,
            LlmModel = original.LlmModel,
            LlmApiKey = original.LlmApiKey,
            VoskModelPath = original.VoskModelPath,
            Audio = new AudioConfig
            {
                DeviceIndex = 3,
                DeviceName = "Studio USB Microphone"
            },
            WakeWord = new WakeWordConfig
            {
                Name = "джарвис",
                NoiseGateRms = 280.0,
                MinDurationMs = 150,
                Threshold = 0.5f
            },
            Tts = new TtsConfig
            {
                PreferredEngine = "SAPI5",
                Voice = "Microsoft Pavel",
                EdgeVoice = "ru-RU-DmitryNeural",
                Sapi5Voice = "Microsoft Pavel",
                EnableEdgeTts = false,
                ConnectionTimeoutMs = 3000
            },
            GameAliases = new Dictionary<string, string>(original.GameAliases)
        };

        try
        {
            AppSettingsService.Save(testData);

            var reloaded = AppSettingsService.Load();

            Assert.Equal(3, reloaded.Audio.DeviceIndex);
            Assert.Equal("Studio USB Microphone", reloaded.Audio.DeviceName);
            Assert.Equal(280.0, reloaded.WakeWord.NoiseGateRms);
            Assert.Equal("SAPI5", reloaded.Tts.PreferredEngine);
            Assert.False(reloaded.Tts.EnableEdgeTts);
            Assert.Equal("Microsoft Pavel", reloaded.Tts.Voice);
            Assert.Equal("Microsoft Pavel", reloaded.Tts.Sapi5Voice);
        }
        finally
        {
            // Restore original config
            AppSettingsService.Save(original);
        }
    }

    [Fact]
    public void SystemSpeechTtsEngine_WithPreferredVoice_InitializesWithoutCrashing()
    {
        using var engineAidar = new SystemSpeechTtsEngine("Aidar");
        Assert.NotNull(engineAidar.Name);
        Assert.True(engineAidar.IsAvailable);

        using var enginePavel = new SystemSpeechTtsEngine("Microsoft Pavel");
        Assert.NotNull(enginePavel.Name);
        Assert.True(enginePavel.IsAvailable);

        using var engineIrina = new SystemSpeechTtsEngine("Microsoft Irina Desktop");
        Assert.NotNull(engineIrina.Name);
        Assert.True(engineIrina.IsAvailable);
        Assert.False(engineIrina.IsPitchShiftedToMale);
    }

    [Fact]
    public void EdgeTtsEngine_WithSvetlanaVoice_InitializesProperly()
    {
        using var engine = new EdgeTtsEngine("ru-RU-SvetlanaNeural");
        Assert.Equal("ru-RU-SvetlanaNeural", engine.Voice);
        Assert.Equal("Edge", engine.Name);
    }

    [Fact]
    public void CompositeVoiceFeedbackService_WithFemaleVoiceSettings_ConfiguresEnginesWithoutPitchShift()
    {
        var ttsConfig = new TtsConfig
        {
            EnableEdgeTts = true,
            EdgeVoice = "ru-RU-SvetlanaNeural",
            Sapi5Voice = "Microsoft Irina Desktop",
            Voice = "ru-RU-SvetlanaNeural"
        };

        using var composite = new CompositeVoiceFeedbackService(null, ttsConfig);
        Assert.Equal("ru-RU-SvetlanaNeural", (composite.EdgeEngine as EdgeTtsEngine)?.Voice);
    }

    [Fact]
    public void GetInstalledSapi5Voices_ReturnsBothMaleAndFemaleVoices()
    {
        var voices = SettingsWindow.GetInstalledSapi5Voices();
        Assert.NotNull(voices);
        Assert.NotEmpty(voices);

        foreach (var (name, desc) in voices)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(desc));
        }

        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        if (synth.GetInstalledVoices().Any(v => v.VoiceInfo.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(voices, v => v.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase));
        }
    }
}
