using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Gem.Handlers;
using Gem.Services;
using Gem.Voice;

namespace Gem.Core;

/// <summary>
/// CLI runner handling early command line commands (--download-model, --test-features).
/// Extracted from Program.cs to maintain clean architecture and compact entry point.
/// </summary>
public static class CliRunner
{
    private static readonly JsonSerializerOptions JsonPrintOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Executes CLI arguments if matched. Returns true if CLI command handled and app should exit.
    /// </summary>
    public static async Task<bool> RunCliCommandsAsync(string[] args)
    {
        if (args.Contains("--download-model", StringComparer.OrdinalIgnoreCase))
        {
            await HandleDownloadModelAsync();
            return true;
        }

        if (args.Contains("--test-features", StringComparer.OrdinalIgnoreCase))
        {
            await RunFeatureTestsAsync();
            return true;
        }

        return false;
    }

    public static async Task HandleDownloadModelAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[+] Загрузка полноразмерной русской модели Vosk vosk-model-ru-0.42 (~1.5 ГБ)...");
        Console.WriteLine("    Это займёт несколько минут. Пожалуйста, не прерывайте процесс.");
        Console.ResetColor();

        var startTime = DateTime.UtcNow;
        var progress = new Progress<int>(percent =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            double mbRead = 1500.0 * percent / 100.0;
            string speedHint = elapsed > 3 && percent > 0
                ? $"  ~{mbRead / elapsed:F1} МБ/с"
                : "";
            Console.Write($"\r  Скачивание: [{percent,3}%] {mbRead:F0} МБ / ~1500 МБ{speedHint}   ");
        });

        try
        {
            string targetDir = Directory.Exists("Models/Vosk") ? "Models/Vosk" : VoskModelHelper.DefaultModelFolder;
            await VoskModelHelper.DownloadModelAsync(
                targetDirectory: targetDir,
                progress: progress);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[+] Модель vosk-model-ru-0.42 успешно скачана и распакована в '{targetDir}'!");
            Console.WriteLine("    Можно запускать: QuickStart.bat или Gem.exe");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[!] Ошибка скачивания модели: {ex.Message}");
            Console.ResetColor();
        }
    }

    public static Task RunFeatureTestsAsync() => FeatureSelfTester.RunFeatureTestsAsync();

    public static async Task RunDemoSuiteAsync(
        CommandRouter router,
        LlmIntentService llmService,
        IVoiceFeedbackService feedbackService,
        Gem.UI.OverlayWidget overlay,
        WeatherService? weatherService,
        Func<string, LlmIntentService, CommandRouter, IVoiceFeedbackService, Gem.UI.OverlayWidget?, WeatherService?, Task> pipelineRunner)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n--- ЗАПУСК ДЕМОНСТРАЦИОННОГО ТЕСТИРОВАНИЯ КОМАНД ---\n");
        Console.ResetColor();

        await ExecuteAndLogAsync(router, "1. Запрос текущей громкости", """{ "command": "volume", "args": { "action": "get" } }""");
        await ExecuteAndLogAsync(router, "2. Установка громкости на 40%", """{ "command": "volume", "args": { "action": "set", "level": 40 } }""");
        await ExecuteAndLogAsync(router, "3. Изменение громкости на +5%", """{ "command": "volume", "args": { "action": "change", "delta": 5 } }""");
        await ExecuteAndLogAsync(router, "4. Запуск приложения 'notepad'", """{ "command": "app", "args": { "action": "start", "name": "notepad" } }""");
        await Task.Delay(1000);
        await ExecuteAndLogAsync(router, "5. Проверка статуса 'notepad'", """{ "command": "app", "args": { "action": "status", "name": "notepad" } }""");
        await ExecuteAndLogAsync(router, "6. Закрытие приложения 'notepad'", """{ "command": "app", "args": { "action": "close", "name": "notepad", "force": false } }""");
        await ExecuteAndLogAsync(router, "7. Симуляция горячей клавиши (SendInput)", """{ "command": "hotkey", "args": { "keys": ["shift"], "delayMs": 50 } }""");

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("[TEST] 8. Тест естественного языка JARVIS (LLM -> Router -> TTS): \"прибавь звук\"");
        Console.ResetColor();
        await pipelineRunner("прибавь звук", llmService, router, feedbackService, overlay, weatherService);

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("[TEST] 9. Тест погоды JARVIS: \"какая погода\"");
        Console.ResetColor();
        await pipelineRunner("какая погода", llmService, router, feedbackService, overlay, weatherService);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n--- ДЕМОНСТРАЦИЯ ЗАВЕРШЕНА ---\n");
        Console.ResetColor();
    }

    private static async Task ExecuteAndLogAsync(CommandRouter router, string description, string json)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"[TEST] {description}:");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  JSON: {json}");
        Console.ResetColor();

        var result = await router.RouteAsync(json);
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  [OK] ");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  [FAIL] ");
        }
        Console.WriteLine(result.Message);
        Console.ResetColor();
    }
}
