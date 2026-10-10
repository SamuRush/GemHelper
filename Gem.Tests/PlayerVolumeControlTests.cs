using System.Text.Json;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class PlayerVolumeControlTests
{
    private class FakeAppAudioService : IAppAudioService
    {
        public int SetCount { get; private set; }
        public int ChangeCount { get; private set; }
        public int MuteCount { get; private set; }
        public int GetCount { get; private set; }

        public string? LastTarget { get; private set; }
        public float LastScalar { get; private set; }
        public float LastDelta { get; private set; }
        public bool? LastMute { get; private set; }

        public Task<AppVolumeResult> SetAppVolumeAsync(string target, float scalar)
        {
            SetCount++;
            LastTarget = target;
            LastScalar = scalar;
            int level = (int)(scalar * 100);
            return Task.FromResult(new AppVolumeResult(true, $"Volume set to {level}%.", level, scalar, false, "FakeApp"));
        }

        public Task<AppVolumeResult> ChangeAppVolumeAsync(string target, float deltaScalar)
        {
            ChangeCount++;
            LastTarget = target;
            LastDelta = deltaScalar;
            return Task.FromResult(new AppVolumeResult(true, "Volume changed.", 60, 0.6f, false, "FakeApp"));
        }

        public Task<AppVolumeResult> SetAppMuteAsync(string target, bool? isMuted)
        {
            MuteCount++;
            LastTarget = target;
            LastMute = isMuted;
            return Task.FromResult(new AppVolumeResult(true, "Mute updated.", 50, 0.5f, isMuted ?? true, "FakeApp"));
        }

        public Task<AppVolumeResult> GetAppVolumeAsync(string target)
        {
            GetCount++;
            LastTarget = target;
            return Task.FromResult(new AppVolumeResult(true, "Current volume.", 50, 0.5f, false, "FakeApp"));
        }
    }

    [Theory]
    [InlineData("громкость музыки 50", "set", "player", 50)]
    [InlineData("звук музыки 60", "set", "player", 60)]
    [InlineData("громкость плеера 70", "set", "player", 70)]
    [InlineData("звук в плеере 80", "set", "player", 80)]
    [InlineData("громкость видео 40", "set", "video", 40)]
    [InlineData("звук видео 30", "set", "video", 30)]
    public void TryFastMatch_PlayerVolumeSet_RoutesWithTarget(
        string phrase, string expectedAction, string expectedTarget, int expectedLevel)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("volume", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());

        Assert.True(args.TryGetProperty("target", out var targetProp));
        Assert.Equal(expectedTarget, targetProp.GetString());

        Assert.True(args.TryGetProperty("level", out var levelProp));
        Assert.Equal(expectedLevel, levelProp.GetInt32());
    }

    [Theory]
    [InlineData("музыка громче", "change", "player", 10)]
    [InlineData("сделай музыку громче", "change", "player", 10)]
    [InlineData("прибавь музыку", "change", "player", 10)]
    [InlineData("плеер громче", "change", "player", 10)]
    [InlineData("музыка тише", "change", "player", -10)]
    [InlineData("сделай музыку тише", "change", "player", -10)]
    [InlineData("убавь музыку", "change", "player", -10)]
    [InlineData("плеер тише", "change", "player", -10)]
    [InlineData("видео громче", "change", "video", 10)]
    [InlineData("видео тише", "change", "video", -10)]
    public void TryFastMatch_PlayerVolumeChange_RoutesWithTargetAndDelta(
        string phrase, string expectedAction, string expectedTarget, int expectedDelta)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("volume", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());

        Assert.True(args.TryGetProperty("target", out var targetProp));
        Assert.Equal(expectedTarget, targetProp.GetString());

        Assert.True(args.TryGetProperty("delta", out var deltaProp));
        Assert.Equal(expectedDelta, deltaProp.GetInt32());
    }

    [Theory]
    [InlineData("выключи звук в плеере", "mute", "player", true)]
    [InlineData("включи звук в плеере", "mute", "player", false)]
    [InlineData("выключи звук видео", "mute", "video", true)]
    [InlineData("включи звук видео", "mute", "video", false)]
    public void TryFastMatch_PlayerVolumeMute_RoutesWithTarget(
        string phrase, string expectedAction, string expectedTarget, bool expectedMute)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("volume", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());

        Assert.True(args.TryGetProperty("target", out var targetProp));
        Assert.Equal(expectedTarget, targetProp.GetString());

        Assert.True(args.TryGetProperty("isMuted", out var muteProp));
        Assert.Equal(expectedMute, muteProp.GetBoolean());
    }

    [Theory]
    [InlineData("громкость 50")]
    [InlineData("сделай тише")]
    [InlineData("прибавь звук")]
    [InlineData("выключи звук")]
    public void TryFastMatch_MasterVolume_DoesNotIncludePlayerTarget(string phrase)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("volume", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        bool hasTarget = args.TryGetProperty("target", out var targetProp);
        Assert.True(!hasTarget || targetProp.GetString() == null || targetProp.GetString() == "master",
            "General volume commands must target master or omit target!");
    }

    [Fact]
    public async Task VolumeCommandHandler_WithPlayerTarget_DelegatesToAppAudioService()
    {
        var fakeAppAudio = new FakeAppAudioService();
        var handler = new VolumeCommandHandler(fakeAppAudio);

        var argsDoc = JsonDocument.Parse("""{ "action": "set", "level": 75, "target": "player" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeAppAudio.SetCount);
        Assert.Equal("player", fakeAppAudio.LastTarget);
        Assert.Equal(0.75f, fakeAppAudio.LastScalar);
    }

    [Fact]
    public async Task VolumeCommandHandler_WithPlayerChange_DelegatesToAppAudioService()
    {
        var fakeAppAudio = new FakeAppAudioService();
        var handler = new VolumeCommandHandler(fakeAppAudio);

        var argsDoc = JsonDocument.Parse("""{ "action": "change", "delta": -15, "target": "player" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeAppAudio.ChangeCount);
        Assert.Equal("player", fakeAppAudio.LastTarget);
        Assert.Equal(-0.15f, fakeAppAudio.LastDelta, 3);
    }

    [Fact]
    public async Task VolumeCommandHandler_WithPlayerMute_DelegatesToAppAudioService()
    {
        var fakeAppAudio = new FakeAppAudioService();
        var handler = new VolumeCommandHandler(fakeAppAudio);

        var argsDoc = JsonDocument.Parse("""{ "action": "mute", "isMuted": true, "target": "player" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeAppAudio.MuteCount);
        Assert.Equal("player", fakeAppAudio.LastTarget);
        Assert.True(fakeAppAudio.LastMute);
    }

    [Fact]
    public void AppAudioService_IsMusicProcess_MatchesKnownMusicPlayers()
    {
        Assert.True(AppAudioService.IsMusicProcessName("Y.Music"));
        Assert.True(AppAudioService.IsMusicProcessName("YandexMusic"));
        Assert.True(AppAudioService.IsMusicProcessName("yandexmusic"));
        Assert.True(AppAudioService.IsMusicProcessName("spotify"));
        Assert.True(AppAudioService.IsMusicProcessName("aimp"));
    }

    [Fact]
    public void AppAudioService_IsMusicProcess_RejectsUnrelatedProcesses()
    {
        Assert.False(AppAudioService.IsMusicProcessName("notepad"));
        Assert.False(AppAudioService.IsMusicProcessName("explorer"));
        Assert.False(AppAudioService.IsMusicProcessName("calc"));
    }

    [Theory]
    [InlineData("YandexMusic.exe", "player", true)]
    [InlineData("Y.Music.exe", "player", true)]
    [InlineData("yandexmusic", "music", true)]
    [InlineData("spotify.exe", "player", true)]
    [InlineData("aimp", "music", true)]
    [InlineData("chrome.exe", "video", true)]
    [InlineData("vlc.exe", "video", true)]
    [InlineData("notepad.exe", "player", false)]
    [InlineData("calc.exe", "video", false)]
    public void AppAudioService_IsProcessNameMatchingTarget_EvaluatesCorrectly(
        string processName, string target, bool expectedMatch)
    {
        bool result = AppAudioService.IsProcessNameMatchingTarget(processName, target);
        Assert.Equal(expectedMatch, result);
    }

    [Fact]
    public void AppAudioService_GetTargetFamilyPids_ReturnsValidSet()
    {
        var pids = AppAudioService.GetTargetFamilyPids("player");
        Assert.NotNull(pids);
    }

    [Fact]
    public void AppAudioService_FindSessions_DoesNotThrow()
    {
        var exception = Record.Exception(() => AppAudioService.FindSessions("player"));
        Assert.Null(exception);
    }

    [Fact]
    public void MediaKeyService_SendCtrlKeyCombination_ExecutesSuccessfully()
    {
        bool upResult = MediaKeyService.SendCtrlKeyCombination(Gem.Win32.NativeConstants.VK_UP);
        bool downResult = MediaKeyService.SendCtrlKeyCombination(Gem.Win32.NativeConstants.VK_DOWN);

        Assert.True(upResult);
        Assert.True(downResult);
    }

    [Fact]
    public void AppAudioService_FindYandexMusicWindow_ExecutesWithoutExceptions()
    {
        var exception = Record.Exception(() => AppAudioService.FindYandexMusicWindow(out _));
        Assert.Null(exception);
    }
}
