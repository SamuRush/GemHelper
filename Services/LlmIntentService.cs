using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Core;
using Microsoft.Extensions.Configuration;

namespace Gem.Services;

/// <summary>
/// Interprets spoken phrases and maps them into JarvisResponse via an OpenAI-compatible API.
/// </summary>
public sealed class LlmIntentService
{
    public const string SystemPrompt =
        """
        ТЫ — СИСТЕМНЫЙ ИИ ДВОРЕЦКИЙ JARVIS. ОТВЕЧАЙ ИСКЛЮЧИТЕЛЬНО JSON-ОБЪЕКТОМ.

        СХЕМА:
        {
          "commandRequest": {
            "command": "app" | "system" | "weather" | "timer",
            "args": {
              "action": "start" | "close" | "install" | "set" | "cancel" | "status",
              "name": "<цель>",
              "city": "<город в именительном падеже>" | null,
              "seconds": <число секунд> | null,
              "label": "<описание таймера>" | null
            }
          } | null,
          "reply": "<текст голосового ответа>" | null
        }

        ПРАВИЛА:
        1. ПОГОДА: На любые вопросы о погоде, температуре, дожде:
           {"commandRequest": {"command": "weather", "args": {"city": "<город в именительном падеже>" | null}}, "reply": null}
           Если город не указан, установи "city": null. Если город указан в косвенном падеже (например, "в Москве", "в Париже", "в Лондоне"), обязательно преобразуй его в именительный падеж ("Москва", "Париж", "Лондон").

        2. ВЫХОД ИЗ ДЖАРВИСА ('закройся', 'выключись', 'отключись'):
           {"commandRequest": {"command": "system", "args": {"action": "close", "name": "jarvis"}}, "reply": "Завершаю работу. До свидания, сэр."}

        3. ИГРЫ И ПРИЛОЖЕНИЯ:
           - Установка игр ("установи <игра>"):
             {"commandRequest": {"command": "app", "args": {"action": "install", "name": "<название игры>"}}, "reply": "Инициирую установку, сэр."}
           - Запуск игр и приложений ("запусти <игра/приложение>"):
             {"commandRequest": {"command": "app", "args": {"action": "start", "name": "<название>"}}, "reply": "Запускаю игру, сэр."}
           - Закрытие:
             {"commandRequest": {"command": "app", "args": {"action": "close", "name": "<название>"}}, "reply": "Закрываю, сэр."}

        4. ОБЫЧНЫЙ ДИАЛОГ (commandRequest = null):
           На вопросы ('как дела', 'кто ты'):
           {"commandRequest": null, "reply": "Все системы функционируют в штатном режиме, сэр."}

        5. ТАЙМЕРЫ И НАПОМИНАНИЯ ('поставь таймер', 'напомни через', 'отмени таймер', 'сколько осталось'):
           - Установка таймера ("поставь таймер на 10 минут", "напомни через 5 минут выключить духовку"):
             {"commandRequest": {"command": "timer", "args": {"action": "set", "seconds": 600, "label": "таймер"}}, "reply": "Таймер на 10 минут установлен, сэр."}
           - Отмена таймера ("отмени таймер", "сбрось таймер"):
             {"commandRequest": {"command": "timer", "args": {"action": "cancel"}}, "reply": "Таймер отменен, сэр."}
           - Статус таймера ("сколько осталось", "статус таймера"):
             {"commandRequest": {"command": "timer", "args": {"action": "status"}}, "reply": "Осталось 5 минут 20 секунд, сэр."}

        ПРИМЕРЫ:
        Пользователь: 'какая погода'
        Ответ: {"commandRequest": {"command": "weather", "args": {"city": null}}, "reply": null}

        Пользователь: 'погода в Москве'
        Ответ: {"commandRequest": {"command": "weather", "args": {"city": "Москва"}}, "reply": null}

        Пользователь: 'погода в Париже'
        Ответ: {"commandRequest": {"command": "weather", "args": {"city": "Париж"}}, "reply": null}

        Пользователь: 'установи ноу менс скай'
        Ответ: {"commandRequest": {"command": "app", "args": {"action": "install", "name": "ноу менс скай"}}, "reply": "Инициирую установку, сэр."}

        Пользователь: 'установи киберпанк'
        Ответ: {"commandRequest": {"command": "app", "args": {"action": "install", "name": "киберпанк"}}, "reply": "Начинаю установку игры, сэр."}

        Пользователь: 'запусти витчер'
        Ответ: {"commandRequest": {"command": "app", "args": {"action": "start", "name": "витчер"}}, "reply": "Запускаю игру, сэр."}

        Пользователь: 'поставь таймер на 10 секунд'
        Ответ: {"commandRequest": {"command": "timer", "args": {"action": "set", "seconds": 10, "label": "таймер"}}, "reply": "Таймер на 10 секунд установлен, сэр."}

        Пользователь: 'отмени таймер'
        Ответ: {"commandRequest": {"command": "timer", "args": {"action": "cancel"}}, "reply": "Таймер отменен, сэр."}
        """;


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
    /// E.g. "http://localhost:1234/v1" -> "http://localhost:1234/v1/chat/completions"
    /// E.g. "http://127.0.0.1:1234" -> "http://127.0.0.1:1234/v1/chat/completions"
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

        // If no /v1 or /chat/completions specified, append /v1/chat/completions
        return $"{trimmed}/v1/chat/completions";
    }

    /// <summary>
    /// Cleans incoming text from wake-word prefixes ("джарвис", "джар", "jarvis").
    /// </summary>
    public static string CleanPrefixes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = text.Trim();
        string[] prefixes = ["джарвис", "джар", "jarvis"];
        bool matched;
        do
        {
            matched = false;
            foreach (var prefix in prefixes)
            {
                if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure prefix is a whole word or followed by non-letter
                    if (cleaned.Length == prefix.Length || !char.IsLetter(cleaned[prefix.Length]))
                    {
                        cleaned = cleaned[prefix.Length..].Trim().TrimStart(',', ':', '-', '!', '?').Trim();
                        matched = true;
                    }
                }
            }
        } while (matched);

        return cleaned;
    }

    /// <summary>
    /// Interprets the spoken text into a CommandRequest and butler reply.
    /// </summary>
    public async Task<JarvisResponse> InterpretAsync(string spokenText, CancellationToken cancellationToken = default)
    {
        // 1. Clean incoming text from wake-word prefixes ("джарвис", "джар", "jarvis")
        string cleanedSpokenText = CleanPrefixes(spokenText);

        if (string.IsNullOrWhiteSpace(cleanedSpokenText))
        {
            return new JarvisResponse(null, "Слушаю вас, сэр.");
        }

        // 1.5. Process confirmation reply if awaiting user confirmation
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

            // Any other command: reset pending state and proceed with normal command execution
            JarvisOrchestrator.Instance.ResetConfirmationState();
        }

        // Fast-path pattern matching: instantly executes clear deterministic commands (0ms latency, zero hallucinations)
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[Router: FastMatch] Анализ фразы: \"{cleanedSpokenText}\"...");
        Console.ResetColor();

        var fastMatch = TryFastMatch(cleanedSpokenText);
        if (fastMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Router: FastMatch] HIT -> Команда: '{fastMatch.CommandRequest?.Command ?? "dialog"}', Ответ: '{fastMatch.Reply}'");
            Console.ResetColor();
            return fastMatch;
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("[Router: FastMatch] MISS -> Перенаправление в LLM (InterpretAsync)...");
        Console.ResetColor();

        // Check if API key is placeholder
        if (_apiKey.Equals("YOUR_OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("[LLM] Предупреждение: ApiKey содержит плейсхолдер. Использую локальный резервный парсер.");
            Console.ResetColor();

            return OfflineFallbackInterpreter(cleanedSpokenText);
        }

        OnThinkingStarted?.Invoke();
        string responseBody = string.Empty;
        try
        {
            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[LLM Request] URL: {_endpointUrl} | Model: {_model}");
                Console.ResetColor();
            }

            // Request to LM Studio / LLM (without response_format to prevent HTTP 400)
            var (success, statusCode, body) = await SendChatCompletionRequestAsync(cleanedSpokenText, cancellationToken);
            responseBody = body;

            if (!success)
            {
                LogRawResponse(responseBody, $"[LLM API Error] HTTP {statusCode}");
                return OfflineFallbackInterpreter(cleanedSpokenText);
            }

            if (_debugMode)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[LLM Raw Response]: {responseBody}");
                Console.ResetColor();
            }

            // Safe parsing of the JSON response
            var result = ParseResponseContent(responseBody, cleanedSpokenText);

            // Persist turn to dialog history (user + assistant)
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
        // Build messages: system prompt + accumulated dialog history + current user message
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

        // Note: "response_format": { "type": "json_object" } is intentionally omitted
        // to prevent HTTP 400 errors from local engines like LM Studio.
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

            // 1. Check if the server returned an error object: { "error": { "message": "..." } }
            if (root.TryGetProperty("error", out var errorProp))
            {
                string errMsg = errorProp.ValueKind == JsonValueKind.Object && errorProp.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? errorProp.ToString()
                    : errorProp.ToString();

                LogRawResponse(responseBody, $"Ошибка от LLM сервера: {errMsg}");
                return OfflineFallbackInterpreter(spokenText);
            }

            // 2. Safely check for "choices" array
            if (!root.TryGetProperty("choices", out var choicesProp) ||
                choicesProp.ValueKind != JsonValueKind.Array ||
                choicesProp.GetArrayLength() == 0)
            {
                LogRawResponse(responseBody, "Отсутствует или пуст массив 'choices'.");
                return OfflineFallbackInterpreter(spokenText);
            }

            var firstChoice = choicesProp[0];

            // 3. Safely check for "message" property
            if (!firstChoice.TryGetProperty("message", out var messageProp) ||
                messageProp.ValueKind != JsonValueKind.Object)
            {
                // Fallback for completion-style endpoints with "text" instead of "message"
                if (firstChoice.TryGetProperty("text", out var textProp))
                {
                    string fallbackRaw = CleanJsonMarkdown(textProp.GetString() ?? string.Empty);
                    return DeserializeJarvisResponse(fallbackRaw, spokenText);
                }

                LogRawResponse(responseBody, "В 'choices[0]' отсутствует объект 'message'.");
                return OfflineFallbackInterpreter(spokenText);
            }

            // 4. Safely check for "content" property
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
        // If the model returned plain text instead of JSON — use it directly as reply
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
            // Return the raw text as reply instead of falling back to generic "Вас понял, сэр"
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

        // Check if enclosed in markdown code fences
        if (text.Contains("```"))
        {
            var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                text = match.Groups[1].Value.Trim();
            }
        }

        // Robust extraction: find substring from first '{' to last '}'
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

    /// <summary>
    /// Safely parses JSON and clones RootElement while immediately disposing the JsonDocument.
    /// Prevents internal ArrayPool rented buffer leaks from un-disposed JsonDocument instances.
    /// </summary>
    private static JsonElement CreateFastMatchArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Fast-path heuristic matcher for unambiguous deterministic commands.
    /// Handles volume (including setting levels and deltas), common apps, and standard hotkeys
    /// with zero latency and 100% reliability, bypassing LLM delays or hallucination.
    /// </summary>
    public static JarvisResponse? TryFastMatch(string spokenText)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            return null;
        }

        string lower = spokenText.ToLowerInvariant().Trim();

        // 0. Pending confirmation reply fast-match
        if (JarvisOrchestrator.Instance.HasPendingAction)
        {
            if (JarvisOrchestrator.IsAffirmativeReply(lower))
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

            if (JarvisOrchestrator.IsNegativeReply(lower))
            {
                var action = JarvisOrchestrator.Instance.PendingAction;
                JarvisOrchestrator.Instance.ResetConfirmationState();
                string reply = action == PendingGameAction.Uninstall ? "Удаление отменено, сэр." : "Понял, отменяю.";
                return new JarvisResponse(null, reply);
            }

            // Any other command: reset pending state and proceed with normal matching below
            JarvisOrchestrator.Instance.ResetConfirmationState();
        }

        // 1. Media Playback & Track Controls (0ms latency, Win32 SendInput bypasses LLM & SteamService completely)
        var mediaMatch = MatchMediaCommand(lower);
        if (mediaMatch != null)
        {
            return mediaMatch;
        }

        // 2. Timer & Reminders (0ms latency, local timer service before Steam and Volume)
        var timerMatch = MatchTimerCommand(lower);
        if (timerMatch != null)
        {
            return timerMatch;
        }

        // 2. Volume commands
        if (lower.Contains("громк") || lower.Contains("звук") || lower.Contains("тише") || lower.Contains("громче") ||
            lower.Contains("убавь") || lower.Contains("прибавь") || lower.StartsWith("volume") || lower.StartsWith("vol") || Regex.IsMatch(lower, @"^\s*\d{1,3}\s*%\s*$"))
        {
            // Mute
            if (lower.Contains("выключи") || lower.Contains("без звука") || lower.Contains("мут") || lower.Contains("mute") || lower.Contains("заглуши"))
            {
                var args = CreateFastMatchArgs("""{ "action": "mute", "isMuted": true }""");
                return new JarvisResponse(new CommandRequest("volume", args), "Звук отключен, сэр.");
            }

            // Unmute
            if (lower.Contains("включи") || lower.Contains("размут") || lower.Contains("unmute") || lower.Contains("верни звук"))
            {
                var args = CreateFastMatchArgs("""{ "action": "mute", "isMuted": false }""");
                return new JarvisResponse(new CommandRequest("volume", args), "Звук включен, сэр.");
            }

            // Status / Query
            if (lower.Contains("статус") || lower.Contains("какой") || lower.Contains("сколько") || lower == "звук" || lower == "громкость")
            {
                var args = CreateFastMatchArgs("""{ "action": "get" }""");
                return new JarvisResponse(new CommandRequest("volume", args), "Проверяю громкость, сэр.");
            }

            // Relative increase
            if (lower.Contains("прибавь") || lower.Contains("громче") || lower.Contains("выше") || lower.Contains("добавь") || lower.Contains("увеличь") || lower.Contains("+"))
            {
                int delta = ParseVolumeNumber(lower) ?? 10;
                var args = CreateFastMatchArgs($$"""{ "action": "change", "delta": {{delta}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость увеличена на {delta}%, сэр.");
            }

            // Relative decrease
            if (lower.Contains("убавь") || lower.Contains("тише") || lower.Contains("меньше") || lower.Contains("снизь") || lower.Contains("уменьши") || lower.Contains("-"))
            {
                int delta = ParseVolumeNumber(lower) ?? 10;
                var args = CreateFastMatchArgs($$"""{ "action": "change", "delta": {{-delta}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость уменьшена на {delta}%, сэр.");
            }

            // Absolute set (e.g. "звук 60", "громкость 50", "звук на 70%", "звук шестьдесят", "звук максимум")
            int? level = ParseVolumeNumber(lower);
            if (level.HasValue)
            {
                var args = CreateFastMatchArgs($$"""{ "action": "set", "level": {{level.Value}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость установлена на {level.Value}%, сэр.");
            }
        }

        // Weather commands
        if (lower.Contains("погода") || lower.Contains("погоду") || lower.Contains("погоде") ||
            lower.Contains("температур") || lower.Contains("на улице") || lower.Contains("дождь") || lower.Contains("снег"))
        {
            // If specific city is requested (e.g. "погода в москве", "погода в париже"), pass to LLM for precise normalization
            bool hasSpecificCity = Regex.IsMatch(lower, @"\b(?:в|во|для|по)\s+[а-яa-zё\-]+", RegexOptions.IgnoreCase);
            if (!hasSpecificCity)
            {
                var args = CreateFastMatchArgs("""{ "city": null }""");
                return new JarvisResponse(new CommandRequest("weather", args), null);
            }
        }

        // Uninstallation commands ("удали ...", "деинсталлируй ...", "сноси ...")
        var uninstallMatch = Regex.Match(spokenText.Trim(), @"^(?:удали|деинсталлируй|сноси)\s+(?:игру\s+)?(.+)$", RegexOptions.IgnoreCase);
        if (uninstallMatch.Success)
        {
            string gameQuery = uninstallMatch.Groups[1].Value.Trim().TrimEnd('.', '!', '?', ',');
            if (!string.IsNullOrWhiteSpace(gameQuery))
            {
                var installedGame = SteamService.FindInstalledGame(gameQuery, initiateConfirmation: true);
                if (installedGame == null)
                {
                    return new JarvisResponse(null, $"Установленная игра '{gameQuery}' не найдена, сэр.");
                }

                // Деинсталляция ВСЕГДА требует обязательного голосового подтверждения через PendingActionState
                JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Uninstall, installedGame);
                try
                {
                    Gem.Voice.VoiceListener.Instance?.EnterConfirmationListening();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VoiceListener] Не удалось переключить режим ожидания подтверждения: {ex.Message}");
                }

                return new JarvisResponse(null, $"Вы действительно хотите удалить {installedGame.Title}, сэр?");
            }
        }

        // Installation commands ("установи ...", "скачай ...", "поставь ...", "install ...")
        if (lower.StartsWith("установи ") || lower.StartsWith("скачай ") || lower.StartsWith("поставь ") || lower.StartsWith("install "))
        {
            string gameName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                var search = SteamService.Instance.SearchGame(gameName);
                if (search != null && search.RequiresConfirmation && search.Game != null)
                {
                    JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Install, search.Game);
                    return new JarvisResponse(null, $"Вы имели в виду {search.Game.Name}, сэр?");
                }

                var args = CreateFastMatchArgs($$"""{ "action": "install", "name": {{JsonSerializer.Serialize(gameName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Инициирую установку, сэр.");
            }
        }

        // 2. Apps & System Applications (Prioritized before generic Steam game search)
        bool isCloseAction = lower.Contains("закрой") || lower.Contains("выключи") || lower.Contains("убей") ||
                             lower.Contains("заверши") || lower.Contains("close") || lower.Contains("kill");

        // Self-shutdown: "закройся", "закрывайся", "выключись", "стоп приложение", "завершись", "отключись", "выход"
        if (lower == "закройся" || lower == "закрывайся" || lower == "выключись" || lower == "стоп приложение" || lower == "завершись" || lower == "отключись" ||
            lower == "выход" || (isCloseAction && (lower.Contains("джарвис") || lower.Contains("jarvis") || lower.Contains("ассистент") || lower.Contains("себя"))))
        {
            var args = CreateFastMatchArgs("""{ "action": "close", "name": "jarvis" }""");
            return new JarvisResponse(new CommandRequest("system", args), "Завершаю работу. До свидания, сэр.");
        }

        // Monster Hunter Wilds
        if (lower.Contains("вайлд") || lower.Contains("уайлд") || lower.Contains("wilds"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "mhw_wilds" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Monster Hunter Wilds, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "mhw_wilds" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Monster Hunter Wilds, сэр.");
            }
        }

        // Monster Hunter World
        if (lower.Contains("ворлд") || lower.Contains("world") || lower.Contains("монстер хантер") || lower.Contains("mhw"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "mhw_world" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Monster Hunter World, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "mhw_world" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Monster Hunter World, сэр.");
            }
        }

        // Steam ("steam", "tim", "стим")
        if (lower.Contains("стим") || lower.Contains("steam") || lower.Contains(" tim") || lower.StartsWith("tim") || lower.Contains(" тим") || lower.StartsWith("тим"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "steam", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Steam, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "steam" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Steam, сэр.");
            }
        }

        // Discord ("discord", "дискорд", "диск")
        if (lower.Contains("дискорд") || lower.Contains("discord") || lower.Contains("диск"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "discord", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Discord, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "discord" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Discord, сэр.");
            }
        }

        // Telegram ("telegram", "телега", "телеграм")
        if (lower.Contains("телеграм") || lower.Contains("телега") || lower.Contains("telegram") || lower.Contains("телегу"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "telegram", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Telegram, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "telegram" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Telegram, сэр.");
            }
        }

        // Browser ("browser", "браузер", "brave", "хром", "chrome", etc.)
        if (lower.Contains("браузер") || lower.Contains("хром") || lower.Contains("chrome") || lower.Contains("брейв") || lower.Contains("brave") || lower.Contains("browser"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "browser", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю браузер, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "browser" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю браузер, сэр.");
            }
        }

        // Notepad ("notepad", "блокнот")
        if (lower.Contains("блокнот") || lower.Contains("notepad"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "notepad", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю блокнот, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "notepad" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю блокнот, сэр.");
            }
        }

        // Calc ("calc", "калькулятор")
        if (lower.Contains("калькулятор") || lower.Contains("calc"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "calc", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю калькулятор, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "calc" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю калькулятор, сэр.");
            }
        }

        if (lower.Contains("проводник") || lower.Contains("explorer"))
        {
            if (isCloseAction)
            {
                var args = CreateFastMatchArgs("""{ "action": "close", "name": "explorer", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю проводник, сэр.");
            }
            else
            {
                var args = CreateFastMatchArgs("""{ "action": "start", "name": "explorer" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю проводник, сэр.");
            }
        }

        // Launch generic Steam game commands ("запусти ...", "включи ...", "открой ...")
        if (lower.StartsWith("запусти ") || lower.StartsWith("включи ") || lower.StartsWith("открой "))
        {
            string candidate = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var search = SteamService.Instance.SearchGame(candidate);
                if (search != null && search.RequiresConfirmation && search.Game != null)
                {
                    JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Start, search.Game);
                    return new JarvisResponse(null, $"Вы имели в виду {search.Game.Name}, сэр?");
                }

                if (search != null && search.Game != null)
                {
                    var args = CreateFastMatchArgs($$"""{ "action": "start", "name": {{JsonSerializer.Serialize(candidate)}} }""");
                    return new JarvisResponse(new CommandRequest("app", args), "Запускаю игру, сэр.");
                }
            }
        }

        // 3. Hotkeys & System shortcuts
        if (lower.Contains("диспетчер задач"))
        {
            var args = CreateFastMatchArgs("""{ "keys": ["ctrl", "shift", "esc"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Открываю диспетчер задач, сэр.");
        }

        if (lower.Contains("сверни все") || lower.Contains("свернуть все") || lower.Contains("рабочий стол"))
        {
            var args = CreateFastMatchArgs("""{ "keys": ["win", "d"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Сворачиваю окна, сэр.");
        }

        if (lower.Contains("скриншот") || lower.Contains("снимок экрана"))
        {
            var args = CreateFastMatchArgs("""{ "keys": ["win", "shift", "s"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Делаю снимок экрана, сэр.");
        }

        if (lower.Contains("пробел") || lower == "space")
        {
            var args = CreateFastMatchArgs("""{ "key": "space" }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Клавиша пробел, сэр.");
        }

        if (lower.Contains("копируй") || lower.Contains("скопируй"))
        {
            var args = CreateFastMatchArgs("""{ "keys": ["ctrl", "c"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Копирую в буфер, сэр.");
        }

        if (lower.Contains("вставь") || lower.Contains("вставить"))
        {
            var args = CreateFastMatchArgs("""{ "keys": ["ctrl", "v"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Вставляю из буфера, сэр.");
        }

        return null;
    }

    /// <summary>
    /// Fast-path recognition for media playback and track navigation commands.
    /// Emulates VK_MEDIA_* virtual keys (0ms latency, zero hallucinations, no SteamService/LLM calls).
    /// </summary>
    public static JarvisResponse? MatchMediaCommand(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
        {
            return null;
        }

        string text = CleanPrefixes(lower).Trim();
        // Strip common polite and filler words (e.g. "пожалуйста", "мне", "быстро", "сейчас", "давай")
        text = Regex.Replace(text, @"\b(пожалуйста|мне|быстро|сейчас|давай)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // 1. Stop Category:
        // Patterns: "заглуши музыку", "выключи музыку"
        // Action: emulation of VK_MEDIA_STOP (0xB2) or VK_MEDIA_PLAY_PAUSE (0xB3)
        // Voice feedback: "Остановил, сэр."
        if (text == "заглуши музыку" || text == "выключи музыку" ||
            text == "заглушить музыку" || text == "выключить музыку" ||
            text == "стоп музыка" || text == "стоп музыку" ||
            text == "выключи трек" || text == "заглуши трек" ||
            text.StartsWith("заглуши музыку") || text.StartsWith("выключи музыку"))
        {
            var args = CreateFastMatchArgs("""{ "action": "stop", "key": "media_stop" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Остановил, сэр.");
        }

        // 2. Next Track Category:
        // Patterns: "следующий трек", "следующая песня", "дальше", "переключи", "трек вперед"
        // Action: emulation of VK_MEDIA_NEXT_TRACK (0xB0)
        // Voice feedback: "Следующий трек, сэр."
        bool isNext = text == "следующий трек" || text == "следующая песня" || text == "дальше" ||
                      text == "переключи" || text == "переключи трек" || text == "переключи песню" ||
                      text == "переключить" || text == "переключай" ||
                      text == "трек вперед" || text == "песня вперед" || text == "вперед трек" ||
                      text == "следующий" || text == "следующую" || text == "следующую песню" ||
                      text.StartsWith("следующий трек") || text.StartsWith("следующая песня") ||
                      text.StartsWith("переключи трек") || text.StartsWith("переключи песню");

        // Guard: do not confuse "переключи раскладку / язык / окно" with next track
        if (isNext && !text.Contains("раскладк") && !text.Contains("язык") && !text.Contains("окно"))
        {
            var args = CreateFastMatchArgs("""{ "action": "next", "key": "media_next" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Следующий трек, сэр.");
        }

        // 3. Prev Track Category:
        // Patterns: "предыдущий трек", "предыдущая песня", "назад", "верни трек"
        // Action: emulation of VK_MEDIA_PREV_TRACK (0xB1)
        // Voice feedback: "Предыдущий трек, сэр."
        bool isPrev = text == "предыдущий трек" || text == "предыдущая песня" || text == "назад" ||
                      text == "верни трек" || text == "верни песню" || text == "трек назад" ||
                      text == "песня назад" || text == "назад трек" ||
                      text == "прошлый трек" || text == "прошлая песня" || text == "предыдущий" ||
                      text == "предыдущую" || text == "предыдущую песню" ||
                      text.StartsWith("предыдущий трек") || text.StartsWith("предыдущая песня") ||
                      text.StartsWith("верни трек") || text.StartsWith("верни песню");

        if (isPrev)
        {
            var args = CreateFastMatchArgs("""{ "action": "prev", "key": "media_prev" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Предыдущий трек, сэр.");
        }

        // 4. Play/Pause Toggle Category:
        // Patterns: "включи музыку", "музыка", "играй", "пауза", "стоп", "останови", "останови музыку", "продолжи", "возобнови"
        // Action: emulation of VK_MEDIA_PLAY_PAUSE (0xB3)
        // Voice feedback:
        //   Для паузы/остановки: "Остановил, сэр."
        //   Для продолжения/включения: "Включаю."

        // 4A. Pause / Stop: "пауза", "стоп", "останови", "останови музыку"
        bool isPause = text == "пауза" || text == "стоп" || text == "останови" || text == "останови музыку" ||
                       text == "останови воспроизведение" || text == "останови трек" || text == "останови песню" ||
                       text == "поставь на паузу" || text == "на паузу" || text == "паузу" ||
                       text == "стоп трек" || text == "стоп песню" ||
                       text.StartsWith("останови музыку") || text.StartsWith("останови трек") ||
                       text.StartsWith("останови песню") || text.StartsWith("поставь на паузу");

        // Do not intercept "стоп приложение" or "стоп джарвис"
        if (isPause && !text.Contains("приложение") && !text.Contains("джарвис") && !text.Contains("себя") && !text.Contains("программ"))
        {
            var args = CreateFastMatchArgs("""{ "action": "play_pause", "key": "media_play_pause" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Остановил, сэр.");
        }

        // 4B. Play / Resume: "включи музыку", "музыка", "играй", "продолжи", "возобнови",
        //     "запусти музыку", "поставь трек", "поставь песню" и аналоги.
        // Regex-паттерн с $-якорем гарантирует точное совпадение только с медиа-объектами,
        // исключая имена Steam-игр (например, "запусти витчера" не совпадёт).
        bool isPlay = text == "включи музыку" || text == "включить музыку" || text == "музыка" || text == "музыку" ||
                      text == "играй" || text == "играй музыку" || text == "играть музыку" ||
                      text == "продолжи" || text == "продолжить" || text == "продолжай" ||
                      text == "продолжи музыку" || text == "продолжить музыку" || text == "продолжи воспроизведение" ||
                      text == "продолжить воспроизведение" ||
                      text == "возобнови" || text == "возобновить" || text == "возобнови музыку" ||
                      text == "возобновить музыку" || text == "возобнови воспроизведение" ||
                      text == "возобновить воспроизведение" ||
                      text == "сними с паузы" || text == "плей" || text == "play" ||
                      text.StartsWith("включи музыку") || text.StartsWith("включить музыку") ||
                      text.StartsWith("продолжи музыку") || text.StartsWith("возобнови музыку") ||
                      // Паттерны: «запусти/поставь + музыку/трек/песню/воспроизведение»
                      Regex.IsMatch(text,
                          @"^(запусти|поставь|включи|продолжи|возобнови)\s+(музыку|музыка|трек|треки|песню|песня|воспроизведение|плейлист)$",
                          RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Guard: do not confuse "играй в <game>" with media playback
        if (isPlay && !text.StartsWith("играй в ") && !text.StartsWith("играть в "))
        {
            var args = CreateFastMatchArgs("""{ "action": "play_pause", "key": "media_play_pause" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Включаю.");
        }

        return null;
    }

    /// <summary>
    /// Fast-path recognition for timer and reminder commands (0ms latency, zero hallucinations).
    /// </summary>
    public static JarvisResponse? MatchTimerCommand(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
        {
            return null;
        }

        string text = CleanPrefixes(lower).Trim();
        // Strip common polite and filler words (e.g. "пожалуйста", "мне", "быстро", "сейчас", "давай")
        text = Regex.Replace(text, @"\b(пожалуйста|мне|быстро|сейчас|давай)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // 1. Cancel Timer Category:
        // Patterns: "отмени таймер", "сбрось таймер", "выключи таймер", "отмени все таймеры", etc.
        if (IsTimerCancelCommand(text))
        {
            var args = CreateFastMatchArgs("""{ "action": "cancel" }""");
            return new JarvisResponse(new CommandRequest("timer", args), "Таймер отменен, сэр.");
        }

        // 2. Status Timer Category:
        // Patterns: "сколько осталось", "статус таймера", "что с таймером", etc.
        if (IsTimerStatusCommand(text))
        {
            var activeTimers = TimerService.Instance?.GetActiveTimers();
            string reply;
            if (activeTimers != null && activeTimers.Count > 0)
            {
                var remaining = activeTimers[0].Remaining;
                int totalSec = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
                int m = totalSec / 60;
                int s = totalSec % 60;
                reply = $"Осталось {m} минут {s} секунд, сэр.";
            }
            else
            {
                reply = "Нет активных таймеров, сэр.";
            }

            var args = CreateFastMatchArgs("""{ "action": "status" }""");
            return new JarvisResponse(new CommandRequest("timer", args), reply);
        }

        // 3. Set Timer / Reminder Category:
        if (TryParseTimerSetCommand(text, out TimeSpan duration, out string label, out string timeDisplay))
        {
            int totalSeconds = (int)Math.Ceiling(duration.TotalSeconds);
            if (totalSeconds > 0)
            {
                string jsonLabel = JsonSerializer.Serialize(label);
                var args = CreateFastMatchArgs($$"""{ "action": "set", "seconds": {{totalSeconds}}, "label": {{jsonLabel}} }""");
                string reply = $"Таймер на {timeDisplay} установлен, сэр.";
                return new JarvisResponse(new CommandRequest("timer", args), reply);
            }
        }

        return null;
    }

    private static bool IsTimerCancelCommand(string text)
    {
        return text == "отмени таймер" || text == "отменить таймер" ||
               text == "сбрось таймер" || text == "сбросить таймер" ||
               text == "выключи таймер" || text == "выключить таймер" ||
               text == "останови таймер" || text == "остановить таймер" ||
               text == "удали таймер" || text == "удалить таймер" ||
               text == "отмена таймера" ||
               text == "отмени все таймеры" || text == "сбрось все таймеры" ||
               text == "отмени таймеры" || text == "сбрось таймеры" ||
               text == "выключи все таймеры" || text == "выключи таймеры" ||
               Regex.IsMatch(text, @"^(?:отмени|отменить|сбрось|сбросить|выключи|выключить|останови|остановить|удали|удалить)\s+(?:все\s+)?таймер[ыа]?$");
    }

    private static bool IsTimerStatusCommand(string text)
    {
        return text == "сколько осталось" || text == "статус таймера" ||
               text == "что с таймером" || text == "сколько на таймере" ||
               text == "что там с таймером" || text == "сколько осталось времени" ||
               text == "сколько осталось до конца таймера" || text == "таймер статус" ||
               Regex.IsMatch(text, @"^(?:сколько осталось(?:\s+времени)?(?:\s+до конца(?:\s+таймера)?)?|статус таймера|что с таймером|что там с таймером|сколько на таймере)$");
    }

    public static bool TryParseTimerSetCommand(string text, out TimeSpan duration, out string label, out string timeDisplay)
    {
        duration = TimeSpan.Zero;
        label = "таймер";
        timeDisplay = string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return false;

        string work = text.Trim();

        // Check if reminder format: "напомни через ...", "напомни мне через ..."
        bool isReminder = false;
        var mRemind = Regex.Match(work, @"^(?:напомни(?: мне)?|напомнить(?: мне)?|напоминание)\s+(?:через\s+)?", RegexOptions.IgnoreCase);
        if (mRemind.Success)
        {
            isReminder = true;
            work = work[mRemind.Length..].Trim();
        }
        else
        {
            // Standard timer prefixes:
            // "поставь таймер на ", "заведи таймер на ", "включи таймер на ", "установи таймер на ", "запусти таймер на "
            // "поставь на ", "заведи на ", "включи на ", "установи на "
            // "таймер на ", "таймер "
            // "на "
            var mPrefix = Regex.Match(work, @"^(?:(?:поставь|заведи|включи|установи|запусти)\s+)?(?:таймер\s+)?на\s+", RegexOptions.IgnoreCase);
            if (mPrefix.Success)
            {
                work = work[mPrefix.Length..].Trim();
            }
            else
            {
                var mPrefix2 = Regex.Match(work, @"^(?:поставь|заведи|включи|установи|запусти)\s+таймер\s+", RegexOptions.IgnoreCase);
                if (mPrefix2.Success)
                {
                    work = work[mPrefix2.Length..].Trim();
                }
                else if (work.StartsWith("таймер ", StringComparison.OrdinalIgnoreCase))
                {
                    work = work[7..].Trim();
                }
                else if (work.StartsWith("через ", StringComparison.OrdinalIgnoreCase))
                {
                    work = work[6..].Trim();
                }
                else
                {
                    // To avoid capturing arbitrary non-timer commands, require time indicator
                    if (!Regex.IsMatch(work, @"^(?:полчаса|полтора|пол\s+часа|\d+|один|одну|одна|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|минут|секунд|час)", RegexOptions.IgnoreCase))
                    {
                        return false;
                    }
                }
            }
        }

        // Now parse duration and remainder from 'work'
        if (!ExtractDurationAndRemainder(work, out duration, out timeDisplay, out string remainder))
        {
            return false;
        }

        // Extract label from remainder
        if (isReminder)
        {
            string cleanRemainder = Regex.Replace(remainder, @"^(?:,|чтобы|что|надо|нужно|–|-)\s*", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleanRemainder))
            {
                label = cleanRemainder;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(remainder))
            {
                string cleanRemainder = Regex.Replace(remainder, @"^(?:на|для|про|–|-)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(cleanRemainder))
                {
                    label = cleanRemainder;
                }
            }
        }

        return true;
    }

    private static bool ExtractDurationAndRemainder(string s, out TimeSpan duration, out string timeDisplay, out string remainder)
    {
        duration = TimeSpan.Zero;
        timeDisplay = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();

        // 1. "полтора часа"
        var mHalfHours = Regex.Match(s, @"^полтора\s+час(?:а|ов|ика)?(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mHalfHours.Success)
        {
            duration = TimeSpan.FromMinutes(90);
            timeDisplay = "полтора часа";
            remainder = s[mHalfHours.Length..].Trim();
            return true;
        }

        // 2. "полчаса" / "пол часа"
        var mHalfHour = Regex.Match(s, @"^(?:полчаса|пол\s+часа|полчасика|пол\s+часика)(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mHalfHour.Success)
        {
            duration = TimeSpan.FromMinutes(30);
            timeDisplay = "полчаса";
            remainder = s[mHalfHour.Length..].Trim();
            return true;
        }

        // 3. Fractional hours (e.g. 1.5 часа, 0.5 часа, 2,5 часа)
        var mFracHour = Regex.Match(s, @"^(\d+[.,]\d+)\s*час(?:а|ов)?(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mFracHour.Success && double.TryParse(mFracHour.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fracHours))
        {
            duration = TimeSpan.FromHours(fracHours);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = s[mFracHour.Length..].Trim();
            return true;
        }

        // 4. Fractional minutes (e.g. 0.5 минут, 1.5 минуты)
        var mFracMin = Regex.Match(s, @"^(\d+[.,]\d+)\s*мин(?:ут|уты|уту|ута)?(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mFracMin.Success && double.TryParse(mFracMin.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fracMins))
        {
            duration = TimeSpan.FromMinutes(fracMins);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = s[mFracMin.Length..].Trim();
            return true;
        }

        // 5. Compound / sequential scan for hours, minutes, seconds:
        int totalSeconds = 0;
        bool foundAnyUnit = false;
        string current = s;

        // 5A. Check Hours
        var mH = Regex.Match(current, @"^(?:(?<val>\d+|один|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять)\s+)?(?:ч|час|часа|часов|часика|часик)(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mH.Success)
        {
            int h = mH.Groups["val"].Success ? (ParseTimerNumeral(mH.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += h * 3600;
            foundAnyUnit = true;
            current = current[mH.Length..].Trim();
        }

        // 5B. Check Minutes
        var mM = Regex.Match(current, @"^(?:(?<val>\d+|одну|одна|один|две|два|три|четыре|пять|шесть|семь|восемь|девять|десять|пятнадцать|двадцать|тридцать|сорок|пятьдесят)\s+)?(?:мин|минут|минуты|минуту|минута|минутки|минутку)(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mM.Success)
        {
            int m = mM.Groups["val"].Success ? (ParseTimerNumeral(mM.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += m * 60;
            foundAnyUnit = true;
            current = current[mM.Length..].Trim();
        }

        // 5C. Check Seconds
        var mS = Regex.Match(current, @"^(?:(?<val>\d+|одну|одна|один|две|два|три|четыре|пять|шесть|семь|восемь|девять|десять|пятнадцать|двадцать|тридцать|сорок|пятьдесят)\s+)?(?:сек|секунд|секунды|секунду|секунда|секундочку|секундка)(?:\s+|$)", RegexOptions.IgnoreCase);
        if (mS.Success)
        {
            int sec = mS.Groups["val"].Success ? (ParseTimerNumeral(mS.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += sec;
            foundAnyUnit = true;
            current = current[mS.Length..].Trim();
        }

        if (foundAnyUnit && totalSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(totalSeconds);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = current.Trim();
            return true;
        }

        return false;
    }

    private static int? ParseTimerNumeral(string val)
    {
        if (int.TryParse(val, out int n)) return n;
        return val.ToLowerInvariant() switch
        {
            "один" or "одну" or "одна" => 1,
            "два" or "две" => 2,
            "три" => 3,
            "четыре" => 4,
            "пять" => 5,
            "шесть" => 6,
            "семь" => 7,
            "восемь" => 8,
            "девять" => 9,
            "десять" => 10,
            "одиннадцать" => 11,
            "двенадцать" => 12,
            "тринадцать" => 13,
            "четырнадцать" => 14,
            "пятнадцать" => 15,
            "шестнадцать" => 16,
            "семнадцать" => 17,
            "восемнадцать" => 18,
            "девятнадцать" => 19,
            "двадцать" => 20,
            "тридцать" => 30,
            "сорок" => 40,
            "пятьдесят" => 50,
            _ => null
        };
    }

    /// <summary>
    /// Extracts a volume percentage number (0-100) from text, supporting digits,
    /// named words (максимум, минимум, половина), and Russian spoken numerals.
    /// </summary>
    public static int? ParseVolumeNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 1. Direct digits (e.g. "60", "60%")
        var match = Regex.Match(text, @"\b(\d{1,3})\b");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int num))
        {
            return Math.Clamp(num, 0, 100);
        }

        // 2. Semantic named levels
        if (text.Contains("максимум") || text.Contains("на всю") || text.Contains("полную") || text.Contains("сотку"))
        {
            return 100;
        }

        if (text.Contains("минимум") || text.Contains("ноль") || text.Contains("нуль"))
        {
            return 0;
        }

        if (text.Contains("половин"))
        {
            return 50;
        }

        // 3. Russian spoken number words (common for Vosk STT outputs)
        var words = text.Split(new[] { ' ', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
        int total = 0;
        bool foundAny = false;

        foreach (var rawWord in words)
        {
            string word = rawWord.Trim();
            int val = word switch
            {
                "один" or "единицу" => 1,
                "два" => 2,
                "три" => 3,
                "четыре" => 4,
                "пять" => 5,
                "шесть" => 6,
                "семь" => 7,
                "восемь" => 8,
                "девять" => 9,
                "десять" => 10,
                "одиннадцать" => 11,
                "двенадцать" => 12,
                "тринадцать" => 13,
                "четырнадцать" => 14,
                "пятнадцать" => 15,
                "шестнадцать" => 16,
                "семнадцать" => 17,
                "восемнадцать" => 18,
                "девятнадцать" => 19,
                "двадцать" => 20,
                "тридцать" => 30,
                "сорок" => 40,
                "пятьдесят" => 50,
                "шестьдесят" => 60,
                "семьдесят" => 70,
                "восемьдесят" => 80,
                "девяносто" => 90,
                "сто" => 100,
                _ => 0
            };

            if (val > 0)
            {
                total += val;
                foundAny = true;
            }
        }

        return foundAny ? Math.Clamp(total, 0, 100) : null;
    }

    /// <summary>
    /// Offline heuristic fallback so Jarvis can execute common commands without an active LLM key.
    /// </summary>
    public static JarvisResponse OfflineFallbackInterpreter(string spokenText)
    {
        var fast = TryFastMatch(spokenText);
        if (fast != null)
        {
            return fast;
        }

        // Offline weather with city support (e.g. "погода в москве")
        string lower = spokenText.ToLowerInvariant();
        if (lower.Contains("погода") || lower.Contains("погоду") || lower.Contains("погоде") || lower.Contains("температур"))
        {
            var match = Regex.Match(lower, @"\b(?:погода|погоду|погоде|температура|температуру|температуре)\s+(?:в|во)\s+([а-яa-zё\-]+)", RegexOptions.IgnoreCase);
            string? city = match.Success ? match.Groups[1].Value.Trim() : null;
            string cityJson = city != null ? $"\"{city}\"" : "null";
            var args = CreateFastMatchArgs($$"""{ "city": {{cityJson}} }""");
            return new JarvisResponse(new CommandRequest("weather", args), null);
        }

        // Offline install command
        if (lower.StartsWith("установи ") || lower.StartsWith("скачай ") || lower.StartsWith("поставь ") || lower.StartsWith("install "))
        {
            string gameName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                var args = CreateFastMatchArgs($$"""{ "action": "install", "name": {{JsonSerializer.Serialize(gameName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Инициирую установку, сэр.");
            }
        }

        // Offline start game / app
        if (lower.StartsWith("запусти ") || lower.StartsWith("включи ") || lower.StartsWith("открой "))
        {
            string appName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(appName))
            {
                var args = CreateFastMatchArgs($$"""{ "action": "start", "name": {{JsonSerializer.Serialize(appName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю игру, сэр.");
            }
        }

        // Fallback reply for generic speech
        return new JarvisResponse(null, "Вас понял, сэр.");

    }
}
