using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Gem.Core;

namespace Gem.Services.App;

/// <summary>
/// Centralized dispatcher for executing assistant responses (Weather, System Exit, Dialog, Media, Timer).
/// </summary>
public static class JarvisResponseDispatcher
{
    public static async Task HandleJarvisResponseAsync(
        JarvisResponse response,
        CommandRouter router,
        IVoiceFeedbackService feedbackService,
        WeatherService? weatherService = null)
    {
        var commandRequest = response.CommandRequest;

        // 1. ПОГОДА: commandRequest?.command == "weather"
        if (commandRequest != null && commandRequest.Command.Equals("weather", StringComparison.OrdinalIgnoreCase))
        {
            var weather = weatherService ?? WeatherService.Instance ?? new WeatherService();

            string? targetCity = null;
            if (commandRequest.Args.ValueKind == JsonValueKind.Object &&
                commandRequest.Args.TryGetProperty("city", out var cityProp) &&
                cityProp.ValueKind == JsonValueKind.String)
            {
                targetCity = cityProp.GetString();
            }

            string weatherReport = await weather.GetCurrentWeatherReportAsync(targetCity);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($">>> [JARVIS] Погода: \"{weatherReport}\"");
            Console.ResetColor();

            await feedbackService.SpeakAsync(weatherReport);
            return;
        }

        // 2. ВЫХОД ИЗ ДЖАРВИСА: commandRequest?.command == "system" && args.name == "jarvis" && args.action == "close"
        if (commandRequest != null && IsSystemExitRequest(commandRequest))
        {
            string farewell = !string.IsNullOrWhiteSpace(response.Reply)
                ? response.Reply
                : "До свидания, сэр.";

            ShutdownJarvis(farewell, feedbackService);
            return;
        }

        // 3. ОБЫЧНЫЙ ДИАЛОГ: commandRequest == null -> озвучить reply в неблокирующем режиме
        if (commandRequest == null)
        {
            if (!string.IsNullOrWhiteSpace(response.Reply))
            {
                await feedbackService.SpeakAsync(response.Reply);
            }
            return;
        }

        // 5. МЕДИА-КЛАВИШИ: commandRequest?.command == "media"
        if (commandRequest != null && commandRequest.Command.Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            var mediaResult = await router.RouteAsync(commandRequest);
            if (mediaResult.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] {mediaResult.Message}");
            }
            else
            {
                ExecuteMediaFallback(commandRequest.Args);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [OK] Simulated media key via MediaKeyService fallback.");
            }
            Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(response.Reply))
            {
                await feedbackService.SpeakAsync(response.Reply);
            }
            return;
        }

        // 5.1 ТАЙМЕРЫ И НАПОМИНАНИЯ: commandRequest?.command == "timer"
        if (commandRequest != null && commandRequest.Command.Equals("timer", StringComparison.OrdinalIgnoreCase))
        {
            var timerResult = await router.RouteAsync(commandRequest);
            if (timerResult.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] {timerResult.Message}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [FAIL] {timerResult.Message}");
            }
            Console.ResetColor();

            string replyToSpeak = response.Reply ?? timerResult.Message ?? string.Empty;
            if (commandRequest.Args.ValueKind == JsonValueKind.Object &&
                commandRequest.Args.TryGetProperty("action", out var actProp) &&
                actProp.GetString() == "status" &&
                !string.IsNullOrWhiteSpace(timerResult.Message))
            {
                replyToSpeak = timerResult.Message;
            }

            if (!string.IsNullOrWhiteSpace(replyToSpeak))
            {
                await feedbackService.SpeakAsync(replyToSpeak);
            }
            return;
        }

        // 6. ДРУГИЕ КОМАНДЫ (volume, hotkey, close app, fallback)
        var commandResult = await router.RouteAsync(commandRequest!);

        if (commandResult.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] {commandResult.Message}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {commandResult.Message}");
        }
        Console.ResetColor();

        if (!JarvisOrchestrator.Instance.HasPendingAction && !string.IsNullOrWhiteSpace(response.Reply))
        {
            await feedbackService.SpeakAsync(response.Reply);
        }
    }

    public static bool IsSystemExitRequest(CommandRequest request)
    {
        if (request.Args.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool isSystemOrApp = request.Command.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                             request.Command.Equals("app", StringComparison.OrdinalIgnoreCase);

        if (!isSystemOrApp) return false;

        string action = request.Args.TryGetProperty("action", out var actProp) ? actProp.GetString() ?? "" : "";
        string name = request.Args.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

        bool isCloseAction = action.Equals("close", StringComparison.OrdinalIgnoreCase) ||
                             action.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                             action.Equals("kill", StringComparison.OrdinalIgnoreCase) ||
                             action.Equals("exit", StringComparison.OrdinalIgnoreCase);

        return isCloseAction && AppCatalog.IsExitTarget(name);
    }

    /// <summary>
    /// Озвучивает прощание и корректно завершает работу процесса приложения.
    /// </summary>
    public static void ShutdownJarvis(string? farewellPhrase = null, IVoiceFeedbackService? voiceFeedback = null)
    {
        string phrase = !string.IsNullOrWhiteSpace(farewellPhrase)
            ? farewellPhrase
            : "До свидания, сэр.";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[JarvisResponseDispatcher] Завершение работы JARVIS: \"{phrase}\"");
        Console.ResetColor();

        // 1. Озвучка прощания: с таймаутом 5 секунд для защиты от зависания семафора
        try
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var tts = voiceFeedback ?? CompositeVoiceFeedbackService.Instance;
            if (tts != null)
            {
                tts.SpeakAsync(phrase, shutdownCts.Token).GetAwaiter().GetResult();
            }
            else
            {
                CompositeVoiceFeedbackService.SpeakSynchronous(phrase);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[JarvisResponseDispatcher Error] Ошибка воспроизведения прощания: {ex.Message}");
            Console.ResetColor();
        }

        // 2. Корректное завершение процесса:
        // Только ПОСЛЕ окончания речи закрыть приложение через Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown())
        try
        {
            if (Application.Current != null)
            {
                Action closeWindowsAndShutdown = () =>
                {
                    try
                    {
                        foreach (Window window in Application.Current.Windows.Cast<Window>().ToList())
                        {
                            try
                            {
                                window.Close();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[JarvisResponseDispatcher Warning] Ошибка закрытия окна: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[JarvisResponseDispatcher Warning] Ошибка перечисления окон: {ex.Message}");
                    }

                    try
                    {
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[JarvisResponseDispatcher Warning] Ошибка Application.Current.Shutdown: {ex.Message}");
                    }
                };

                if (Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(closeWindowsAndShutdown);
                }
                else
                {
                    closeWindowsAndShutdown();
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[JarvisResponseDispatcher Warning] Ошибка при завершении WPF: {ex.Message}");
            Console.ResetColor();
        }

        // 3. При необходимости вызвать Environment.Exit(0) в качестве финального шага
        try
        {
            Thread.Sleep(300);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JarvisResponseDispatcher Warning] Ошибка Environment.Exit: {ex.Message}");
        }
    }

    private static void ExecuteMediaFallback(JsonElement args)
    {
        string action = "play_pause";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var actProp))
        {
            action = actProp.GetString()?.ToLowerInvariant() ?? "play_pause";
        }

        switch (action)
        {
            case "next":
            case "next_track":
                MediaKeyService.NextTrack();
                break;
            case "prev":
            case "previous":
            case "prev_track":
                MediaKeyService.PreviousTrack();
                break;
            case "stop":
                MediaKeyService.Stop();
                break;
            default:
                MediaKeyService.PlayPause();
                break;
        }
    }
}
