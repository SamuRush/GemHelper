using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Services.Steam;

namespace Gem.Services;

public sealed partial class SteamService
{
    public GameSearchResult? FindGame(string query, bool autoInstall = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        bool hasInstallAction = Regex.IsMatch(query, @"\b(установи|скачай|поставь|install)\b", RegexOptions.IgnoreCase);
        var result = SearchGame(query);
        if (result == null || result.Game == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра не найдена для запроса '{query}'.");
            Console.ResetColor();

            if (autoInstall || hasInstallAction)
            {
                VoiceFeedbackService.Instance?.SpeakAsync("Игра не найдена в вашей библиотеке, сэр.");
            }
            return null;
        }

        if (result.RequiresConfirmation)
        {
            if (autoInstall || hasInstallAction)
            {
                JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Install, result.Game);
                VoiceFeedbackService.Instance?.SpeakAsync($"Вы имели в виду {result.Game.Name}, сэр?");
            }
            return result;
        }

        if (autoInstall || hasInstallAction)
        {
            LaunchInstallation(result.Game);
        }

        return result;
    }

    public GameSearchResult? SearchGame(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (IsServiceEntry(query)) return null;

        string text = Regex.Replace(query, @"\b(установи|скачай|поставь|install|игра|игру|игры|запусти|включи|открой)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = query.Trim();

        if (IsServiceEntry(text)) return null;

        if (text.Contains("видео", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ролик", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("фильм", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("сериал", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rawQuery      = text.ToLowerInvariant().Replace(" ", "");
        string translitQuery = Transliterate(text).ToLowerInvariant().Replace(" ", "");

        string cleanRaw      = Regex.Replace(rawQuery,      @"[^a-z0-9а-яё]", "");
        string cleanTranslit = Regex.Replace(translitQuery, @"[^a-z0-9а-яё]", "");

        if (uint.TryParse(cleanRaw, out uint numericAppId))
        {
            lock (_lock)
            {
                if (_gamesByAppId.TryGetValue(numericAppId.ToString(), out var directAppIdGame))
                {
                    return new GameSearchResult(directAppIdGame, 1.0, false);
                }
            }
        }

        lock (_lock)
        {
            if ((_aliases.TryGetValue(cleanRaw, out string? aliasAppId) ||
                 _aliases.TryGetValue(cleanTranslit, out aliasAppId)     ||
                 _aliases.TryGetValue(text, out aliasAppId)              ||
                 _aliases.TryGetValue(query.Trim(), out aliasAppId)) &&
                aliasAppId != null)
            {
                if (_gamesByAppId.TryGetValue(aliasAppId, out var aliasGame))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена по алиасу: '{query}' -> '{aliasGame.Name}' (AppID: {aliasGame.AppId})");
                    Console.ResetColor();
                    return new GameSearchResult(aliasGame, 1.0, false);
                }
            }
        }

        SteamGameInfo? bestMatch = null;
        double bestSimilarity = 0.0;

        lock (_lock)
        {
            foreach (var game in _games)
            {
                if (IsServiceEntry(game.Title) || IsServiceEntry(game.Name)) continue;

                var cleanGameName = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (string.IsNullOrEmpty(cleanGameName)) continue;

                double scoreRaw = CalculateSimilarity(cleanGameName, cleanRaw);
                double scoreTranslit = CalculateSimilarity(cleanGameName, cleanTranslit);

                double scoreCyrillic = !string.IsNullOrEmpty(game.CyrillicTitle)
                    ? CalculateSimilarity(Regex.Replace(game.CyrillicTitle.ToLowerInvariant(), @"[^a-z0-9а-яё]", ""), cleanRaw)
                    : 0.0;

                double currentScore = Math.Max(scoreRaw, Math.Max(scoreTranslit, scoreCyrillic));

                bool isDlcOrBonus = game.Title.Contains("Bonus Content", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Expansion Pass", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Artbook", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Trailer", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus) currentScore *= 0.80;

                if (currentScore > bestSimilarity)
                {
                    bestSimilarity = currentScore;
                    bestMatch = game;
                }
            }
        }

        if (bestSimilarity >= 0.85 && bestMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена (уверенно): '{query}' -> '{bestMatch.Name}' (AppID: {bestMatch.AppId}, Score: {bestSimilarity:F2})");
            Console.ResetColor();
            return new GameSearchResult(bestMatch, bestSimilarity, false);
        }

        if (bestSimilarity >= 0.60 && bestMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена в зоне сомнения (требуется подтверждение): '{query}' -> '{bestMatch.Name}' (AppID: {bestMatch.AppId}, Score: {bestSimilarity:F2})");
            Console.ResetColor();
            return new GameSearchResult(bestMatch, bestSimilarity, true);
        }

        string? llmResolvedTitle = QueryLlmForEnglishGameTitle(query);
        if (!string.IsNullOrWhiteSpace(llmResolvedTitle))
        {
            var matchedGame = MatchGameByTitle(llmResolvedTitle, query);
            if (matchedGame != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена через LLM fallback: '{query}' -> '{llmResolvedTitle}' -> '{matchedGame.Name}' (AppID: {matchedGame.AppId})");
                Console.ResetColor();
                return new GameSearchResult(matchedGame, 0.85, false);
            }
        }

        return null;
    }

    public Task<GameSearchResult?> SearchGameAsync(string query) => Task.Run(() => SearchGame(query));

    public static SteamGameInfo? FindInstalledGame(string query, bool initiateConfirmation = false) =>
        Instance.FindInstalledGameInternal(query, initiateConfirmation);

    public SteamGameInfo? FindInstalledGameInternal(string query, bool initiateConfirmation = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (IsServiceEntry(query)) return null;

        string text = Regex.Replace(query, @"\b(удали|деинсталлируй|сноси|удалить|деинсталлировать|снеси|игра|игру|игры)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = query.Trim();

        if (IsServiceEntry(text)) return null;

        string rawQuery      = text.ToLowerInvariant().Replace(" ", "");
        string translitQuery = Transliterate(text).ToLowerInvariant().Replace(" ", "");

        string cleanRaw      = Regex.Replace(rawQuery,      @"[^a-z0-9а-яё]", "");
        string cleanTranslit = Regex.Replace(translitQuery, @"[^a-z0-9а-яё]", "");

        lock (_lock)
        {
            if (uint.TryParse(cleanRaw, out uint numericAppId))
            {
                if (_gamesByAppId.TryGetValue(numericAppId.ToString(), out var directGame) &&
                    (directGame.IsInstalled || directGame.IsFromManifest || (directGame.StateFlags != 1 && directGame.StateFlags > 0)))
                {
                    if (initiateConfirmation) TriggerUninstallConfirmation(directGame);
                    return directGame;
                }
            }

            if ((_aliases.TryGetValue(cleanRaw, out string? aliasAppId) ||
                 _aliases.TryGetValue(cleanTranslit, out aliasAppId)     ||
                 _aliases.TryGetValue(text, out aliasAppId)              ||
                 _aliases.TryGetValue(query.Trim(), out aliasAppId)) &&
                aliasAppId != null)
            {
                if (_gamesByAppId.TryGetValue(aliasAppId, out var aliasGame) &&
                    (aliasGame.IsInstalled || aliasGame.IsFromManifest || (aliasGame.StateFlags != 1 && aliasGame.StateFlags > 0)))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра найдена по алиасу: '{query}' -> '{aliasGame.Name}' (AppID: {aliasGame.AppId})");
                    Console.ResetColor();

                    if (initiateConfirmation) TriggerUninstallConfirmation(aliasGame);
                    return aliasGame;
                }
            }

            SteamGameInfo? bestGame = null;
            double bestScore = 0.0;

            foreach (var game in _games)
            {
                if (IsServiceEntry(game.Title) || IsServiceEntry(game.Name)) continue;
                bool isCandidate = game.IsInstalled || game.IsFromManifest || (game.StateFlags != 1 && game.StateFlags > 0);
                if (!isCandidate) continue;

                var cleanGameName = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (string.IsNullOrEmpty(cleanGameName)) continue;

                double scoreRaw      = CalculateSimilarity(cleanGameName, cleanRaw);
                double scoreTranslit = CalculateSimilarity(cleanGameName, cleanTranslit);
                double scoreCyrillic = !string.IsNullOrEmpty(game.CyrillicTitle)
                    ? CalculateSimilarity(Regex.Replace(game.CyrillicTitle.ToLowerInvariant(), @"[^a-z0-9а-яё]", ""), cleanRaw)
                    : 0.0;

                double score = Math.Max(scoreRaw, Math.Max(scoreTranslit, scoreCyrillic));

                bool isDlcOrBonus = game.Title.Contains("Bonus Content", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Expansion Pass", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Artbook", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Trailer", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus) score *= 0.80;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestGame = game;
                }
            }

            if (bestScore >= 0.60 && bestGame != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра найдена: '{query}' -> '{bestGame.Name}' (AppID: {bestGame.AppId}, Score: {bestScore:F2})");
                Console.ResetColor();

                if (initiateConfirmation) TriggerUninstallConfirmation(bestGame);
                return bestGame;
            }

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра не найдена для запроса '{query}' (лучший балл: {bestScore:F2} < 0.60).");
            Console.ResetColor();

            return null;
        }
    }

    private static void TriggerUninstallConfirmation(SteamGameInfo game)
    {
        try
        {
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Uninstall, game);
            VoiceListener.Instance?.EnterConfirmationListening("Awaiting uninstallation confirmation reply (bypassing wake-word)...");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamService] Ошибка активации FSM подтверждения деинсталляции: {ex.Message}");
        }
    }

    public SteamGameInfo? MatchGameByTitle(string title, string? originalQuery = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (IsServiceEntry(title)) return null;

        string cleanTitle = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
        if (string.IsNullOrEmpty(cleanTitle)) return null;

        lock (_lock)
        {
            foreach (var game in _games)
            {
                if (IsServiceEntry(game.Title) || IsServiceEntry(game.Name)) continue;
                var cleanGame = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (cleanGame == cleanTitle) return game;
            }

            SteamGameInfo? best = null;
            double bestScore = 0.0;
            foreach (var game in _games)
            {
                if (IsServiceEntry(game.Title) || IsServiceEntry(game.Name)) continue;
                var cleanGame = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                double score = CalculateSimilarity(cleanGame, cleanTitle);

                bool isDlcOrBonus = game.Title.Contains("Bonus Content", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Expansion Pass", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Artbook", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Trailer", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus) score *= 0.80;

                if (!string.IsNullOrEmpty(originalQuery) && _aliases.TryGetValue(originalQuery, out string? origAliasAppId) && game.AppId == origAliasAppId)
                {
                    score += 0.15;
                }

                if (score > bestScore || (Math.Abs(score - bestScore) < 0.001 && game.IsInstalled && best != null && !best.IsInstalled))
                {
                    bestScore = score;
                    best = game;
                }
            }

            return bestScore >= 0.82 ? best : null;
        }
    }

    public string? QueryLlmForEnglishGameTitle(string query)
    {
        try
        {
            var settings = AppSettingsService.Load();
            string baseUrl = settings.LlmBaseUrl;
            string model = settings.LlmModel;
            string apiKey = settings.LlmApiKey;

            string prompt = $"Какое точное оригинальное название у игры '{query}' в Steam на английском языке? Ответь ТОЛЬКО названием игры, без кавычек и знаков препинания.";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string endpoint = LlmIntentService.BuildEndpointUrl(baseUrl);

            var requestBody = new
            {
                model = model,
                temperature = 0.1,
                max_tokens = 50,
                messages = new[] { new { role = "user", content = prompt } }
            };

            string json = JsonSerializer.Serialize(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey) && !apiKey.Equals("YOUR_OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = Task.Run(async () => await client.SendAsync(request)).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                string body = Task.Run(async () => await response.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    string? text = null;
                    if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentProp))
                    {
                        text = contentProp.GetString();
                    }
                    else if (first.TryGetProperty("text", out var textProp))
                    {
                        text = textProp.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string cleaned = text.Trim().Trim('"', '\'', '`', '.', '!', '?', '\n', '\r');
                        cleaned = Regex.Replace(cleaned, @"^```[a-z]*\s*", "", RegexOptions.IgnoreCase);
                        cleaned = Regex.Replace(cleaned, @"\s*```$", "", RegexOptions.IgnoreCase).Trim();
                        return cleaned;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] LLM fallback для '{query}' не ответил: {ex.Message}");
            Console.ResetColor();
        }

        lock (_lock)
        {
            if (_aliases.TryGetValue(query, out string? aliasAppId) && _gamesByAppId.TryGetValue(aliasAppId, out var aliasGame))
            {
                return aliasGame.Title;
            }
        }

        return null;
    }
}
