using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace Gem.Services;

/// <summary>
/// Primary TTS engine utilizing Microsoft Edge Neural Text-to-Speech protocol (ru-RU-DmitryNeural).
/// Streams audio over WebSocket and plays via NAudio with a 2500ms timeout.
/// Includes fast reconnect mechanism (1 quick retry with 400ms timeout) before falling back to Silero,
/// and WebSocket Keep-Alive ping maintenance.
/// </summary>
public sealed class EdgeTtsEngine : ITtsEngine, IDisposable
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string SecMsGecVersion = "1-143.0.3650.75";
    private const string ChromiumUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
    private const string EdgeExtensionOrigin = "chrome-extension://jdiccldimpdaibmpdkgikmbggipbghpp";

    public const int DefaultConnectionTimeoutMs = 2500;
    public const int FastReconnectTimeoutMs = 400;

    public int ConnectionTimeoutMs { get; set; } = DefaultConnectionTimeoutMs;

    private readonly string _voice;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsLock = new(1, 1);
    private readonly CancellationTokenSource _keepAliveCts = new();
    private bool _disposed = false;

    public string Name => "Edge";
    public string Voice => _voice;

    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();

    public EdgeTtsEngine(string? voice = null, int timeoutMs = DefaultConnectionTimeoutMs)
    {
        _voice = string.IsNullOrWhiteSpace(voice) ? "ru-RU-DmitryNeural" : voice;
        ConnectionTimeoutMs = timeoutMs > 0 ? timeoutMs : DefaultConnectionTimeoutMs;

        StartKeepAliveLoop();
    }

    /// <summary>
    /// Generates the DRM Sec-MS-GEC token required by the Microsoft Edge TTS WebSocket endpoint.
    /// </summary>
    public static string GenerateSecMsGec()
    {
        const long winEpoch = 11644473600L;
        long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long windowsSeconds = unixSeconds + winEpoch;
        windowsSeconds -= (windowsSeconds % 300);
        long windowsTicks = windowsSeconds * 10_000_000L;
        string combined = windowsTicks.ToString() + TrustedClientToken;
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(combined));
        return Convert.ToHexString(hash);
    }

    private void ResetClient()
    {
        try
        {
            if (_ws != null)
            {
                if (_ws.State == WebSocketState.Open)
                {
                    _ws.Abort();
                }
                _ws.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EdgeTtsEngine] Ошибка при сбросе WebSocket: {ex.Message}");
        }
        finally
        {
            _ws = null;
        }
    }

    private void StartKeepAliveLoop()
    {
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!_keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(_keepAliveCts.Token);

                    if (_ws != null && _ws.State == WebSocketState.Open)
                    {
                        // Lightweight keep-alive ping frame to prevent server idle disconnect
                        await _wsLock.WaitAsync(_keepAliveCts.Token);
                        try
                        {
                            if (_ws != null && _ws.State == WebSocketState.Open)
                            {
                                var pingMsg = Encoding.UTF8.GetBytes("Path:ping\r\n\r\n");
                                using var pingCts = new CancellationTokenSource(1000);
                                await _ws.SendAsync(new ArraySegment<byte>(pingMsg), WebSocketMessageType.Text, true, pingCts.Token);
                            }
                        }
                        catch
                        {
                            // Reset silently on ping error; next speak call will reconnect
                            ResetClient();
                        }
                        finally
                        {
                            _wsLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EdgeTtsEngine KeepAlive] {ex.Message}");
                }
            }
        }, _keepAliveCts.Token);
    }

    private async Task EnsureConnectedAsync(int timeoutMs, CancellationToken ct)
    {
        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            return;
        }

        ResetClient();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("User-Agent", ChromiumUserAgent);
        _ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br");
        _ws.Options.SetRequestHeader("Accept-Language", "ru,en-US,en;q=0.9");
        _ws.Options.SetRequestHeader("Pragma", "no-cache");
        _ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        _ws.Options.SetRequestHeader("Origin", EdgeExtensionOrigin);
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        string secMsGec = GenerateSecMsGec();
        string connectionId = Guid.NewGuid().ToString("N");
        string wssUrl = $"wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken={TrustedClientToken}&Sec-MS-GEC={secMsGec}&Sec-MS-GEC-Version={SecMsGecVersion}&ConnectionId={connectionId}";

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(timeoutMs);

        try
        {
            await _ws.ConnectAsync(new Uri(wssUrl), connectCts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            ResetClient();
            throw new TimeoutException($"Таймаут подключения к Edge-TTS ({timeoutMs} мс) превышен.", ex);
        }
        catch
        {
            ResetClient();
            throw;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!IsAvailable)
        {
            throw new InvalidOperationException("Сетевое подключение недоступно для Edge-TTS.");
        }

        await _wsLock.WaitAsync(ct);
        try
        {
            int baseTimeout = ConnectionTimeoutMs > 0 ? ConnectionTimeoutMs : DefaultConnectionTimeoutMs;

            // 1 попытка стандартная + 1 быстрый Reconnect (400 мс) перед сбросом на Silero
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int currentTimeout = (attempt == 0) ? baseTimeout : FastReconnectTimeoutMs;

                try
                {
                    using var mp3Stream = await SynthesizeToMp3StreamAsync(text, currentTimeout, ct);
                    mp3Stream.Position = 0;
                    await PlayMp3StreamAsync(mp3Stream, ct);
                    return;
                }
                catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: Edge] Быстрый Reconnect (таймаут {FastReconnectTimeoutMs} мс) перед сбросом на Silero... Причина: {ex.Message}");
                    Console.ResetColor();

                    ResetClient();
                    // loop continues to attempt = 1 (Fast Reconnect)
                }
                catch (Exception)
                {
                    ResetClient();
                    throw;
                }
            }
        }
        finally
        {
            _wsLock.Release();
        }
    }

    private async Task<MemoryStream> SynthesizeToMp3StreamAsync(string text, int timeoutMs, CancellationToken ct)
    {
        await EnsureConnectedAsync(timeoutMs, ct);

        // 1. Send speech.config message
        string configMessage = "Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        byte[] configBytes = Encoding.UTF8.GetBytes(configMessage);

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(timeoutMs);

        await _ws!.SendAsync(new ArraySegment<byte>(configBytes), WebSocketMessageType.Text, true, sendCts.Token);

        // 2. Send SSML message
        string requestId = Guid.NewGuid().ToString("N");
        string escapedText = SecurityElement.Escape(text);
        string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'><voice name='{_voice}'>{escapedText}</voice></speak>";
        string ssmlMessage = $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n{ssml}";
        byte[] ssmlBytes = Encoding.UTF8.GetBytes(ssmlMessage);

        await _ws.SendAsync(new ArraySegment<byte>(ssmlBytes), WebSocketMessageType.Text, true, sendCts.Token);

        // 3. Receive audio stream
        var mp3Stream = new MemoryStream();
        byte[] receiveBuffer = new byte[8192];
        bool receivedFirstAudio = false;

        using var firstAudioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstAudioCts.CancelAfter(timeoutMs);

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var frameStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                var receiveToken = receivedFirstAudio ? ct : firstAudioCts.Token;
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), receiveToken);
                frameStream.Write(receiveBuffer, 0, result.Count);
            } while (!result.EndOfMessage);

            byte[] frameBytes = frameStream.ToArray();

            if (result.MessageType == WebSocketMessageType.Close)
            {
                ResetClient();
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string textMsg = Encoding.UTF8.GetString(frameBytes);
                if (textMsg.Contains("Path:turn.end", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            else if (result.MessageType == WebSocketMessageType.Binary && frameBytes.Length > 2)
            {
                int headerLen = (frameBytes[0] << 8) | frameBytes[1];
                int audioOffset = 2 + headerLen;
                if (frameBytes.Length > audioOffset)
                {
                    mp3Stream.Write(frameBytes, audioOffset, frameBytes.Length - audioOffset);
                    receivedFirstAudio = true;
                }
            }
        }

        if (mp3Stream.Length == 0)
        {
            ResetClient();
            throw new InvalidOperationException("Edge-TTS не вернул аудиоданные.");
        }

        return mp3Stream;
    }

    private static async Task PlayMp3StreamAsync(MemoryStream mp3Stream, CancellationToken ct)
    {
        WaveStream waveStream;
        try
        {
            waveStream = new StreamMediaFoundationReader(mp3Stream);
        }
        catch (Exception ex1)
        {
            try
            {
                mp3Stream.Position = 0;
                waveStream = new Mp3FileReader(mp3Stream);
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException($"Ошибка декодирования MP3 Edge-TTS (MediaFoundation: {ex1.Message}; Mp3: {ex2.Message})", ex2);
            }
        }

        using (waveStream)
        using (var waveOut = new WaveOutEvent())
        {
            waveOut.Init(waveStream);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
            {
                await Task.Delay(20, ct);
            }

            if (ct.IsCancellationRequested)
            {
                waveOut.Stop();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _keepAliveCts.Cancel();
        _keepAliveCts.Dispose();
        ResetClient();
        _wsLock.Dispose();
    }
}
