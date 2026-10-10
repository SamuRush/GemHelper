using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public class TtsFailoverTests
{
    private sealed class SimulatedTimeoutEdgeTtsEngine : ITtsEngine
    {
        private readonly int _delayMs;
        private readonly Exception _exceptionToThrow;

        public string Name => "Edge";
        public bool IsAvailable { get; }

        public SimulatedTimeoutEdgeTtsEngine(int delayMs, Exception exceptionToThrow, bool isAvailable = true)
        {
            _delayMs = delayMs;
            _exceptionToThrow = exceptionToThrow;
            IsAvailable = isAvailable;
        }

        public async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, ct);
            }
            throw _exceptionToThrow;
        }
    }

    private sealed class MockSystemSpeechTtsEngine : ITtsEngine
    {
        public string Name => "System.Speech";
        public bool IsAvailable => true;
        public int SpeakCount { get; private set; }
        public string? LastSpokenText { get; private set; }

        public Task SpeakAsync(string text, CancellationToken ct = default)
        {
            SpeakCount++;
            LastSpokenText = text;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void EdgeTtsEngine_DefaultConnectionTimeout_MustBe1500Ms()
    {
        // Technical requirement 1: ConnectionTimeoutMs must be reduced to 1500 ms
        Assert.Equal(1500, EdgeTtsEngine.DefaultConnectionTimeoutMs);
    }

    [Fact]
    public async Task WhenEdgeTtsThrowsSocketException_FailoverToSapi5MustCompleteWithin1600Ms()
    {
        // Technical requirement 4: simulate socket timeout/abort and verify fallback to SAPI5 within <= 1.6s
        var mockEdge = new SimulatedTimeoutEdgeTtsEngine(
            delayMs: 1200,
            exceptionToThrow: new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Remote socket closed prematurely")
        );
        var mockSapi5 = new MockSystemSpeechTtsEngine();
        using var composite = new CompositeVoiceFeedbackService(null, mockEdge, mockSapi5, enableEdgeTts: true);

        var sw = Stopwatch.StartNew();
        // Must NOT throw unhandled exceptions
        await composite.SpeakAsync("Системное уведомление");
        sw.Stop();

        Assert.Equal(1, mockSapi5.SpeakCount);
        Assert.Equal("Системное уведомление", mockSapi5.LastSpokenText);
        Assert.Equal("System.Speech", composite.LastUsedEngineName);
        Assert.True(sw.ElapsedMilliseconds <= 1600,
            $"Failover to SAPI5 must take <= 1600 ms, but took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WhenNetworkIsUnavailable_FailoverToSapi5MustBeInstant()
    {
        var mockEdge = new SimulatedTimeoutEdgeTtsEngine(
            delayMs: 0,
            exceptionToThrow: new InvalidOperationException("No network"),
            isAvailable: false
        );
        var mockSapi5 = new MockSystemSpeechTtsEngine();
        using var composite = new CompositeVoiceFeedbackService(null, mockEdge, mockSapi5, enableEdgeTts: true);

        var sw = Stopwatch.StartNew();
        await composite.SpeakAsync("Быстрый откат при отсутствии сети");
        sw.Stop();

        Assert.Equal(1, mockSapi5.SpeakCount);
        Assert.Equal("System.Speech", composite.LastUsedEngineName);
        Assert.True(sw.ElapsedMilliseconds <= 500,
            $"Instant failover without network must take <= 500 ms, took {sw.ElapsedMilliseconds} ms");
    }
}
