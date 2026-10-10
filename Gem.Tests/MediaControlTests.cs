using System.Diagnostics;
using System.Text.Json;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class MediaControlTests
{
    private class FakeMediaSessionService : IMediaSessionService
    {
        public MediaPlaybackStatus CurrentStatus { get; set; } = MediaPlaybackStatus.Unknown;
        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public int NextCount { get; private set; }
        public int PrevCount { get; private set; }
        public int PlayMusicCount { get; private set; }
        public string? LastMusicApp { get; private set; }
        public int ControlVideoCount { get; private set; }
        public bool? LastVideoPause { get; private set; }

        public Task<MediaPlaybackStatus> GetPlaybackStatusAsync()
        {
            return Task.FromResult(CurrentStatus);
        }

        public Task<bool> PlayAsync(CancellationToken cancellationToken = default)
        {
            PlayCount++;
            CurrentStatus = MediaPlaybackStatus.Playing;
            return Task.FromResult(true);
        }

        public Task<bool> PauseAsync(CancellationToken cancellationToken = default)
        {
            PauseCount++;
            CurrentStatus = MediaPlaybackStatus.Paused;
            return Task.FromResult(true);
        }

        public Task<bool> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            CurrentStatus = MediaPlaybackStatus.Stopped;
            return Task.FromResult(true);
        }

        public Task<bool> NextTrackAsync(CancellationToken cancellationToken = default)
        {
            NextCount++;
            return Task.FromResult(true);
        }

        public Task<bool> PreviousTrackAsync(CancellationToken cancellationToken = default)
        {
            PrevCount++;
            return Task.FromResult(true);
        }

        public Task<bool> PlayMusicAsync(string appOrTarget = "yandexmusic", CancellationToken cancellationToken = default)
        {
            PlayMusicCount++;
            LastMusicApp = appOrTarget;
            CurrentStatus = MediaPlaybackStatus.Playing;
            return Task.FromResult(true);
        }

        public Task<bool> ControlVideoAsync(bool pause, CancellationToken cancellationToken = default)
        {
            ControlVideoCount++;
            LastVideoPause = pause;
            CurrentStatus = pause ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
            return Task.FromResult(true);
        }
    }

    [Theory]
    [InlineData("останови видео", "pause_video")]
    [InlineData("видео на паузу", "pause_video")]
    [InlineData("пауза видео", "pause_video")]
    [InlineData("продолжи видео", "resume_video")]
    [InlineData("включи видео", "resume_video")]
    [InlineData("включи фильм", "resume_video")]
    [InlineData("включи ролик", "resume_video")]
    [InlineData("включи сериал", "resume_video")]
    [InlineData("видео плей", "resume_video")]
    [InlineData("возобнови видео", "resume_video")]
    [InlineData("включи яндекс музыку", "play_music")]
    [InlineData("запусти яндекс музыку", "play_music")]
    [InlineData("яндекс музыка", "play_music")]
    [InlineData("включи музыку", "play_music")]
    [InlineData("поставь музыку", "play_music")]
    [InlineData("запусти музыку", "play_music")]
    [InlineData("поставь на паузу", "pause")]
    [InlineData("пауза", "pause")]
    [InlineData("следующий трек", "next")]
    [InlineData("следующая песня", "next")]
    [InlineData("следующий", "next")]
    [InlineData("следующее видео", "next")]
    [InlineData("переключи следующий", "next")]
    [InlineData("переключи на следующий", "next")]
    [InlineData("включи следующий", "next")]
    [InlineData("скипни", "next")]
    [InlineData("скип", "next")]
    [InlineData("пропусти трек", "next")]
    [InlineData("предыдущий трек", "prev")]
    [InlineData("предыдущая песня", "prev")]
    [InlineData("предыдущий", "prev")]
    [InlineData("предыдущее видео", "prev")]
    [InlineData("переключи предыдущий", "prev")]
    [InlineData("переключи на предыдущий", "prev")]
    [InlineData("включи предыдущий", "prev")]
    public void TryFastMatch_MediaPhrases_AreMappedToDistinctContracts(string phrase, string expectedAction)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("media", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());
    }

    [Fact]
    public void TryFastMatch_YandexMusicCommand_SpecifiesTargetApp()
    {
        var response = LlmIntentService.TryFastMatch("включи яндекс музыку");

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal("play_music", actionProp.GetString());

        Assert.True(args.TryGetProperty("app", out var appProp));
        Assert.Equal("yandexmusic", appProp.GetString());
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPauseVideo_ToControlVideoAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "pause_video" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.ControlVideoCount);
        Assert.True(fakeSession.LastVideoPause);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesResumeVideo_ToControlVideoAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "resume_video" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.ControlVideoCount);
        Assert.False(fakeSession.LastVideoPause);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPlayVideo_ToControlVideoAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "play_video" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.ControlVideoCount);
        Assert.False(fakeSession.LastVideoPause);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPlayMusic_ToPlayMusicAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "play_music", "app": "yandexmusic" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PlayMusicCount);
        Assert.Equal("yandexmusic", fakeSession.LastMusicApp);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPause_ToPauseAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "pause" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PauseCount);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPlay_ToPlayAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "play" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PlayCount);
    }

    [Fact]
    public async Task AppControlCommandHandler_MediaAlias_DelegatesToMediaSessionService()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new AppControlCommandHandler(mediaSessionService: fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "start", "name": "яндекс музыка" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PlayMusicCount);
        Assert.Equal("yandexmusic", fakeSession.LastMusicApp);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("browser")]
    [InlineData("opera")]
    [InlineData("brave")]
    [InlineData("vlc")]
    [InlineData("mpc-hc")]
    [InlineData("mpc-be")]
    [InlineData("potplayer")]
    public void MediaSessionService_IsVideoWindowTarget_IdentifiesTargetProcesses(string processName)
    {
        bool isTarget = MediaSessionService.IsVideoProcessName(processName);
        Assert.True(isTarget, $"Process '{processName}' must be identified as video target!");
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("calc")]
    [InlineData("explorer")]
    [InlineData("steam")]
    [InlineData("discord")]
    public void MediaSessionService_IsVideoWindowTarget_RejectsNonVideoProcesses(string processName)
    {
        bool isTarget = MediaSessionService.IsVideoProcessName(processName);
        Assert.False(isTarget, $"Process '{processName}' must not be identified as video target!");
    }

    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up - YouTube - Google Chrome")]
    [InlineData("ВК Видео - Смотреть сериалы онлайн - Браузер")]
    [InlineData("Фильм онлайн - Кинопоиск")]
    [InlineData("Dota 2 Championship - Twitch - Brave")]
    [InlineData("RuTube - Популярное видео")]
    [InlineData("matrix_reloaded.mp4 - VLC media player")]
    [InlineData("interstellar.mkv")]
    public void MediaSessionService_IsVideoTitle_IdentifiesVideoTitles(string title)
    {
        bool isVideo = MediaSessionService.IsVideoTitle(title);
        Assert.True(isVideo, $"Title '{title}' must be recognized as video title!");
    }

    [Theory]
    [InlineData("Новая вкладка - Google Chrome")]
    [InlineData("Google Chrome")]
    [InlineData("Новая вкладка - Brave")]
    [InlineData("Поиск в Google - Mozilla Firefox")]
    [InlineData("Входящие (5) - Почта - Яндекс Браузер")]
    [InlineData("GitHub - Google/Antigravity")]
    [InlineData("")]
    [InlineData("   ")]
    public void MediaSessionService_IsVideoTitle_RejectsNonVideoTitles(string title)
    {
        bool isVideo = MediaSessionService.IsVideoTitle(title);
        Assert.False(isVideo, $"Title '{title}' must NOT be recognized as video title!");
    }

    [Theory]
    [InlineData("brave", true, false)]
    [InlineData("chrome", true, false)]
    [InlineData("msedge", true, false)]
    [InlineData("firefox", true, false)]
    [InlineData("vlc", false, true)]
    [InlineData("mpc-hc", false, true)]
    [InlineData("potplayer", false, true)]
    public void MediaSessionService_DistinguishesBrowserAndMediaPlayerProcesses(string procName, bool expectedBrowser, bool expectedPlayer)
    {
        Assert.Equal(expectedBrowser, MediaSessionService.IsBrowserProcessName(procName));
        Assert.Equal(expectedPlayer, MediaSessionService.IsMediaPlayerProcessName(procName));
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesStop_ToStopAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "stop" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.StopCount);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesNext_ToNextTrackAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "next" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.NextCount);
    }

    [Fact]
    public async Task MediaCommandHandler_RoutesPrev_ToPreviousTrackAsync()
    {
        var fakeSession = new FakeMediaSessionService();
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "prev" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PrevCount);
    }

    [Fact]
    public async Task MediaCommandHandler_SmartPlayPause_WhenPlaying_CallsPause()
    {
        var fakeSession = new FakeMediaSessionService { CurrentStatus = MediaPlaybackStatus.Playing };
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "play_pause" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PauseCount);
        Assert.Equal(0, fakeSession.PlayCount);
    }

    [Fact]
    public async Task MediaCommandHandler_SmartPlayPause_WhenPaused_CallsPlay()
    {
        var fakeSession = new FakeMediaSessionService { CurrentStatus = MediaPlaybackStatus.Paused };
        var handler = new MediaCommandHandler(fakeSession);

        var argsDoc = JsonDocument.Parse("""{ "action": "play_pause" }""");
        var result = await handler.ExecuteAsync(argsDoc.RootElement);

        Assert.True(result.Success);
        Assert.Equal(1, fakeSession.PlayCount);
        Assert.Equal(0, fakeSession.PauseCount);
    }

    [Fact]
    public void MediaSessionService_MusicProcessNames_ContainsYandexMusicIdentifiers()
    {
        var names = MediaSessionService.MusicProcessNames;
        Assert.Contains("yandexmusic", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("YandexMusic", names, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("останови видео")]
    [InlineData("продолжи видео")]
    [InlineData("поставь на паузу")]
    [InlineData("включи яндекс музыку")]
    public void TryFastMatch_NewMediaContracts_LatencyMustBeUnder5Ms(string phrase)
    {
        var sw = Stopwatch.StartNew();
        var response = LlmIntentService.TryFastMatch(phrase);
        sw.Stop();

        Assert.NotNull(response);
        Assert.True(sw.Elapsed.TotalMilliseconds < 5.0,
            $"TryFastMatch('{phrase}') took {sw.Elapsed.TotalMilliseconds:F3} ms, exceeding 5ms threshold!");
    }
}
