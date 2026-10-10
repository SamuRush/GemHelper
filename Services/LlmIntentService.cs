using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Core;
using Gem.Services.Routing;
using Microsoft.Extensions.Configuration;

namespace Gem.Services;

/// <summary>
/// Interprets spoken phrases and maps them into JarvisResponse via an OpenAI-compatible API.
/// </summary>
public sealed partial class LlmIntentService
{
    public const string SystemPrompt = LlmSystemPrompt.Prompt;

    // Dialog history: stores up to 6 last messages for conversational context
    private static readonly List<object> _dialogHistory = new();
    private const int MaxHistorySize = 6;

    /// <summary>
    /// Clears the dialog history, resetting the conversation context.
    /// </summary>
    public static void ClearHistory() => _dialogHistory.Clear();

    public event Action? OnThinkingStarted;
    public event Action? OnThinkingFinished;

    private readonly HttpClient _httpClient;
    private string _endpointUrl;
    private string _apiKey;
    private string _model;
    private readonly bool _debugMode;

    public string EndpointUrl => _endpointUrl;
    public string Model => _model;
    public string ApiKey => _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LlmIntentService(IConfiguration configuration, HttpClient? httpClient = null)
    {
        string rawBaseUrl = configuration["Llm:BaseUrl"] ?? "http://127.0.0.1:1234/v1";
        _endpointUrl = BuildEndpointUrl(rawBaseUrl);
        _apiKey = configuration["Llm:ApiKey"] ?? string.Empty;
        _model = configuration["Llm:Model"] ?? "qwen2.5-1.5b-instruct";
        _debugMode = !bool.TryParse(configuration["Llm:Debug"], out bool dbg) || dbg;

        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public LlmIntentService(
        string baseUrl,
        string apiKey,
        string model,
        bool useResponseFormat = false,
        bool debugMode = true,
        HttpClient? httpClient = null)
    {
        _endpointUrl = BuildEndpointUrl(baseUrl);
        _apiKey = apiKey;
        _model = model;
        _debugMode = debugMode;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void UpdateSettings(string baseUrl, string model, string? apiKey = null)
    {
        _endpointUrl = BuildEndpointUrl(baseUrl);
        _model = model;
        if (apiKey != null)
        {
            _apiKey = apiKey;
        }
    }

    /// <summary>
    /// Correctly forms the target endpoint URL.
    /// </summary>
    public static string BuildEndpointUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        string trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}/chat/completions";
        }

        return $"{trimmed}/v1/chat/completions";
    }

    /// <summary>
    /// Cleans incoming text from wake-word prefixes ("джарвис", "джар", "jarvis").
    /// </summary>
    public static string CleanPrefixes(string text) => RoutingHelper.CleanPrefixes(text);

    /// <summary>
    /// Interprets the spoken text into a CommandRequest and butler reply.
    /// </summary>
    public async Task<JarvisResponse> InterpretAsync(string spokenText, CancellationToken cancellationToken = default)
    {
        string cleanedSpokenText = CleanPrefixes(spokenText);

        if (string.IsNullOrWhiteSpace(cleanedSpokenText))
        {
            return new JarvisResponse(null, "Слушаю вас, сэр.");
        }

        // Process confirmation reply if awaiting user confirmation
        if (JarvisOrchestrator.Instance.HasPendingAction)
        {
            string cleanReply = cleanedSpokenText.ToLowerInvariant().Trim();

            if (JarvisOrchestrator.IsAffirmativeReply(cleanReply))
            {
                var action = JarvisOrchestrator.Instance.PendingAction;
                var game = JarvisOrchestrator.Instance.PendingGame!;
                JarvisOrchestrator.Instance.ResetConfirmationState();

                if (action == PendingGameAction.Uninstall)
                {
                    _ = Task.Run(async () =>
                    {
                        if (uint.TryParse(game.AppId, out uint numId))
                        {
                            await SteamService.Instance.UninstallGameAsync(numId, game.Title);
                        }
                    });
                    return new JarvisResponse(null, $"Удаляю {game.Name}, сэр.");
                }
                else if (action == PendingGameAction.Install)
                {
                    _ = Task.Run(async () => await SteamService.Instance.InstallGameAsync(game));
                    return new JarvisResponse(null, $"Принято. Начинаю установку {game.Name}, сэр.");
                }
                else
                {
                    SteamService.Instance.LaunchGame(game.AppId, out _);
                    return new JarvisResponse(null, $"Запускаю {game.Name}, сэр.");
                }
            }

            if (JarvisOrchestrator.IsNegativeReply(cleanReply))
            {
                var action = JarvisOrchestrator.Instance.PendingAction;
                JarvisOrchestrator.Instance.ResetConfirmationState();
                string reply = action == PendingGameAction.Uninstall ? "Удаление отменено, сэр." : "Понял, отменяю.";
                return new JarvisResponse(null, reply);
            }

            JarvisOrchestrator.Instance.ResetConfirmationState();
        }

        // Fast-path pattern matching: instantly executes clear deterministic commands (0ms latency, zero hallucinations)
        JarvisLog.Route("FastMatch", $"Анализ фразы: \"{cleanedSpokenText}\"...");

        var fastMatch = TryFastMatch(cleanedSpokenText);
        if (fastMatch != null)
        {
            JarvisLog.Info("Router: FastMatch", $"HIT -> Команда: '{fastMatch.CommandRequest?.Command ?? "dialog"}', Ответ: '{fastMatch.Reply}'");
            return fastMatch;
        }

        JarvisLog.Warn("Router: FastMatch", "MISS -> Перенаправление в LLM (InterpretAsync)...");

        if (_apiKey.Equals("YOUR_OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            JarvisLog.Warn("LLM", "ApiKey содержит плейсхолдер. Использую локальный резервный парсер.");
            return OfflineFallbackInterpreter(cleanedSpokenText);
        }

        OnThinkingStarted?.Invoke();
        string responseBody = string.Empty;
        try
        {
            if (_debugMode)
            {
                JarvisLog.Debug("LLM", $"Request URL: {_endpointUrl} | Model: {_model}");
            }

            var (success, statusCode, body) = await SendChatCompletionRequestAsync(cleanedSpokenText, cancellationToken);
            responseBody = body;

            if (!success)
            {
                JarvisLog.Error("LLM", $"API Error HTTP {statusCode}");
                return OfflineFallbackInterpreter(cleanedSpokenText);
            }

            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[LLM Raw Response]: {responseBody}");
                Console.ResetColor();
            }

            var result = ParseResponseContent(responseBody, cleanedSpokenText);

            if (!string.IsNullOrWhiteSpace(result.Reply))
            {
                _dialogHistory.Add(new { role = "user", content = cleanedSpokenText });
                _dialogHistory.Add(new { role = "assistant", content = result.Reply });
                while (_dialogHistory.Count > MaxHistorySize)
                {
                    _dialogHistory.RemoveAt(0);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            LogRawResponse(responseBody, $"[LLM] Исключение: {ex.Message}");
            return OfflineFallbackInterpreter(cleanedSpokenText);
        }
        finally
        {
            OnThinkingFinished?.Invoke();
        }
    }

    private async Task<(bool Success, int StatusCode, string Body)> SendChatCompletionRequestAsync(
        string spokenText,
        CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        messages.AddRange(_dialogHistory);
        messages.Add(new { role = "user", content = spokenText });

        var requestDict = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["temperature"] = 0.1,
            ["max_tokens"] = 250,
            ["messages"] = messages
        };

        string requestJson = JsonSerializer.Serialize(requestDict);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private JarvisResponse ParseResponseContent(string responseBody, string spokenText)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            LogRawResponse(responseBody, "Получено пустое тело ответа от LLM.");
            return OfflineFallbackInterpreter(spokenText);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                string errMsg = errorProp.ValueKind == JsonValueKind.Object && errorProp.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? errorProp.ToString()
                    : errorProp.ToString();

                LogRawResponse(responseBody, $"Ошибка от LLM сервера: {errMsg}");
                return OfflineFallbackInterpreter(spokenText);
            }

            if (!root.TryGetProperty("choices", out var choicesProp) ||
                choicesProp.ValueKind != JsonValueKind.Array ||
                choicesProp.GetArrayLength() == 0)
            {
                LogRawResponse(responseBody, "Отсутствует или пуст массив 'choices'.");
                return OfflineFallbackInterpreter(spokenText);
            }

            var firstChoice = choicesProp[0];

            if (!firstChoice.TryGetProperty("message", out var messageProp) ||
                messageProp.ValueKind != JsonValueKind.Object)
            {
                if (firstChoice.TryGetProperty("text", out var textProp))
                {
                    string fallbackRaw = CleanJsonMarkdown(textProp.GetString() ?? string.Empty);
                    return DeserializeJarvisResponse(fallbackRaw, spokenText);
                }

                LogRawResponse(responseBody, "В 'choices[0]' отсутствует объект 'message'.");
                return OfflineFallbackInterpreter(spokenText);
            }

            if (!messageProp.TryGetProperty("content", out var contentProp) ||
                contentProp.ValueKind != JsonValueKind.String)
            {
                LogRawResponse(responseBody, "В 'message' отсутствует строковое поле 'content'.");
                return OfflineFallbackInterpreter(spokenText);
            }

            string rawContent = contentProp.GetString() ?? string.Empty;
            string cleanedContent = CleanJsonMarkdown(rawContent);

            return DeserializeJarvisResponse(cleanedContent, spokenText);
        }
        catch (JsonException ex)
        {
            LogRawResponse(responseBody, $"Ошибка синтаксического анализа JSON ответа: {ex.Message}");
            return OfflineFallbackInterpreter(spokenText);
        }
    }

    private JarvisResponse DeserializeJarvisResponse(string json, string spokenText)
    {
        if (!json.StartsWith('{'))
        {
            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[LLM] Модель вернула чистый текст (не JSON), использую как reply: {json}");
                Console.ResetColor();
            }
            return new JarvisResponse(null, json);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<JarvisResponse>(json, JsonOptions);
            if (parsed != null && (!string.IsNullOrWhiteSpace(parsed.Reply) || parsed.CommandRequest != null))
            {
                return parsed;
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[LLM Content Parse Error]: {ex.Message} | Текст: {json}");
            Console.ResetColor();
            return new JarvisResponse(null, json);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[LLM Content Parse Error]: {ex.Message} | Текст: {json}");
            Console.ResetColor();
        }

        return OfflineFallbackInterpreter(spokenText);
    }

    /// <summary>
    /// Removes markdown blocks (e.g. ```json ... ```) and extracts pure JSON string from the first '{' to last '}'.
    /// </summary>
    public static string CleanJsonMarkdown(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "{}";
        }

        string text = raw.Trim();

        if (text.Contains("```"))
        {
            var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                text = match.Groups[1].Value.Trim();
            }
        }

        int firstBrace = text.IndexOf('{');
        int lastBrace = text.LastIndexOf('}');
        if (firstBrace != -1 && lastBrace != -1 && lastBrace >= firstBrace)
        {
            return text.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
        }

        return text.Trim();
    }

    private static void LogRawResponse(string? rawResponse, string reason)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[LLM Ошибка] {reason}");
        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[LLM Raw Response Body]:\n{rawResponse}");
        }
        Console.ResetColor();
    }
}

