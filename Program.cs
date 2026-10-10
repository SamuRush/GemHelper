using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Gem.UI;
using Gem.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gem;

public static class Program
{
    private static readonly JsonSerializerOptions JsonPrintOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Console encoding setup error: {ex.Message}");
        }

        // Attach to parent terminal console if launched from cmd/powershell
        if (!Console.IsOutputRedirected && AttachConsole(ATTACH_PARENT_PROCESS))
        {
            try
            {
                var standardOutput = new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(standardOutput);
                var standardError = new StreamWriter(Console.OpenStandardError(), System.Text.Encoding.UTF8) { AutoFlush = true };
                Console.SetError(standardError);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Console redirection error: {ex.Message}");
            }
        }

        PrintBanner();

        // 1. Early CLI commands handler (extracted to Core/CliRunner.cs)
        if (CliRunner.RunCliCommandsAsync(args).GetAwaiter().GetResult())
        {
            return;
        }

        // 2. Build Dependency Injection Container
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        services.AddJarvisCore(configuration);
        services.AddJarvisTts(configuration);
        services.AddJarvisHandlers();

        // 3. Initialize WPF Application & OverlayWidget
        var app = new Application();
        var overlay = new OverlayWidget();
        services.AddSingleton(overlay);

        using var serviceProvider = services.BuildServiceProvider();

        var settingsData = serviceProvider.GetRequiredService<AppSettingsData>();
        AppHandler.SetSteamGames(settingsData.GameAliases);

        var router = serviceProvider.ConfigureJarvisRouter();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] Зарегистрированы обработчики команд: {string.Join(", ", router.RegisteredCommands)}");
        Console.ResetColor();

        var voiceFeedback = serviceProvider.GetRequiredService<IVoiceFeedbackService>();
        var weatherService = serviceProvider.GetRequiredService<WeatherService>();
        var timerService = serviceProvider.GetRequiredService<ITimerService>();
        var llmService = serviceProvider.GetRequiredService<LlmIntentService>();

        overlay.AttachServices(llmService);
        overlay.AttachVoiceFeedback(voiceFeedback);
        llmService.OnThinkingStarted += () => overlay.SetState(JarvisState.Thinking);

        // 4. Initialize background voice listener (Vosk)
        VoiceListener? voiceListener = null;
        string? resolvedModelPath = VoskModelHelper.FindModelDirectory(settingsData.VoskModelPath);
        string voskModelPath = resolvedModelPath
            ?? (Directory.Exists(settingsData.VoskModelPath)
                ? settingsData.VoskModelPath
                : VoskModelHelper.DefaultModelFolder);

        if (Directory.Exists(voskModelPath) && VoskModelHelper.IsModelAvailable(voskModelPath))
        {
            try
            {
                voiceListener = new VoiceListener(modelPath: voskModelPath, wakeWords: settingsData.WakeWords?.ToArray());
                overlay.AttachVoiceListener(voiceListener);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Ошибка создания голосового слушателя: {ex.Message}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[i] Папка модели Vosk '{voskModelPath}' не найдена или не содержит файлов модели. Фоновый микрофон ожидает загрузки модели.");
            Console.WriteLine("    Чтобы загрузить полноразмерную русскую модель Vosk vosk-model-ru-0.42 (~1.5 ГБ), выполните: QuickStart.bat или Gem.exe --download-model");
            Console.ResetColor();
        }

        // 5. Connect VoiceListener events
        if (voiceListener != null)
        {
            voiceListener.OnWakeWordDetected += (wakeWord) =>
            {
                overlay.SetState(JarvisState.Listening);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\n>>> [JARVIS] [Слушаю...] (Wake-word: '{wakeWord}')");
                Console.ResetColor();
            };

            voiceListener.OnCommandSpoken += (spokenText) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n>>> [ГОЛОС] Пользователь: \"{spokenText}\"");
                Console.ResetColor();

                _ = Task.Run(async () =>
                {
                    await ProcessJarvisPipelineAsync(spokenText, llmService, router, voiceFeedback, overlay, weatherService);
                });
            };

            voiceListener.OnStatusChanged += (state, reason) =>
            {
                if (state == VoiceListenerState.ListeningForCommand)
                {
                    overlay.SetState(JarvisState.Listening);
                }
                else if (state == VoiceListenerState.WaitingForWakeWord &&
                    overlay.CurrentState != JarvisState.Thinking &&
                    overlay.CurrentState != JarvisState.Action)
                {
                    overlay.SetState(JarvisState.Idle);
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[VoiceListener: {state}] {reason}");
                Console.ResetColor();
            };

            try
            {
                voiceListener.Start();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("[+] Фоновый слушатель микрофона активен. Скажите 'джарвис <команда>'...");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Ошибка старта микрофона: {ex.Message}");
                Console.ResetColor();
            }
        }

        // 6. Индексация библиотеки Steam
        _ = SteamService.Instance;

        // 7. Оповещение о готовности всех систем
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Все системы инициализированы. Готов к работе.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await voiceFeedback.SpeakAsync("Все системы инициализированы. Я на связи, сэр.");
        });

        // 8. Start background console REPL & Demo suite
        Task.Run(async () =>
        {
            try
            {
                bool isDemo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
                if (isDemo)
                {
                    await RunDemoSuiteAsync(router, llmService, voiceFeedback, overlay, weatherService);
                }

                await RunConsoleLoopAsync(router, llmService, voiceFeedback, overlay, weatherService);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Console Task Error]: {ex.Message}");
            }
        });

        // 9. Run WPF Application on main STA thread
        overlay.Closed += (s, e) =>
        {
            (timerService as IDisposable)?.Dispose();
            voiceListener?.Dispose();
            (voiceFeedback as IDisposable)?.Dispose();
            Environment.Exit(0);
        };

        app.Run(overlay);
    }

    /// <summary>
    /// Executes the full JARVIS pipeline:
    /// Visual Overlay (Thinking) -> LLM Interpreter -> CommandRouter / AppHandler -> TTS Feedback -> Overlay (Action -> Idle)
    /// </summary>
    public static async Task ProcessJarvisPipelineAsync(
        string inputPhrase,
        LlmIntentService llmService,
        CommandRouter router,
        IVoiceFeedbackService feedbackService,
        OverlayWidget? overlay = null,
        WeatherService? weatherService = null)
    {
        try
        {
            VoiceListener.Instance?.NotifyProcessingStarted();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[JARVIS] Обработка запроса: \"{inputPhrase}\"");
            Console.ResetColor();

            // 1. Проверка системных команд на выход перед обращением к LLM
            if (await JarvisOrchestrator.CheckDirectExitCommandAsync(inputPhrase, feedbackService))
            {
                return;
            }

            overlay?.SetState(JarvisState.Thinking);

            var jarvisResponse = await llmService.InterpretAsync(inputPhrase);

            overlay?.SetState(JarvisState.Action);

            if (!string.IsNullOrWhiteSpace(jarvisResponse.Reply))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($">>> [JARVIS]: \"{jarvisResponse.Reply}\"");
                Console.ResetColor();
            }

            // Centralized execution in AppHandler (Weather, Exit, Dialog, Router Commands)
            await AppHandler.HandleJarvisResponseAsync(jarvisResponse, router, feedbackService, weatherService);

            // Smooth transition to Idle or Listening depending on confirmation state
            if (JarvisOrchestrator.Instance.HasPendingAction)
            {
                overlay?.SetState(JarvisState.Listening);
            }
            else
            {
                overlay?.SetState(JarvisState.Idle);
            }
        }
        finally
        {
            if (VoiceListener.Instance?.IsProcessing == true)
            {
                await Task.Delay(250);
                VoiceListener.Instance?.NotifyProcessingFinished();
            }
        }
    }

    private static async Task RunConsoleLoopAsync(
        CommandRouter router,
        LlmIntentService llmService,
        IVoiceFeedbackService voiceFeedback,
        OverlayWidget overlay,
        WeatherService? weatherService = null)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("Интерактивная консоль JARVIS с виджетом оверлея WPF.");
        Console.WriteLine(" - Виджет в правом нижнем углу экрана (перетаскивайте левой кнопкой мыши)");
        Console.WriteLine(" - Двойной клик по виджету или шестеренке открывает настройки");
        Console.WriteLine(" - Введите JSON: { \"command\": \"...\", \"args\": { ... } }");
        Console.WriteLine(" - Или напишите текстовую команду (напр. 'какая погода', 'закрой блокнот')");
        Console.WriteLine(" - Введите 'exit' или 'quit' для выхода.");
        Console.WriteLine("=======================================================\n");
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("JARVIS > ");
            Console.ResetColor();

            string? line = Console.ReadLine();
            if (line == null)
            {
                break;
            }

            line = line.Trim().Trim('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                AppHandler.ShutdownJarvis("До свидания, сэр.", voiceFeedback);
                break;
            }

            if (line.StartsWith('{'))
            {
                overlay.SetState(JarvisState.Action);
                var result = await router.RouteAsync(line);
                PrintResult(result);
                overlay.SetState(JarvisState.Idle);
            }
            else
            {
                await ProcessJarvisPipelineAsync(line, llmService, router, voiceFeedback, overlay, weatherService);
            }
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║      JARVIS Minimalist HUD Overlay & Assistant (.NET 8)      ║");
        Console.WriteLine("║   - WPF Holographic Core Overlay (Idle/Listen/Think/Action)  ║");
        Console.WriteLine("║   - Windows 11 Dark Settings Dialog                          ║");
        Console.WriteLine("║   - Open-Meteo Weather Forecast Service                      ║");
        Console.WriteLine("║   - Steam Games Dynamic Catalog & Process Kill Protection    ║");
        Console.WriteLine("║   - Volume (NAudio CoreAudioApi)                             ║");
        Console.WriteLine("║   - Hotkey Simulation (P/Invoke SendInput)                   ║");
        Console.WriteLine("║   - Voice Listener (Vosk: wake-word 'джарвис' + silence)     ║");
        Console.WriteLine("║   - LLM Intent Interpreter (OpenAI-compatible / LM Studio)   ║");
        Console.WriteLine("║   - Voice Feedback (Hybrid: Edge-TTS -> System.Speech SAPI5) ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");
        Console.ResetColor();
    }

    private static async Task RunDemoSuiteAsync(
        CommandRouter router,
        LlmIntentService llmService,
        IVoiceFeedbackService feedbackService,
        OverlayWidget overlay,
        WeatherService? weatherService = null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n--- ЗАПУСК ДЕМОНСТРАЦИОННОГО ТЕСТИРОВАНИЯ КОМАНД ---\n");
        Console.ResetColor();

        await ExecuteAndLogAsync(router,
            "1. Запрос текущей громкости",
            """{ "command": "volume", "args": { "action": "get" } }""");

        await ExecuteAndLogAsync(router,
            "2. Установка громкости на 40%",
            """{ "command": "volume", "args": { "action": "set", "level": 40 } }""");

        await ExecuteAndLogAsync(router,
            "3. Изменение громкости на +5%",
            """{ "command": "volume", "args": { "action": "change", "delta": 5 } }""");

        await ExecuteAndLogAsync(router,
            "4. Запуск приложения 'notepad'",
            """{ "command": "app", "args": { "action": "start", "name": "notepad" } }""");

        await Task.Delay(1000);

        await ExecuteAndLogAsync(router,
            "5. Проверка статуса 'notepad'",
            """{ "command": "app", "args": { "action": "status", "name": "notepad" } }""");

        await ExecuteAndLogAsync(router,
            "6. Закрытие приложения 'notepad'",
            """{ "command": "app", "args": { "action": "close", "name": "notepad", "force": false } }""");

        await ExecuteAndLogAsync(router,
            "7. Симуляция горячей клавиши (SendInput)",
            """{ "command": "hotkey", "args": { "keys": ["shift"], "delayMs": 50 } }""");

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("[TEST] 8. Тест естественного языка JARVIS (LLM -> Router -> TTS):");
        Console.WriteLine("  Фраза: \"прибавь звук\"");
        Console.ResetColor();
        await ProcessJarvisPipelineAsync("прибавь звук", llmService, router, feedbackService, overlay, weatherService);

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("[TEST] 9. Тест погоды JARVIS:");
        Console.WriteLine("  Фраза: \"какая погода\"");
        Console.ResetColor();
        await ProcessJarvisPipelineAsync("какая погода", llmService, router, feedbackService, overlay, weatherService);

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
        PrintResult(result);
        Console.WriteLine();
    }

    private static void PrintResult(CommandResult result)
    {
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

        if (result.Data != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            string dataJson = JsonSerializer.Serialize(result.Data, JsonPrintOptions);
            Console.WriteLine($"  Data: {dataJson}");
            Console.ResetColor();
        }
    }
}
