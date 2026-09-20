using Gem.Core;
using Gem.UI;

namespace Gem.Services;

/// <summary>
/// Possible actions requiring confirmation.
/// </summary>
public enum PendingGameAction
{
    None,
    Install,
    Start,
    Uninstall
}

/// <summary>
/// State payload describing an action pending user voice confirmation.
/// </summary>
public sealed class PendingActionState
{
    public string ActionType { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public SteamGameInfo? Game { get; set; }
}

/// <summary>
/// Orchestrator for processing recognized voice and text queries before LLM dispatch.
/// <para>
/// Provides a confirmation state machine for Steam game actions:
/// when a game search returns a probable (not exact) match, Jarvis asks the user
/// "Did you mean X?" and waits for a yes/no reply before proceeding.
/// </para>
/// </summary>
public sealed class JarvisOrchestrator
{
    // ─── Singleton ────────────────────────────────────────────────────────────

    private static readonly Lazy<JarvisOrchestrator> _instance =
        new(() => new JarvisOrchestrator());

    /// <summary>Global singleton instance shared across the application.</summary>
    public static JarvisOrchestrator Instance => _instance.Value;

    // ─── Confirmation State Machine ───────────────────────────────────────────

    public PendingGameAction PendingAction { get; set; } = PendingGameAction.None;
    public SteamGameInfo? PendingGame { get; set; } = null;
    public PendingActionState? CurrentPendingAction { get; set; } = null;
    public bool HasPendingAction => PendingAction != PendingGameAction.None && PendingGame != null;

    private SteamGameInfo? _pendingGameToInstall = null;
    private bool _isAwaitingConfirmation = false;

    // Timeout: reset confirmation state if user does not reply within 10 seconds
    private CancellationTokenSource? _confirmationTimeoutCts = null;

    // ─── Instance Methods ─────────────────────────────────────────────────────

    public void SetPendingConfirmation(PendingGameAction action, SteamGameInfo game, IVoiceFeedbackService? ttsService = null)
    {
        PendingAction = action;
        PendingGame = game;
        _pendingGameToInstall = game;
        _isAwaitingConfirmation = true;
        CurrentPendingAction = new PendingActionState
        {
            ActionType = action == PendingGameAction.Uninstall ? "UninstallGame" : action.ToString(),
            AppId = game.AppId,
            Title = game.Title,
            Game = game
        };
        StartConfirmationTimeout(ttsService);
    }

    /// <summary>
    /// Checks whether the user's spoken text represents an affirmative confirmation reply.
    /// </summary>
    public static bool IsAffirmativeReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string clean = text.Trim().ToLowerInvariant();
        if (clean == "да" || clean == "давай" || clean == "подтверждаю" || clean == "устанавливай" ||
            clean == "запускай" || clean == "удаляй" || clean == "деинсталлируй" || clean == "сноси" ||
            clean == "верно" || clean == "именно" || clean == "ага" ||
            clean == "конечно" || clean == "хорошо")
        {
            return true;
        }
        return System.Text.RegularExpressions.Regex.IsMatch(
            clean,
            @"\b(да|давай|подтверждаю|устанавливай|запускай|удаляй|деинсталлируй|сноси|верно|именно|ага|конечно|хорошо)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Checks whether the user's spoken text represents a negative cancellation reply.
    /// </summary>
    public static bool IsNegativeReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string clean = text.Trim().ToLowerInvariant();
        if (clean == "нет" || clean == "отмена" || clean == "не надо" || clean == "отбой" ||
            clean == "стой" || clean == "не то" || clean == "отставить")
        {
            return true;
        }
        return System.Text.RegularExpressions.Regex.IsMatch(
            clean,
            @"\b(нет|отмена|не надо|отбой|стой|не то|отставить)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Restarts the 10-second confirmation timeout (e.g. when microphone capture opens after TTS).
    /// </summary>
    public void RestartConfirmationTimeout()
    {
        if (HasPendingAction || _isAwaitingConfirmation)
        {
            StartConfirmationTimeout();
        }
    }

    /// <summary>
    /// Immediately resets the confirmation state without any side-effects.
    /// Call when the session ends or a timeout fires externally.
    /// </summary>
    public void ResetConfirmationState()
    {
        CancelConfirmationTimeout();
        _isAwaitingConfirmation = false;
        _pendingGameToInstall = null;
        PendingAction = PendingGameAction.None;
        PendingGame = null;
        CurrentPendingAction = null;

        try
        {
            var listener = Gem.Voice.VoiceListener.Instance;
            if (listener != null && listener.CurrentState == Gem.Voice.VoiceListenerState.ListeningForCommand)
            {
                listener.TransitionToWaitingForWakeWord("Confirmation completed or cancelled. Returning to wake-word mode.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JARVIS Warning] Ошибка сброса состояния слушателя: {ex.Message}");
        }
    }

    /// <summary>
    /// Main entry point for the confirmation state machine.
    /// Call this before routing any recognized text to the LLM pipeline.
    /// </summary>
    /// <param name="text">Raw recognized speech text.</param>
    /// <param name="steamService">Steam service used for searching and installing games.</param>
    /// <param name="ttsService">TTS service for voice feedback.</param>
    /// <param name="gameQueryExtractor">
    /// Optional delegate that extracts the game name from a free-form install command.
    /// When null, the raw <paramref name="text"/> is used as-is after stripping common keywords.
    /// </param>
    /// <returns>
    /// <c>true</c> when the input was handled by the state machine (caller should NOT
    /// forward to the LLM pipeline); <c>false</c> when the caller should continue
    /// with normal LLM processing.
    /// </returns>
    public async Task<bool> ProcessInputWithConfirmationAsync(
        string text,
        SteamService steamService,
        IVoiceFeedbackService ttsService,
        Func<string, string?>? gameQueryExtractor = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // ── A. Awaiting yes/no confirmation ──────────────────────────────────
        if (HasPendingAction || _isAwaitingConfirmation)
        {
            CancelConfirmationTimeout();

            var cleanInput = text.ToLowerInvariant().Trim();

            bool isYes = IsAffirmativeReply(cleanInput);
            bool isNo  = IsNegativeReply(cleanInput);

            if (isYes)
            {
                var action = PendingAction != PendingGameAction.None ? PendingAction : PendingGameAction.Install;
                var game = (PendingGame ?? _pendingGameToInstall)!;
                ResetConfirmationState();

                if (action == PendingGameAction.Uninstall)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Подтверждение получено. Удаляю '{game.Name}'.");
                    Console.ResetColor();

                    await ttsService.SpeakAsync($"Удаляю {game.Name}, сэр.");
                    _ = Task.Run(async () =>
                    {
                        if (uint.TryParse(game.AppId, out uint numericAppId))
                        {
                            await steamService.UninstallGameAsync(numericAppId, game.Title);
                        }
                    });
                }
                else if (action == PendingGameAction.Install)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Подтверждение получено. Начинаю установку '{game.Name}'.");
                    Console.ResetColor();

                    await ttsService.SpeakAsync($"Принято. Начинаю установку {game.Name}, сэр.");
                    await steamService.InstallGameAsync(game);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Подтверждение получено. Запускаю '{game.Name}'.");
                    Console.ResetColor();

                    await ttsService.SpeakAsync($"Запускаю {game.Name}, сэр.");
                    steamService.LaunchGame(game.AppId, out _);
                }
                return true;
            }

            if (isNo)
            {
                var action = PendingAction;
                ResetConfirmationState();

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Действие отменено пользователем.");
                Console.ResetColor();

                if (action == PendingGameAction.Uninstall)
                {
                    await ttsService.SpeakAsync("Удаление отменено, сэр.");
                }
                else
                {
                    await ttsService.SpeakAsync("Понял, отменяю.");
                }
                return true;
            }

            // Любая другая команда (не да/нет) — сбрасываем PendingAction и обрабатываем как новую обычную команду
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Получена другая команда. Сброс состояния подтверждения.");
            Console.ResetColor();

            ResetConfirmationState();
        }

        // ── B. Normal mode: check for install / start intent ──────────────────
        bool isInstallCommand = System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"\b(установи|скачай|поставь|install)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        bool isStartCommand = System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"\b(запусти|включи|открой)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!isInstallCommand && !isStartCommand)
            return false; // not our concern — let LLM pipeline handle it

        // Extract game query from the command phrase
        string query = gameQueryExtractor?.Invoke(text) ?? ExtractGameQuery(text);
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var result = await steamService.SearchGameAsync(query);
        if (result == null || !result.Found || result.Game == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Игра не найдена для запроса '{query}'.");
            Console.ResetColor();

            await ttsService.SpeakAsync("К сожалению, мне не удалось найти эту игру в каталоге, сэр.");
            return true;
        }

        if (!result.RequiresConfirmation)
        {
            // Exact match — install or launch immediately
            if (isInstallCommand)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Точное совпадение: '{result.Game.Name}'. Начинаю установку.");
                Console.ResetColor();

                await ttsService.SpeakAsync($"Инициирую установку {result.Game.Name}, сэр.");
                await steamService.InstallGameAsync(result.Game);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Точное совпадение: '{result.Game.Name}'. Запускаю.");
                Console.ResetColor();

                await ttsService.SpeakAsync($"Запускаю {result.Game.Name}, сэр.");
                steamService.LaunchGame(result.Game.AppId, out _);
            }
            return true;
        }

        // Probable match (0.60 <= Score < 0.82) — ask for confirmation
        SetPendingConfirmation(isInstallCommand ? PendingGameAction.Install : PendingGameAction.Start, result.Game, ttsService);

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Вероятный кандидат: '{result.Game.Name}' (conf: {result.Confidence:F2}). Запрашиваю подтверждение.");
        Console.ResetColor();

        await ttsService.SpeakAsync($"Вы имели в виду {result.Game.Name}, сэр?");
        return true;
    }

    // ─── Private Helpers ──────────────────────────────────────────────────────

    private static string ExtractGameQuery(string text)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b(установи|скачай|поставь|install|запусти|включи|открой|игру|игра|игры|мне|пожалуйста)\b",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        return clean;
    }

    private void StartConfirmationTimeout(IVoiceFeedbackService? ttsService = null)
    {
        CancelConfirmationTimeout();
        _confirmationTimeoutCts = new CancellationTokenSource();
        var token = _confirmationTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                // Timeout reached — reset state silently
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Тайм-аут подтверждения. Сброс состояния.");
                Console.ResetColor();

                ResetConfirmationState();
            }
            catch (TaskCanceledException)
            {
                // Cancelled normally — user replied in time
            }
        }, token);
    }

    private void CancelConfirmationTimeout()
    {
        _confirmationTimeoutCts?.Cancel();
        _confirmationTimeoutCts?.Dispose();
        _confirmationTimeoutCts = null;
    }

    // ─── Static helpers (preserved for backward compatibility) ─────────────────

    /// <summary>
    /// Checks whether the text contains any system exit commands.
    /// </summary>
    public static bool IsExitCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowerText = text.ToLowerInvariant();
        var exitKeywords = new[] { "закройся", "закрыть", "выход", "отключись", "заверши работу", "выключись", "стоп приложение" };

        return exitKeywords.Any(k => lowerText.Contains(k)) || lowerText.Contains("закрывайся");
    }

    /// <summary>
    /// Checks for direct exit commands and terminates application if matched.
    /// </summary>
    public static async Task<bool> CheckDirectExitCommandAsync(string text, IVoiceFeedbackService? feedbackService = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowerText = text.ToLowerInvariant();
        var exitKeywords = new[] { "закройся", "закрыть", "выход", "отключись", "заверши работу", "выключись", "стоп приложение" };

        if (exitKeywords.Any(keyword => lowerText.Contains(keyword)) || lowerText.Contains("закрывайся"))
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Перехвачена прямая команда закрытия приложения.");
            Console.WriteLine("Завершаю работу, сэр.");

            try
            {
                var tts = feedbackService ?? CompositeVoiceFeedbackService.Instance;
                if (tts != null)
                {
                    await tts.SpeakAsync("Завершаю работу, сэр.");
                }
                else
                {
                    CompositeVoiceFeedbackService.SpeakSynchronous("Завершаю работу, сэр.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JARVIS Error] Ошибка воспроизведения прощания: {ex.Message}");
            }

            await Task.Delay(300);
            Environment.Exit(0);
            return true;
        }

        return false;
    }
}
