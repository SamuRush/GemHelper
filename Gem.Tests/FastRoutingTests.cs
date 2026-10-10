using System;
using System.Diagnostics;
using System.Text.Json;
using Gem.Core;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class FastRoutingTests
{
    static FastRoutingTests()
    {
        // Comprehensive JIT warmup for all regexes and fast matching branches
        _ = LlmIntentService.TryFastMatch("пауза");
        _ = LlmIntentService.TryFastMatch("следующий трек");
        _ = LlmIntentService.TryFastMatch("громкость 50");
        _ = LlmIntentService.TryFastMatch("поставь таймер на 1 минуту");
        _ = LlmIntentService.TryFastMatch("открой блокнот");
        _ = LlmIntentService.TryFastMatch("диспетчер задач");
        _ = LlmIntentService.TryFastMatch("погода");
        _ = LlmIntentService.TryFastMatch("закройся");
    }

    public FastRoutingTests()
    {
        // Ensure clean state before each test
        JarvisOrchestrator.Instance.ResetConfirmationState();
    }

    [Theory]
    [InlineData("пауза")]
    [InlineData("следующий трек")]
    [InlineData("громкость 50")]
    [InlineData("сделай тише")]
    [InlineData("выключи звук")]
    [InlineData("открой блокнот")]
    [InlineData("закрой калькулятор")]
    [InlineData("открой браузер")]
    [InlineData("диспетчер задач")]
    [InlineData("сверни все")]
    [InlineData("скриншот")]
    [InlineData("погода")]
    [InlineData("поставь таймер на 5 минут")]
    [InlineData("закройся")]
    [InlineData("погода в москве")] // Should defer to LLM
    [InlineData("расскажи шутку")]   // Should defer to LLM
    public void TryFastMatch_ExecutionLatency_MustBeStrictlyUnder5Ms(string phrase)
    {
        var sw = Stopwatch.StartNew();
        var response = LlmIntentService.TryFastMatch(phrase);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalMilliseconds < 5.0,
            $"TryFastMatch('{phrase}') took {sw.Elapsed.TotalMilliseconds:F3} ms, which exceeds the 5 ms latency threshold!");
    }

    [Fact]
    public void TryFastMatch_BatchProcessing_AveragesWellUnder5Ms()
    {
        string[] phrases = [
            "пауза", "следующий трек", "предыдущий трек", "поставь на паузу",
            "громкость 20", "тише", "громче", "без звука",
            "открой блокнот", "закрой блокнот", "открой калькулятор", "открой браузер",
            "диспетчер задач", "сверни все", "скриншот", "скопируй", "вставь",
            "погода", "таймер на 10 секунд", "отмени таймер", "закройся", "выход"
        ];

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < phrases.Length; i++)
        {
            _ = LlmIntentService.TryFastMatch(phrases[i]);
        }
        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double avgMs = totalMs / phrases.Length;

        Assert.True(avgMs < 1.0, $"Average latency across batch ({avgMs:F3} ms) must be < 1.0 ms!");
        Assert.True(totalMs < 50.0, $"Total batch duration ({totalMs:F3} ms) must be < 50 ms!");
    }

    [Theory]
    [InlineData("открой блокнот", "app", "start", "notepad")]
    [InlineData("закрой блокнот", "app", "close", "notepad")]
    [InlineData("открой калькулятор", "app", "start", "calc")]
    [InlineData("закрой калькулятор", "app", "close", "calc")]
    [InlineData("открой браузер", "app", "start", "browser")]
    [InlineData("закрой браузер", "app", "close", "browser")]
    [InlineData("открой проводник", "app", "start", "explorer")]
    [InlineData("закрой проводник", "app", "close", "explorer")]
    [InlineData("дискорд", "app", "start", "discord")]
    [InlineData("телеграм", "app", "start", "telegram")]
    public void SystemAppCommands_AreIsolatedFromSteam_AndRoutedToTargetApp(
        string phrase, string expectedCommand, string expectedAction, string expectedAppName)
    {
        // Invariant: System apps (Notepad, Calc, Browser, etc.) must NEVER be treated as Steam games
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal(expectedCommand, response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());

        Assert.True(args.TryGetProperty("name", out var nameProp));
        Assert.Equal(expectedAppName, nameProp.GetString());
    }

    [Theory]
    [InlineData("пауза", "pause")]
    [InlineData("поставь на паузу", "pause")]
    [InlineData("останови видео", "pause_video")]
    [InlineData("продолжи видео", "resume_video")]
    [InlineData("включи яндекс музыку", "play_music")]
    [InlineData("следующий трек", "next")]
    [InlineData("предыдущий трек", "prev")]
    public void MediaCommands_AreIsolatedFromSteam_AndRoutedToMedia(string phrase, string expectedAction)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("media", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());
    }

    [Theory]
    [InlineData("громкость 50", "set")]
    [InlineData("сделай тише", "change")]
    [InlineData("прибавь звук", "change")]
    [InlineData("выключи звук", "mute")]
    public void VolumeCommands_AreIsolatedFromSteam_AndRoutedToVolume(string phrase, string expectedAction)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("volume", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.True(args.TryGetProperty("action", out var actionProp));
        Assert.Equal(expectedAction, actionProp.GetString());
    }

    [Theory]
    [InlineData("диспетчер задач")]
    [InlineData("сверни все")]
    [InlineData("скриншот")]
    [InlineData("скопируй")]
    [InlineData("вставь")]
    public void HotkeyCommands_AreIsolatedFromSteam_AndRoutedToHotkey(string phrase)
    {
        var response = LlmIntentService.TryFastMatch(phrase);

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("hotkey", response.CommandRequest.Command);
    }

    [Fact]
    public void SelfShutdownCommand_IsIsolatedFromSteam_AndRoutedToSystem()
    {
        var response = LlmIntentService.TryFastMatch("закройся");

        Assert.NotNull(response);
        Assert.NotNull(response.CommandRequest);
        Assert.Equal("system", response.CommandRequest.Command);

        var args = response.CommandRequest.Args;
        Assert.Equal("close", args.GetProperty("action").GetString());
        Assert.Equal("jarvis", args.GetProperty("name").GetString());
    }

    [Fact]
    public void WeatherCommand_IsIsolatedFromSteam_AndHandlesCityDelegation()
    {
        // Generic weather query routes to weather command directly
        var generalWeather = LlmIntentService.TryFastMatch("погода");
        Assert.NotNull(generalWeather);
        Assert.NotNull(generalWeather.CommandRequest);
        Assert.Equal("weather", generalWeather.CommandRequest.Command);

        // Weather with specific city must defer to LLM for NLP extraction without triggering Steam
        var cityWeather = LlmIntentService.TryFastMatch("погода в москве");
        Assert.Null(cityWeather);
    }

    [Fact]
    public void WithoutPendingConfirmation_AffirmativeOrNegativeReplies_DoNotTriggerSteam()
    {
        JarvisOrchestrator.Instance.ResetConfirmationState();
        Assert.False(JarvisOrchestrator.Instance.HasPendingAction);

        var yesResp = LlmIntentService.TryFastMatch("да");
        var noResp = LlmIntentService.TryFastMatch("нет");

        // Neither should trigger any Steam command
        Assert.True(yesResp == null || yesResp.CommandRequest == null);
        Assert.True(noResp == null || noResp.CommandRequest == null);
    }
}
