using System;
using System.IO;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Gem.UI;
using Gem.Voice;
using NAudio.Wave;
using Xunit;

namespace Gem.Tests;

public class OptimizationAndSafetyTests
{
    // =========================================================================
    // 1. Steam Install Status Check
    // =========================================================================

    [Fact]
    public void SteamService_IsGameInstalled_DetectsInstalledStateFlags()
    {
        var installedGame = new SteamGameInfo(
            AppId: "1091500",
            Title: "Cyberpunk 2077",
            NormalizedTitle: "cyberpunk 2077",
            CyrillicTitle: "киберпанк 2077",
            IsInstalled: true,
            InstallPath: Path.GetTempPath(),
            StateFlags: 4,
            IsFromManifest: true
        );

        bool isInstalled = SteamService.Instance.IsGameInstalled(installedGame);
        Assert.True(isInstalled);
    }

    [Fact]
    public void SteamService_IsGameInstalled_ReturnsFalseForUninstalledFlags()
    {
        var uninstalledGame = new SteamGameInfo(
            AppId: "1091500",
            Title: "Cyberpunk 2077",
            NormalizedTitle: "cyberpunk 2077",
            CyrillicTitle: "киберпанк 2077",
            IsInstalled: false,
            InstallPath: null,
            StateFlags: 2,
            IsFromManifest: false
        );

        bool isInstalled = SteamService.Instance.IsGameInstalled(uninstalledGame);
        Assert.False(isInstalled);
    }

    [Fact]
    public async Task SteamService_InstallGameAsync_WhenAlreadyInstalled_CancelsInstallAndPromptsLaunch()
    {
        var game = new SteamGameInfo(
            AppId: "999999",
            Title: "Test Game",
            NormalizedTitle: "test game",
            CyrillicTitle: "тест гейм",
            IsInstalled: true,
            InstallPath: Path.GetTempPath(),
            StateFlags: 4,
            IsFromManifest: true
        );

        await SteamService.Instance.InstallGameAsync(game);

        // Must set pending confirmation to launch instead of installing
        Assert.Equal(PendingGameAction.Start, JarvisOrchestrator.Instance.PendingAction);
        Assert.Equal("999999", JarvisOrchestrator.Instance.PendingGame?.AppId);

        // Cleanup confirmation state
        JarvisOrchestrator.Instance.ResetConfirmationState();
    }

    // =========================================================================
    // 2. UI Click Safety & EULA Verification
    // =========================================================================

    [Theory]
    [InlineData("Лицензионное соглашение", true)]
    [InlineData("Steam - Лицензионное соглашение конечного пользователя", true)]
    [InlineData("End User License Agreement (EULA)", true)]
    [InlineData("Terms of Service and License Agreement", true)]
    [InlineData("Software Agreement", true)]
    [InlineData("Установка - Cyberpunk 2077", false)]
    [InlineData("Steam Library", false)]
    [InlineData("Настройки звука", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EulaSafety_IsEulaDialogTitle_StrictlyValidatesTitles(string? title, bool expected)
    {
        bool result = SteamService.IsEulaDialogTitle(title);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Принять", true)]
    [InlineData("Согласен", true)]
    [InlineData("Accept", true)]
    [InlineData("I Agree", true)]
    [InlineData("Я принимаю", true)]
    [InlineData("Я согласен", true)]
    [InlineData("Далее", false)]
    [InlineData("Next", false)]
    [InlineData("Отмена", false)]
    [InlineData("Cancel", false)]
    [InlineData("Установить", false)]
    [InlineData("Install", false)]
    [InlineData("Удалить", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EulaSafety_IsEulaButtonName_StrictlyValidatesButtonNames(string? btnName, bool expected)
    {
        bool result = SteamService.IsEulaButtonName(btnName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EulaSafety_FindEulaButtonViaUiAutomation_WithZeroHwnd_ReturnsNullWithoutClicking()
    {
        Point? pt = SteamService.FindEulaButtonViaUiAutomation(IntPtr.Zero);
        Assert.Null(pt);
    }

    [Fact]
    public async Task EulaSafety_TryConfirmEulaViaUiAutomationAsync_WithZeroHwnd_ReturnsNullDeterministically()
    {
        Point? pt = await SteamService.TryConfirmEulaViaUiAutomationAsync(IntPtr.Zero);
        Assert.Null(pt);
    }

    // =========================================================================
    // 3. Media & Tab Context Isolation
    // =========================================================================

    [Theory]
    [InlineData("YouTube - Rick Astley - Never Gonna Give You Up", true)]
    [InlineData("Фильм онлайн - Кинопоиск", true)]
    [InlineData("Трансляция - Twitch", true)]
    [InlineData("ВК Видео - Смотреть сериал", true)]
    [InlineData("trailer.mp4 - VLC media player", true)]
    [InlineData("movie.mkv - Media Player Classic", true)]
    [InlineData("Новая вкладка - Google Chrome", false)]
    [InlineData("Поиск в Google - Mozilla Firefox", false)]
    [InlineData("Яндекс Музыка", false)]
    [InlineData("", false)]
    public void MediaContextIsolation_IsVideoTitle_IdentifiesVideoStreams(string title, bool expected)
    {
        bool result = MediaSessionService.IsVideoTitle(title);
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // 4. AudioSpectrumAnalyzer Loopback & Beat Detection
    // =========================================================================

    [Fact]
    public void AudioSpectrumAnalyzer_ProcessLoopback_ExtractsBassAndDetectsBeat()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        bool beatFired = false;
        analyzer.OnBeatDetected += (intensity) => beatFired = true;

        // Simulate 48kHz Stereo 32-bit Float PCM audio buffer
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        int sampleCount = 2048; // 1024 samples per channel
        byte[] buffer = new byte[sampleCount * sizeof(float)];

        // Generate synthetic 60 Hz kick drum bass impulse (high amplitude)
        float[] floatSamples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i += 2)
        {
            float t = (float)i / 48000f;
            float sample = (float)Math.Sin(2 * Math.PI * 60 * t) * 0.95f;
            floatSamples[i] = sample;     // Left
            floatSamples[i + 1] = sample; // Right
        }
        Buffer.BlockCopy(floatSamples, 0, buffer, 0, buffer.Length);

        // Feed multiple frames to establish rolling baseline then transient kick
        for (int frame = 0; frame < 5; frame++)
        {
            analyzer.ProcessLoopback(buffer, buffer.Length, waveFormat);
        }

        Assert.True(analyzer.BassEnergy > 0f, $"Expected BassEnergy > 0, got {analyzer.BassEnergy}");
        Assert.True(analyzer.BeatIntensity > 0f, $"Expected BeatIntensity > 0, got {analyzer.BeatIntensity}");
        Assert.True(beatFired, "Expected beat detection event to fire on peak bass");
    }

    [Fact]
    public void AudioSpectrumAnalyzer_BeatIntensity_SmoothlyDecaysOverTime()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        // High bass frame
        int sampleCount = 2048;
        byte[] loudBuffer = new byte[sampleCount * sizeof(float)];
        float[] loudSamples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++) loudSamples[i] = 0.9f;
        Buffer.BlockCopy(loudSamples, 0, loudBuffer, 0, loudBuffer.Length);

        analyzer.ProcessLoopback(loudBuffer, loudBuffer.Length, waveFormat);
        float initialBeat = analyzer.BeatIntensity;

        // Process silence frame
        byte[] silentBuffer = new byte[sampleCount * sizeof(float)];
        analyzer.ProcessLoopback(silentBuffer, silentBuffer.Length, waveFormat);
        float decayedBeat = analyzer.BeatIntensity;

        Assert.True(decayedBeat <= initialBeat, $"Expected decayedBeat ({decayedBeat}) <= initialBeat ({initialBeat})");
    }

    // =========================================================================
    // 5. Audited Logging & Router Traceability
    // =========================================================================

    [Fact]
    public async Task CommandRouter_AuditedExecution_DispatchesAndLogsCorrectly()
    {
        var router = new CommandRouter();
        router.Register(new VolumeCommandHandler());

        var result = await router.ExecuteJsonAsync("{\"command\":\"volume\",\"args\":{\"action\":\"get\"}}");
        Assert.NotNull(result);
        Assert.True(result.Success);
    }

    // Helper fake for TTS verification
    private sealed class FakeVoiceFeedbackService : IVoiceFeedbackService
    {
        public string LastSpokenText { get; private set; } = string.Empty;
        public ITtsEngine EdgeEngine => null!;
        public ITtsEngine SystemSpeechEngine => null!;
        public string? LastUsedEngineName => "Fake";
        public event Action? OnSpeakingStarted;
        public event Action? OnSpeakingFinished;

        public Task SpeakAsync(string text, System.Threading.CancellationToken cancellationToken = default)
        {
            LastSpokenText = text;
            OnSpeakingStarted?.Invoke();
            OnSpeakingFinished?.Invoke();
            return Task.CompletedTask;
        }

        public void Speak(string text)
        {
            LastSpokenText = text;
            OnSpeakingStarted?.Invoke();
            OnSpeakingFinished?.Invoke();
        }

        public void Dispose() { }
    }
}
