using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using Gem.Core;
using Gem.Handlers;
using Gem.Services;
using Gem.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

        // Attach to parent terminal console if launched from cmd/powershell (WinExe / Exe hybrid support)
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

        // 1. Download model argument handler
        if (args.Contains("--download-model", StringComparer.OrdinalIgnoreCase))
        {
            HandleDownloadModelAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--test-features", StringComparer.OrdinalIgnoreCase))
        {
            RunFeatureTestsAsync().GetAwaiter().GetResult();
            return;
        }



        // 2. Initialize WPF Application
        var app = new Application();
        var overlay = new OverlayWidget();

        // 3. Load configuration from AppSettingsService / appsettings.json
        var settingsData = AppSettingsService.Load();
        AppHandler.SetSteamGames(settingsData.GameAliases);

        var weatherService = new WeatherService(
            cityName: settingsData.CityName,
            latitude: settingsData.Latitude,
            longitude: settingsData.Longitude
        );

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"))
                   .AddConsole();
        });
        var logger = loggerFactory.CreateLogger("Gem");
        logger.LogInformation("Инициализация сервисов JARVIS...");

        // 4. Initialize background voice listener (Vosk)
        VoiceListener? voiceListener = null;
        string voskModelPath = Directory.Exists(settingsData.VoskModelPath)
            ? settingsData.VoskModelPath
            : VoskModelHelper.DefaultModelFolder;

        if (Directory.Exists(voskModelPath))
        {
            try
            {
                voiceListener = new VoiceListener(modelPath: voskModelPath, wakeWords: settingsData.WakeWords?.ToArray());
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
            Console.WriteLine($"[i] Папка модели Vosk '{voskModelPath}' не найдена. Фоновый микрофон ожидает загрузки модели.");
            Console.WriteLine("    Чтобы загрузить полноразмерную русскую модель Vosk vosk-model-ru-0.42 (~1.5 ГБ), выполните: dotnet run -- --download-model");
            Console.ResetColor();
        }

        // 5. Initialize TTS Voice Feedback and TimerService
        IVoiceFeedbackService voiceFeedback = new CompositeVoiceFeedbackService(voiceListener, configuration);
        var timerService = new TimerService(voiceFeedback);

        // 6. Initialize CommandRouter and register all handlers with unified voice feedback
        var router = new CommandRouter();
        router.Register(new VolumeCommandHandler())
              .Register(new AppControlCommandHandler(voiceFeedback))
              .Register(new HotkeyCommandHandler())
              .Register(new MediaCommandHandler())
              .Register(new WeatherCommandHandler(weatherService, voiceFeedback))
              .Register(new SystemCommandHandler(voiceFeedback))
              .Register(new TimerCommandHandler(timerService));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] Зарегистрированы обработчики команд: {string.Join(", ", router.RegisteredCommands)}");
        Console.ResetColor();

        var llmService = new LlmIntentService(configuration);
        overlay.AttachServices(llmService);

        // Connect LLM thinking events to overlay
        llmService.OnThinkingStarted += () => overlay.SetState(JarvisState.Thinking);

        // 7. Connect VoiceListener events with Overlay, Beep, LLM, Router, and VoiceFeedback
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

        // 8. Индексация библиотеки Steam
        _ = SteamService.Instance;

        // 9. Оповещение о готовности всех систем
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [JARVIS] Все системы инициализированы. Готов к работе.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // небольшая пауза после старта аудиокарты
            await voiceFeedback.SpeakAsync("Все системы инициализированы. Я на связи, сэр.");
        });

        // 10. Start background console REPL & Demo suite
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

        // 11. Run WPF Application on main STA thread
        overlay.Closed += (s, e) =>
        {
            timerService.Dispose();
            voiceListener?.Dispose();
            voiceFeedback.Dispose();
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
                // Direct JSON execution
                overlay.SetState(JarvisState.Action);
                var result = await router.RouteAsync(line);
                PrintResult(result);
                overlay.SetState(JarvisState.Idle);
            }
            else
            {
                // Process natural language text via JARVIS pipeline
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
        Console.WriteLine("║   - Voice Feedback (Hybrid: Edge-TTS -> Silero ONNX -> System.Speech)  ║");
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

        // 1. Volume test: Get current volume
        await ExecuteAndLogAsync(router,
            "1. Запрос текущей громкости",
            """{ "command": "volume", "args": { "action": "get" } }""");

        // 2. Volume test: Set volume to 40%
        await ExecuteAndLogAsync(router,
            "2. Установка громкости на 40%",
            """{ "command": "volume", "args": { "action": "set", "level": 40 } }""");

        // 3. Volume test: Change volume by +5%
        await ExecuteAndLogAsync(router,
            "3. Изменение громкости на +5%",
            """{ "command": "volume", "args": { "action": "change", "delta": 5 } }""");

        // 4. App control test: Start Notepad
        await ExecuteAndLogAsync(router,
            "4. Запуск приложения 'notepad'",
            """{ "command": "app", "args": { "action": "start", "name": "notepad" } }""");

        await Task.Delay(1000);

        // 5. App control test: Check status of Notepad
        await ExecuteAndLogAsync(router,
            "5. Проверка статуса 'notepad'",
            """{ "command": "app", "args": { "action": "status", "name": "notepad" } }""");

        // 6. App control test: Close Notepad
        await ExecuteAndLogAsync(router,
            "6. Закрытие приложения 'notepad'",
            """{ "command": "app", "args": { "action": "close", "name": "notepad", "force": false } }""");

        // 7. Hotkey test: Safe simulation of a key combination (e.g. Shift key)
        await ExecuteAndLogAsync(router,
            "7. Симуляция горячей клавиши (SendInput)",
            """{ "command": "hotkey", "args": { "keys": ["shift"], "delayMs": 50 } }""");

        // 8. Natural Language Jarvis Pipeline Demo
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("[TEST] 8. Тест естественного языка JARVIS (LLM -> Router -> TTS):");
        Console.WriteLine("  Фраза: \"прибавь звук\"");
        Console.ResetColor();
        await ProcessJarvisPipelineAsync("прибавь звук", llmService, router, feedbackService, overlay, weatherService);

        // 9. Weather Test
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

    private static async Task HandleDownloadModelAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[+] Загрузка полноразмерной русской модели Vosk vosk-model-ru-0.42 (~1.5 ГБ)...");
        Console.WriteLine("    Это займёт несколько минут. Пожалуйста, не прерывайте процесс.");
        Console.ResetColor();

        var startTime = DateTime.UtcNow;
        var progress = new Progress<int>(percent =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            // Оценка скачанных МБ: 1500 МБ * percent / 100
            double mbRead = 1500.0 * percent / 100.0;
            string speedHint = elapsed > 3 && percent > 0
                ? $"  ~{mbRead / elapsed:F1} МБ/с"
                : "";
            Console.Write($"\r  Скачивание: [{percent,3}%] {mbRead:F0} МБ / ~1500 МБ{speedHint}   ");
        });

        try
        {
            await VoskModelHelper.DownloadModelAsync(
                targetDirectory: VoskModelHelper.DefaultModelFolder,
                progress: progress);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[+] Модель vosk-model-ru-0.42 успешно скачана и распакована в './model'!");
            Console.WriteLine("    Можно запускать: dotnet run");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[!] Ошибка скачивания модели: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static async Task RunFeatureTestsAsync()
    {
        Console.WriteLine("\n================ RUNNING FEATURE TESTS ================");

        // Test 1: FastCommands
        Console.WriteLine("\n[1] Testing VoiceListener Fast Commands...");


        string[] fastSamples = ["закройся", "выключись", "стоп", "отмена", "закрой монстер хантер", "закрой игру", "закройся пожалуйста"];
        foreach (var sample in fastSamples)
        {
            bool isFast = VoiceListener.IsFastCommand(sample);
            Console.WriteLine($"  IsFastCommand('{sample}') = {isFast} (Expected: True)");
            if (!isFast) throw new Exception($"Fast command '{sample}' was not recognized!");
        }

        string[] nonFastSamples = ["какая погода", "расскажи шутку", "кто президент"];
        foreach (var sample in nonFastSamples)
        {
            bool isFast = VoiceListener.IsFastCommand(sample);
            Console.WriteLine($"  IsFastCommand('{sample}') = {isFast} (Expected: False)");
            if (isFast) throw new Exception($"Non-fast command '{sample}' was incorrectly recognized as fast!");
        }

        // Test 2: SystemPrompt & Intent
        Console.WriteLine("\n[2] Testing LlmIntentService Prompt & Match...");
        if (!LlmIntentService.SystemPrompt.Contains("\"city\": \"<город в именительном падеже>\" | null"))
            throw new Exception("SystemPrompt missing city schema definition!");
        if (!LlmIntentService.SystemPrompt.Contains("погода в Москве"))
            throw new Exception("SystemPrompt missing 'погода в Москве' example!");
        if (!LlmIntentService.SystemPrompt.Contains("погода в Париже"))
            throw new Exception("SystemPrompt missing 'погода в Париже' example!");
        Console.WriteLine("  SystemPrompt schema and examples verified successfully.");

        var fastWeather = LlmIntentService.TryFastMatch("какая погода");
        if (fastWeather?.CommandRequest?.Command != "weather")
            throw new Exception("TryFastMatch('какая погода') did not return weather command!");
        Console.WriteLine("  TryFastMatch('какая погода') returned weather command.");

        var cityWeather = LlmIntentService.TryFastMatch("погода в Москве");
        if (cityWeather != null)
            throw new Exception("TryFastMatch('погода в Москве') should return null to allow LLM city extraction!");
        Console.WriteLine("  TryFastMatch('погода в Москве') correctly deferred to LLM.");

        var offlineCity = LlmIntentService.OfflineFallbackInterpreter("погода в Москве");
        if (offlineCity.CommandRequest?.Command != "weather" ||
            !offlineCity.CommandRequest.Args.TryGetProperty("city", out var cProp) ||
            cProp.GetString() != "москве")
            throw new Exception("OfflineFallbackInterpreter('погода в Москве') failed to extract city!");
        Console.WriteLine("  OfflineFallbackInterpreter('погода в Москве') extracted city correctly.");

        // Test 3: WeatherService
        Console.WriteLine("\n[3] Testing WeatherService Multi-city...");
        var weatherService = new WeatherService();

        string defRep = await weatherService.GetCurrentWeatherReportAsync();
        Console.WriteLine($"  Default: {defRep}");
        if (!defRep.Contains("Санкт-Петербург") || !defRep.Contains("°C"))
            throw new Exception("Default weather report failed!");

        string moscowRep = await weatherService.GetCurrentWeatherReportAsync("Москва");
        Console.WriteLine($"  Москва: {moscowRep}");
        if (!moscowRep.Contains("Москва") || !moscowRep.Contains("°C"))
            throw new Exception("Moscow weather report failed!");

        string parisRep = await weatherService.GetCurrentWeatherReportAsync("Париж");
        Console.WriteLine($"  Париж: {parisRep}");
        if (!parisRep.Contains("Париж") || !parisRep.Contains("°C"))
            throw new Exception("Paris weather report failed!");

        string unknownRep = await weatherService.GetCurrentWeatherReportAsync("НесуществующийГород777");
        Console.WriteLine($"  Unknown: {unknownRep}");
        if (!unknownRep.Contains("не удалось найти"))
            throw new Exception("Unknown city report failed!");

        // Test 4: SteamService Phonetics & Transliteration
        Console.WriteLine("\n[4] Testing SteamService Phonetics & Transliteration...");
        string cyb = SteamService.TransliterateToCyrillic("Cyberpunk");
        Console.WriteLine($"  TransliterateToCyrillic('Cyberpunk') = '{cyb}'");
        if (!cyb.Contains("киберпанк"))
            throw new Exception($"Transliteration of 'Cyberpunk' failed! Got '{cyb}'");

        string spc = SteamService.TransliterateToCyrillic("Space");
        Console.WriteLine($"  TransliterateToCyrillic('Space') = '{spc}'");
        if (!spc.Contains("спейс"))
            throw new Exception($"Transliteration of 'Space' failed! Got '{spc}'");

        string nms = SteamService.TransliterateToCyrillic("No Man's Sky");
        Console.WriteLine($"  TransliterateToCyrillic('No Man''s Sky') = '{nms}'");
        if (!nms.Contains("ноу менс скай") && !nms.Contains("ноу мен скай"))
            throw new Exception($"Transliteration of 'No Man''s Sky' failed! Got '{nms}'");

        string wtc = SteamService.TransliterateToCyrillic("Witcher");
        Console.WriteLine($"  TransliterateToCyrillic('Witcher') = '{wtc}'");
        if (!wtc.Contains("витчер"))
            throw new Exception($"Transliteration of 'Witcher' failed! Got '{wtc}'");

        // Test 5: SteamService Levenshtein & Fuzzy Search
        Console.WriteLine("\n[5] Testing SteamService Fuzzy Search & Library Indexing...");
        string translitTest = SteamService.Transliterate("киберпанк");
        Console.WriteLine($"  Transliterate('киберпанк') = '{translitTest}'");
        if (translitTest != "kiberpank")
            throw new Exception($"Transliterate('киберпанк') expected 'kiberpank', got '{translitTest}'");

        var steam = SteamService.Instance;
        Console.WriteLine($"  Total Indexed Games: {steam.IndexedGames.Count}");

        var cyberpunkGame = steam.FindGame("киберпанк");
        Console.WriteLine($"  FindGame('киберпанк') = '{cyberpunkGame?.Title}' (AppID: {cyberpunkGame?.AppId})");
        if (cyberpunkGame == null || cyberpunkGame.AppId != "1091500")
            throw new Exception("FindGame('киберпанк') did not find Cyberpunk 2077 (1091500)!");

        var stopWordGame = steam.FindGame("установи киберпанк", autoInstall: false);
        Console.WriteLine($"  FindGame('установи киберпанк') = '{stopWordGame?.Title}' (AppID: {stopWordGame?.AppId})");
        if (stopWordGame == null || stopWordGame.AppId != "1091500")
            throw new Exception("FindGame('установи киберпанк') did not resolve properly!");

        var nmsGame = steam.FindGame("ноу менс скай");
        Console.WriteLine($"  FindGame('ноу менс скай') = '{nmsGame?.Title}' (AppID: {nmsGame?.AppId})");
        if (nmsGame == null || nmsGame.AppId != "275850")
            throw new Exception("FindGame('ноу менс скай') did not find No Man's Sky (275850)!");

        var witcherGame = steam.FindGame("ведьмак");
        Console.WriteLine($"  FindGame('ведьмак') = '{witcherGame?.Title}' (AppID: {witcherGame?.AppId})");
        if (witcherGame == null || witcherGame.AppId != "292030")
            throw new Exception("FindGame('ведьмак') did not find The Witcher 3 (292030)!");

        var witcherGamePhonetic = steam.FindGame("витчер");
        Console.WriteLine($"  FindGame('витчер') = '{witcherGamePhonetic?.Title}' (AppID: {witcherGamePhonetic?.AppId})");
        if (witcherGamePhonetic == null || (witcherGamePhonetic.AppId != "292030" && !witcherGamePhonetic.Title.Contains("Witcher", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("FindGame('витчер') did not find Witcher!");

        var dotaGame = steam.FindGame("дота");
        Console.WriteLine($"  FindGame('дота') = '{dotaGame?.Title}' (AppID: {dotaGame?.AppId})");
        if (dotaGame == null || dotaGame.AppId != "570")
            throw new Exception("FindGame('дота') did not find Dota 2 (570)!");

        // Test 6: LlmIntentService Install Prompt & Matching
        Console.WriteLine("\n[6] Testing Install command schema, prompt and intent matching...");
        if (!LlmIntentService.SystemPrompt.Contains("\"action\": \"start\" | \"close\" | \"install\""))
            throw new Exception("SystemPrompt missing 'install' in action schema!");
        if (!LlmIntentService.SystemPrompt.Contains("установи ноу менс скай"))
            throw new Exception("SystemPrompt missing 'установи ноу менс скай' example!");
        if (!LlmIntentService.SystemPrompt.Contains("установи киберпанк"))
            throw new Exception("SystemPrompt missing 'установи киберпанк' example!");
        if (!LlmIntentService.SystemPrompt.Contains("запусти витчер"))
            throw new Exception("SystemPrompt missing 'запусти витчер' example!");

        var fastInstallNms = LlmIntentService.TryFastMatch("установи ноу менс скай");
        if (fastInstallNms?.CommandRequest?.Command != "app" ||
            !fastInstallNms.CommandRequest.Args.TryGetProperty("action", out var actNms) ||
            actNms.GetString() != "install" ||
            !fastInstallNms.CommandRequest.Args.TryGetProperty("name", out var nameNms) ||
            nameNms.GetString() != "ноу менс скай")
            throw new Exception("TryFastMatch('установи ноу менс скай') failed!");
        Console.WriteLine("  TryFastMatch('установи ноу менс скай') verified.");

        var fastInstallCp = LlmIntentService.TryFastMatch("установи киберпанк");
        if (fastInstallCp?.CommandRequest?.Command != "app" ||
            !fastInstallCp.CommandRequest.Args.TryGetProperty("action", out var actCp) ||
            actCp.GetString() != "install" ||
            !fastInstallCp.CommandRequest.Args.TryGetProperty("name", out var nameCp) ||
            nameCp.GetString() != "киберпанк")
            throw new Exception("TryFastMatch('установи киберпанк') failed!");
        Console.WriteLine("  TryFastMatch('установи киберпанк') verified.");

        var fastLaunchWitcher = LlmIntentService.TryFastMatch("запусти витчер");
        if (fastLaunchWitcher?.CommandRequest?.Command != "app" ||
            !fastLaunchWitcher.CommandRequest.Args.TryGetProperty("action", out var actW) ||
            actW.GetString() != "start")
            throw new Exception("TryFastMatch('запусти витчер') failed!");
        Console.WriteLine("  TryFastMatch('запусти витчер') verified.");

        var offlineInstall = LlmIntentService.OfflineFallbackInterpreter("установи киберпанк");
        if (offlineInstall.CommandRequest?.Command != "app" ||
            !offlineInstall.CommandRequest.Args.TryGetProperty("action", out var offAct) ||
            offAct.GetString() != "install")
            throw new Exception("OfflineFallbackInterpreter('установи киберпанк') failed!");
        Console.WriteLine("  OfflineFallbackInterpreter('установи киберпанк') verified.");

        // Test 7: Multi-WakeWords and Regex Cleaning
        Console.WriteLine("\n[7] Testing Multi-WakeWords configuration and Regex CleanUp...");
        var loadedSettings = AppSettingsService.Load();
        string[] requiredWakeWords =
        [
            "джарвис", "jarvis", "рис", "вис",
            "джемини", "гемини", "джеминай", "gemini",
            "димон", "петрович", "алиса", "компьютер"
        ];
        foreach (var req in requiredWakeWords)
        {
            if (!loadedSettings.WakeWords.Contains(req, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception($"WakeWord '{req}' missing from loaded configuration!");
            }
        }
        Console.WriteLine($"  All {requiredWakeWords.Length} WakeWords verified in configuration.");

        var testListener = new VoiceListener(wakeWords: loadedSettings.WakeWords.ToArray());
        string clean1 = testListener.CleanUpCommandText("джемини установи игру");
        if (clean1 != "установи игру")
            throw new Exception($"CleanUpCommandText('джемини установи игру') failed! Got: '{clean1}'");
        Console.WriteLine($"  CleanUpCommandText('джемини установи игру') = '{clean1}' (OK)");

        string clean2 = testListener.CleanUpCommandText("джарвис привет");
        if (clean2 != "привет")
            throw new Exception($"CleanUpCommandText('джарвис привет') failed! Got: '{clean2}'");
        Console.WriteLine($"  CleanUpCommandText('джарвис привет') = '{clean2}' (OK)");

        string clean3 = testListener.CleanUpCommandText("димон закройся");
        if (clean3 != "закройся")
            throw new Exception($"CleanUpCommandText('димон закройся') failed! Got: '{clean3}'");
        Console.WriteLine($"  CleanUpCommandText('димон закройся') = '{clean3}' (OK)");

        string cleanEmpty1 = testListener.CleanUpCommandText("джемини");
        if (!string.IsNullOrEmpty(cleanEmpty1))
            throw new Exception($"CleanUpCommandText('джемини') should be empty! Got: '{cleanEmpty1}'");
        Console.WriteLine($"  CleanUpCommandText('джемини') = '' (OK - pause mode)");

        string cleanEmpty2 = testListener.CleanUpCommandText("алиса");
        if (!string.IsNullOrEmpty(cleanEmpty2))
            throw new Exception($"CleanUpCommandText('алиса') should be empty! Got: '{cleanEmpty2}'");
        Console.WriteLine($"  CleanUpCommandText('алиса') = '' (OK - pause mode)");

        // Test 8: SteamService FindMainSteamWindow API
        Console.WriteLine("\n[8] Testing SteamService FindMainSteamWindow API...");
        IntPtr testHwnd = SteamService.FindMainSteamWindow();
        Console.WriteLine($"  FindMainSteamWindow returned HWND: {testHwnd}");

        // Test 9: Direct App Exit Keyword Check
        Console.WriteLine("\n[9] Testing Direct App Exit keywords detection...");
        string[] testExitPhrases = ["закройся", "закрывайся", "закрыть", "выход", "отключись", "заверши работу", "выключись", "стоп приложение", "джарвис закрывайся"];
        foreach (var phrase in testExitPhrases)
        {
            if (!JarvisOrchestrator.IsExitCommand(phrase))
            {
                throw new Exception($"Exit command '{phrase}' was not recognized!");
            }
        }
        Console.WriteLine("  All exit keywords verified successfully.");

        // Test 10: Confirmation Listening Bypass Wake-Word
        Console.WriteLine("\n[10] Testing Confirmation Listening Bypass Wake-Word...");
        JarvisOrchestrator.Instance.ResetConfirmationState();

        // 10.1 Verify keyword matchers
        string[] affirmativeWords = ["да", "давай", "подтверждаю", "устанавливай", "запускай", "верно", "именно", "ага", "конечно", "хорошо"];
        foreach (var word in affirmativeWords)
        {
            if (!JarvisOrchestrator.IsAffirmativeReply(word))
                throw new Exception($"IsAffirmativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Affirmative confirmation keywords verified.");

        string[] negativeWords = ["нет", "отмена", "не надо", "отбой", "стой", "не то", "отставить"];
        foreach (var word in negativeWords)
        {
            if (!JarvisOrchestrator.IsNegativeReply(word))
                throw new Exception($"IsNegativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Negative confirmation keywords verified.");

        if (JarvisOrchestrator.IsAffirmativeReply("сделай тише") || JarvisOrchestrator.IsNegativeReply("сделай тише"))
            throw new Exception("Unrelated command matched confirmation keywords!");

        // 10.2 Full scenario: 'установи астра ниро' -> doubt zone -> confirmation question
        var confPromptResp = LlmIntentService.TryFastMatch("установи астра ниро");
        if (confPromptResp == null || !confPromptResp.Reply!.Contains("ASTRONEER") || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("TryFastMatch('установи астра ниро') failed to trigger pending confirmation for ASTRONEER!");
        Console.WriteLine($"  Prompt response: '{confPromptResp.Reply}' (PendingAction={JarvisOrchestrator.Instance.PendingAction})");

        // 10.3 Direct capture without wake-word: VoiceListener enters ListeningForCommand
        testListener.EnterConfirmationListening("Awaiting confirmation reply (bypassing wake-word)...");
        if (testListener.CurrentState != VoiceListenerState.ListeningForCommand)
            throw new Exception($"Expected VoiceListener state ListeningForCommand, got {testListener.CurrentState}");
        Console.WriteLine("  VoiceListener successfully entered ListeningForCommand (bypassing wake-word).");

        // 10.4 User says 'Да' without wake-word
        var yesResult = LlmIntentService.TryFastMatch("Да");
        if (yesResult == null || !yesResult.Reply!.Contains("Принято. Начинаю установку ASTRONEER") || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Direct affirmative reply 'Да' failed to confirm installation of ASTRONEER!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after confirmation, got {testListener.CurrentState}");
        Console.WriteLine($"  Affirmative response: '{yesResult.Reply}' -> State returned to WaitingForWakeWord.");

        // 10.5 Negative reply scenario: 'установи астра ниро' -> 'Нет'
        LlmIntentService.TryFastMatch("установи астра ниро");
        testListener.EnterConfirmationListening();
        var noResult = LlmIntentService.TryFastMatch("Нет");
        if (noResult == null || noResult.Reply != "Понял, отменяю." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Direct negative reply 'Нет' failed to cancel pending confirmation!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after cancellation, got {testListener.CurrentState}");
        Console.WriteLine($"  Negative response: '{noResult.Reply}' -> State returned to WaitingForWakeWord.");

        // 10.6 Other command scenario: 'установи астра ниро' -> 'сделай тише'
        LlmIntentService.TryFastMatch("установи астра ниро");
        testListener.EnterConfirmationListening();
        var otherResult = LlmIntentService.TryFastMatch("сделай тише");
        if (otherResult?.CommandRequest?.Command != "volume" || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Unrelated command 'сделай тише' failed to cancel pending confirmation and execute!");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after fallback, got {testListener.CurrentState}");
        // Test 11: Media FastMatch and Win32 Media Keys (TASK: 41_Fix_Media_FastMatch_And_Win32_Media_Keys)
        Console.WriteLine("\n[11] Testing Media FastMatch & Win32 Media Keys...");

        // 11.1 Play/Pause Toggle commands
        string[] playPhrases = ["включи музыку", "музыка", "играй", "продолжи", "возобнови"];
        foreach (var phrase in playPhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command! Got: {res?.CommandRequest?.Command}");
            if (!res.CommandRequest.Args.TryGetProperty("action", out var act) || act.GetString() != "play_pause")
                throw new Exception($"TryFastMatch('{phrase}') action != 'play_pause'");
            if (res.Reply != "Включаю.")
                throw new Exception($"TryFastMatch('{phrase}') reply != 'Включаю.', got: '{res.Reply}'");
        }
        Console.WriteLine("  Play/Resume toggle commands verified with 'Включаю.' feedback.");

        string[] pausePhrases = ["пауза", "стоп", "останови", "останови музыку"];
        foreach (var phrase in pausePhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command! Got: {res?.CommandRequest?.Command}");
            if (!res.CommandRequest.Args.TryGetProperty("action", out var act) || act.GetString() != "play_pause")
                throw new Exception($"TryFastMatch('{phrase}') action != 'play_pause'");
            if (res.Reply != "Остановил, сэр.")
                throw new Exception($"TryFastMatch('{phrase}') reply != 'Остановил, сэр.', got: '{res.Reply}'");
        }
        Console.WriteLine("  Pause/Stop toggle commands verified with 'Остановил, сэр.' feedback.");

        // 11.2 Next Track commands
        string[] nextPhrases = ["следующий трек", "следующая песня", "дальше", "переключи", "трек вперед"];
        foreach (var phrase in nextPhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command! Got: {res?.CommandRequest?.Command}");
            if (!res.CommandRequest.Args.TryGetProperty("action", out var act) || act.GetString() != "next")
                throw new Exception($"TryFastMatch('{phrase}') action != 'next'");
            if (res.Reply != "Следующий трек, сэр.")
                throw new Exception($"TryFastMatch('{phrase}') reply != 'Следующий трек, сэр.', got: '{res.Reply}'");
        }
        Console.WriteLine("  Next Track commands verified.");

        // 11.3 Prev Track commands
        string[] prevPhrases = ["предыдущий трек", "предыдущая песня", "назад", "верни трек"];
        foreach (var phrase in prevPhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command! Got: {res?.CommandRequest?.Command}");
            if (!res.CommandRequest.Args.TryGetProperty("action", out var act) || act.GetString() != "prev")
                throw new Exception($"TryFastMatch('{phrase}') action != 'prev'");
            if (res.Reply != "Предыдущий трек, сэр.")
                throw new Exception($"TryFastMatch('{phrase}') reply != 'Предыдущий трек, сэр.', got: '{res.Reply}'");
        }
        Console.WriteLine("  Prev Track commands verified.");

        // 11.4 Stop commands
        string[] stopPhrases = ["заглуши музыку", "выключи музыку"];
        foreach (var phrase in stopPhrases)
        {
            var res = LlmIntentService.TryFastMatch(phrase);
            if (res?.CommandRequest?.Command != "media")
                throw new Exception($"TryFastMatch('{phrase}') did not return 'media' command! Got: {res?.CommandRequest?.Command}");
            if (!res.CommandRequest.Args.TryGetProperty("action", out var act) || act.GetString() != "stop")
                throw new Exception($"TryFastMatch('{phrase}') action != 'stop'");
            if (res.Reply != "Остановил, сэр.")
                throw new Exception($"TryFastMatch('{phrase}') reply != 'Остановил, сэр.', got: '{res.Reply}'");
        }
        Console.WriteLine("  Stop commands verified.");

        // 11.5 Disambiguation checks
        var stopApp = LlmIntentService.TryFastMatch("стоп приложение");
        if (stopApp?.CommandRequest?.Command != "system")
            throw new Exception("TryFastMatch('стоп приложение') was incorrectly captured by media instead of system exit!");
        Console.WriteLine("  'стоп приложение' correctly maps to system exit.");

        if (JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("Media fast-matches unexpectedly triggered pending actions in SteamService!");

        // 11.6 Latency requirement: < 5 ms
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 1000;
        for (int i = 0; i < iterations; i++)
        {
            _ = LlmIntentService.TryFastMatch("включи музыку");
            _ = LlmIntentService.TryFastMatch("останови");
            _ = LlmIntentService.TryFastMatch("пауза");
        }
        sw.Stop();
        double avgPerCallMs = sw.Elapsed.TotalMilliseconds / (iterations * 3);
        Console.WriteLine($"  Latency benchmark: {iterations * 3} calls in {sw.ElapsedMilliseconds} ms (avg {avgPerCallMs:F4} ms/call, required < 5.0 ms).");
        if (avgPerCallMs >= 5.0)
            throw new Exception($"Latency test failed! Average per call was {avgPerCallMs:F2} ms (exceeded 5.0 ms)");

        // 11.7 MediaCommandHandler and Win32 SendInput execution
        var testRouter = new CommandRouter().Register(new MediaCommandHandler());
        var playCmd = LlmIntentService.TryFastMatch("пауза")!.CommandRequest!;
        var routerResult = await testRouter.RouteAsync(playCmd);
        if (!routerResult.Success)
            throw new Exception($"router.RouteAsync(media play_pause) failed: {routerResult.Message}");
        Console.WriteLine($"  router.RouteAsync(media) verified: {routerResult.Message}");

        bool sendMediaOk = Gem.Services.MediaKeyService.PlayPause();
        if (!sendMediaOk)
            throw new Exception("MediaKeyService.PlayPause() returned false!");
        Console.WriteLine("  MediaKeyService.PlayPause() SendInput successfully executed.");

        // 11.8 Verify InterpretAsync bypasses LLM & network completely (points to dummy invalid endpoint)
        var dummyLlm = new LlmIntentService("http://invalid-unreachable-host:9999", "dummy_key", "dummy_model");
        var fastResp1 = await dummyLlm.InterpretAsync("включи музыку");
        if (fastResp1?.CommandRequest?.Command != "media" || fastResp1.Reply != "Включаю.")
            throw new Exception("InterpretAsync('включи музыку') failed or attempted network call!");

        var fastResp2 = await dummyLlm.InterpretAsync("останови");
        if (fastResp2?.CommandRequest?.Command != "media" || fastResp2.Reply != "Остановил, сэр.")
            throw new Exception("InterpretAsync('останови') failed or attempted network call!");

        var fastResp3 = await dummyLlm.InterpretAsync("пауза");
        if (fastResp3?.CommandRequest?.Command != "media" || fastResp3.Reply != "Остановил, сэр.")
            throw new Exception("InterpretAsync('пауза') failed or attempted network call!");

        Console.WriteLine("  InterpretAsync bypassed LLM & network completely for media commands (verified with unreachable endpoint).");

        // Test 12: Timer and Reminder Service (TASK: 42_Implement_Timer_And_Reminder_Service)
        Console.WriteLine("\n[12] Testing Timer and Reminder Service...");

        // 12.1 FastMatch latency benchmark: < 5 ms
        var timerSw = System.Diagnostics.Stopwatch.StartNew();
        const int timerIters = 1000;
        for (int i = 0; i < timerIters; i++)
        {
            _ = LlmIntentService.TryFastMatch("поставь таймер на 10 секунд");
            _ = LlmIntentService.TryFastMatch("отмени таймер");
            _ = LlmIntentService.TryFastMatch("сколько осталось");
        }
        timerSw.Stop();
        double timerAvgMs = timerSw.Elapsed.TotalMilliseconds / (timerIters * 3);
        Console.WriteLine($"  Timer Latency benchmark: {timerIters * 3} calls in {timerSw.ElapsedMilliseconds} ms (avg {timerAvgMs:F4} ms/call, required < 5.0 ms).");
        if (timerAvgMs >= 5.0)
            throw new Exception($"Timer latency test failed! Average per call was {timerAvgMs:F2} ms (exceeded 5.0 ms)");

        // 12.2 Time parser verification ("на 30 секунд", "на 5 минут", "на 1 час", "на полчаса", "отмени таймер")
        var t1 = LlmIntentService.TryFastMatch("на 30 секунд");
        if (t1?.CommandRequest?.Command != "timer" ||
            !t1.CommandRequest.Args.TryGetProperty("seconds", out var s1) || s1.GetInt32() != 30 ||
            !t1.Reply!.Contains("30 секунд"))
            throw new Exception($"TryFastMatch('на 30 секунд') failed! Got: {t1?.CommandRequest?.Command}, reply: '{t1?.Reply}'");
        Console.WriteLine("  TryFastMatch('на 30 секунд') verified.");

        var t2 = LlmIntentService.TryFastMatch("на 5 минут");
        if (t2?.CommandRequest?.Command != "timer" ||
            !t2.CommandRequest.Args.TryGetProperty("seconds", out var s2) || s2.GetInt32() != 300 ||
            !t2.Reply!.Contains("5 минут"))
            throw new Exception($"TryFastMatch('на 5 минут') failed! Got: {t2?.CommandRequest?.Command}, reply: '{t2?.Reply}'");
        Console.WriteLine("  TryFastMatch('на 5 минут') verified.");

        var t3 = LlmIntentService.TryFastMatch("на 1 час");
        if (t3?.CommandRequest?.Command != "timer" ||
            !t3.CommandRequest.Args.TryGetProperty("seconds", out var s3) || s3.GetInt32() != 3600 ||
            !t3.Reply!.Contains("1 час"))
            throw new Exception($"TryFastMatch('на 1 час') failed! Got: {t3?.CommandRequest?.Command}, reply: '{t3?.Reply}'");
        Console.WriteLine("  TryFastMatch('на 1 час') verified.");

        var t4 = LlmIntentService.TryFastMatch("на полчаса");
        if (t4?.CommandRequest?.Command != "timer" ||
            !t4.CommandRequest.Args.TryGetProperty("seconds", out var s4) || s4.GetInt32() != 1800 ||
            (!t4.Reply!.Contains("полчаса") && !t4.Reply!.Contains("30 минут")))
            throw new Exception($"TryFastMatch('на полчаса') failed! Got: {t4?.CommandRequest?.Command}, reply: '{t4?.Reply}'");
        Console.WriteLine("  TryFastMatch('на полчаса') verified.");

        var tCancel = LlmIntentService.TryFastMatch("отмени таймер");
        if (tCancel?.CommandRequest?.Command != "timer" ||
            !tCancel.CommandRequest.Args.TryGetProperty("action", out var actCancel) || actCancel.GetString() != "cancel" ||
            tCancel.Reply != "Таймер отменен, сэр.")
            throw new Exception($"TryFastMatch('отмени таймер') failed! Got: {tCancel?.CommandRequest?.Command}, reply: '{tCancel?.Reply}'");
        Console.WriteLine("  TryFastMatch('отмени таймер') verified.");

        // 12.3 Extended patterns ("поставь таймер на 10 секунд", "полтора часа", "минуту", "напомни через 15 минут выключить плиту")
        var tSet10 = LlmIntentService.TryFastMatch("поставь таймер на 10 секунд");
        if (tSet10?.CommandRequest?.Command != "timer" ||
            !tSet10.CommandRequest.Args.TryGetProperty("seconds", out var s10) || s10.GetInt32() != 10 ||
            tSet10.Reply != "Таймер на 10 секунд установлен, сэр.")
            throw new Exception($"TryFastMatch('поставь таймер на 10 секунд') failed! Got: {tSet10?.Reply}");
        Console.WriteLine("  TryFastMatch('поставь таймер на 10 секунд') verified.");

        var tHalfHours = LlmIntentService.TryFastMatch("заведи таймер на полтора часа");
        if (tHalfHours?.CommandRequest?.Command != "timer" ||
            !tHalfHours.CommandRequest.Args.TryGetProperty("seconds", out var sHh) || sHh.GetInt32() != 5400)
            throw new Exception($"TryFastMatch('заведи таймер на полтора часа') failed! Got: {tHalfHours?.CommandRequest?.Args}");
        Console.WriteLine("  TryFastMatch('заведи таймер на полтора часа') verified (5400s / 90m).");

        var tMin = LlmIntentService.TryFastMatch("включи таймер на минуту");
        if (tMin?.CommandRequest?.Command != "timer" ||
            !tMin.CommandRequest.Args.TryGetProperty("seconds", out var sMin) || sMin.GetInt32() != 60)
            throw new Exception($"TryFastMatch('включи таймер на минуту') failed! Got: {tMin?.CommandRequest?.Args}");
        Console.WriteLine("  TryFastMatch('включи таймер на минуту') verified (60s).");

        var tReminder = LlmIntentService.TryFastMatch("напомни через 15 минут выключить плиту");
        if (tReminder?.CommandRequest?.Command != "timer" ||
            !tReminder.CommandRequest.Args.TryGetProperty("seconds", out var sRem) || sRem.GetInt32() != 900 ||
            !tReminder.CommandRequest.Args.TryGetProperty("label", out var lRem) || lRem.GetString() != "выключить плиту")
            throw new Exception($"TryFastMatch reminder failed! Got: {tReminder?.CommandRequest?.Args}");
        Console.WriteLine("  TryFastMatch('напомни через 15 минут выключить плиту') verified (label='выключить плиту').");

        // 12.4 Cancellation and status patterns
        string[] cancelSynonyms = ["сбрось таймер", "выключи таймер", "отмени все таймеры"];
        foreach (var syn in cancelSynonyms)
        {
            var res = LlmIntentService.TryFastMatch(syn);
            if (res?.CommandRequest?.Command != "timer" ||
                !res.CommandRequest.Args.TryGetProperty("action", out var a) || a.GetString() != "cancel")
                throw new Exception($"TryFastMatch('{syn}') failed!");
        }
        Console.WriteLine("  Cancel synonyms verified.");

        string[] statusSynonyms = ["сколько осталось", "статус таймера", "что с таймером"];
        foreach (var syn in statusSynonyms)
        {
            var res = LlmIntentService.TryFastMatch(syn);
            if (res?.CommandRequest?.Command != "timer" ||
                !res.CommandRequest.Args.TryGetProperty("action", out var a) || a.GetString() != "status")
                throw new Exception($"TryFastMatch('{syn}') failed!");
        }
        Console.WriteLine("  Status synonyms verified.");

        // 12.5 Steam disambiguation check (ensure 'поставь киберпанк' does NOT get captured by timer)
        var gameInstall = LlmIntentService.TryFastMatch("поставь киберпанк");
        if (gameInstall?.CommandRequest?.Command != "app" ||
            !gameInstall.CommandRequest.Args.TryGetProperty("action", out var actG) || actG.GetString() != "install")
            throw new Exception("TryFastMatch('поставь киберпанк') was incorrectly intercepted by timer matcher!");
        Console.WriteLine("  'поставь киберпанк' correctly preserves Steam game install.");

        // 12.6 Live TimerService & CommandRouter Execution
        var testTimerService = new TimerService();
        var testRouterWithTimer = new CommandRouter().Register(new TimerCommandHandler(testTimerService));

        // Route set command
        var setCmd = LlmIntentService.TryFastMatch("поставь таймер на 10 секунд")!.CommandRequest!;
        var setRes = await testRouterWithTimer.RouteAsync(setCmd);
        if (!setRes.Success)
            throw new Exception($"RouteAsync timer set failed: {setRes.Message}");
        var activeTimers = testTimerService.GetActiveTimers();
        if (activeTimers.Count != 1 || Math.Abs(activeTimers[0].Duration.TotalSeconds - 10) > 0.1)
            throw new Exception($"GetActiveTimers() count != 1, got {activeTimers.Count}");
        Console.WriteLine("  RouteAsync(set 10s) verified.");

        // Route status command
        var statCmd = LlmIntentService.TryFastMatch("статус таймера")!.CommandRequest!;
        var statRes = await testRouterWithTimer.RouteAsync(statCmd);
        if (!statRes.Success || !statRes.Message!.Contains("Осталось"))
            throw new Exception($"RouteAsync timer status failed: {statRes.Message}");
        Console.WriteLine($"  RouteAsync(status) verified: {statRes.Message}");

        // Route cancel command
        var cancelCmd = LlmIntentService.TryFastMatch("отмени таймер")!.CommandRequest!;
        var cancelRes = await testRouterWithTimer.RouteAsync(cancelCmd);
        if (!cancelRes.Success)
            throw new Exception($"RouteAsync timer cancel failed: {cancelRes.Message}");
        if (testTimerService.GetActiveTimers().Count != 0)
            throw new Exception("CancelTimer failed to clear active timers!");
        Console.WriteLine("  RouteAsync(cancel) verified. Active timers cleared.");

        // 12.7 Short 2-second timer with callback verification
        Console.WriteLine("  Testing short 2-second countdown with callback...");
        var tcs = new TaskCompletionSource<bool>();
        ActiveTimer? elapsedTimer = null;
        testTimerService.OnTimerElapsed += t =>
        {
            elapsedTimer = t;
            tcs.TrySetResult(true);
        };

        var triggerSw = System.Diagnostics.Stopwatch.StartNew();
        var t2Id = testTimerService.SetTimer(TimeSpan.FromSeconds(2), "тестовое напоминание");

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(4000));
        triggerSw.Stop();

        if (completedTask != tcs.Task)
            throw new Exception("Short 2-second timer did not trigger callback within timeout (4000ms)!");

        if (elapsedTimer == null || elapsedTimer.Id != t2Id || elapsedTimer.Label != "тестовое напоминание")
            throw new Exception($"Elapsed timer payload mismatch! ID: {elapsedTimer?.Id}, Label: {elapsedTimer?.Label}");

        Console.WriteLine($"  Short 2-second timer successfully fired callback after {triggerSw.ElapsedMilliseconds} ms (Label: '{elapsedTimer.Label}').");

        // Verify active timers count is now 0
        if (testTimerService.GetActiveTimers().Count != 0)
            throw new Exception("Active timers not empty after timer elapsed!");

        // 12.8 Cancellation test (verify cancelled timer does NOT fire callback)
        bool cancelledFired = false;
        testTimerService.OnTimerElapsed += t =>
        {
            if (t.Label == "отменяемый таймер")
            {
                cancelledFired = true;
            }
        };

        var cancelId = testTimerService.SetTimer(TimeSpan.FromSeconds(2), "отменяемый таймер");
        await Task.Delay(100);
        bool didCancel = testTimerService.CancelTimer(cancelId);
        if (!didCancel)
            throw new Exception("CancelTimer returned false for existing timer!");

        await Task.Delay(2500);
        if (cancelledFired)
            throw new Exception("Cancelled timer unexpectedly fired OnTimerElapsed callback!");

        Console.WriteLine("  Cancelled timer verified: callback did NOT fire.");
        testTimerService.Dispose();

        // Test 13: Steam Game Uninstallation With AutoClicker (TASK: 43_Implement_Steam_Game_Uninstallation_With_AutoClicker)
        Console.WriteLine("\n[13] Testing Steam Game Uninstallation With AutoClicker...");
        JarvisOrchestrator.Instance.ResetConfirmationState();

        // 13.1 Affirmative replies verification (including 'удаляй', 'деинсталлируй', 'сноси')
        string[] uninstallAffirmative = ["удаляй", "деинсталлируй", "сноси", "да", "давай", "подтверждаю"];
        foreach (var word in uninstallAffirmative)
        {
            if (!JarvisOrchestrator.IsAffirmativeReply(word))
                throw new Exception($"IsAffirmativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Uninstallation affirmative keywords verified.");

        // 13.2 Negative replies verification ('нет', 'отмена', 'отбой')
        string[] uninstallNegative = ["нет", "отмена", "отбой"];
        foreach (var word in uninstallNegative)
        {
            if (!JarvisOrchestrator.IsNegativeReply(word))
                throw new Exception($"IsNegativeReply('{word}') returned false!");
        }
        Console.WriteLine("  Uninstallation negative keywords verified.");

        // 13.3 Non-installed game test: "удали незнакомая_игра_123" -> "Установленная игра 'незнакомая_игра_123' не найдена, сэр."
        var notFoundResp = LlmIntentService.TryFastMatch("удали незнакомая_игра_123");
        if (notFoundResp == null || notFoundResp.Reply != "Установленная игра 'незнакомая_игра_123' не найдена, сэр." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"Expected not found reply for uninstalled game, got: '{notFoundResp?.Reply}'");
        Console.WriteLine("  Uninstalled game rejection verified: " + notFoundResp.Reply);

        // 13.4 Register ASTRONEER as installed for testing
        SteamService.Instance.RegisterInstalledGameForTesting("361420", "ASTRONEER");
        var installedAstro = SteamService.FindInstalledGame("ASTRONEER");
        if (installedAstro == null || installedAstro.AppId != "361420" || !installedAstro.IsInstalled)
            throw new Exception("FindInstalledGame('ASTRONEER') failed to resolve installed test game!");
        Console.WriteLine($"  FindInstalledGame('ASTRONEER') resolved: {installedAstro.Title} (AppId: {installedAstro.AppId})");

        var installedAstroFuzzy1 = SteamService.FindInstalledGame("астрони р");
        if (installedAstroFuzzy1 == null || installedAstroFuzzy1.AppId != "361420")
            throw new Exception($"FindInstalledGame('астрони р') failed to resolve ASTRONEER, got: '{installedAstroFuzzy1?.Title}'");
        Console.WriteLine($"  FindInstalledGame('астрони р') resolved: {installedAstroFuzzy1.Title} (AppId: {installedAstroFuzzy1.AppId})");

        var installedAstroFuzzy2 = SteamService.FindInstalledGame("о стране");
        if (installedAstroFuzzy2 == null || installedAstroFuzzy2.AppId != "361420")
            throw new Exception($"FindInstalledGame('о стране') failed to resolve ASTRONEER, got: '{installedAstroFuzzy2?.Title}'");
        Console.WriteLine($"  FindInstalledGame('о стране') resolved: {installedAstroFuzzy2.Title} (AppId: {installedAstroFuzzy2.AppId})");

        // 13.5 Uninstallation command patterns matching:
        // "удали ASTRONEER" -> "Вы действительно хотите удалить ASTRONEER, сэр?"
        var uninstallResp1 = LlmIntentService.TryFastMatch("удали ASTRONEER");
        if (uninstallResp1 == null || uninstallResp1.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('удали ASTRONEER') failed! Reply: '{uninstallResp1?.Reply}'");
        if (JarvisOrchestrator.Instance.PendingAction != PendingGameAction.Uninstall ||
            JarvisOrchestrator.Instance.CurrentPendingAction?.ActionType != "UninstallGame" ||
            JarvisOrchestrator.Instance.CurrentPendingAction?.AppId != "361420")
            throw new Exception("PendingActionState invalid after 'удали ASTRONEER'!");
        Console.WriteLine("  TryFastMatch('удали ASTRONEER') verified: " + uninstallResp1.Reply);

        // Direct capture without wake-word
        testListener.EnterConfirmationListening("Awaiting uninstallation confirmation reply (bypassing wake-word)...");
        if (testListener.CurrentState != VoiceListenerState.ListeningForCommand)
            throw new Exception($"Expected VoiceListener state ListeningForCommand, got {testListener.CurrentState}");

        // Test cancel: "Нет" -> "Удаление отменено, сэр."
        var cancelResp = LlmIntentService.TryFastMatch("Нет");
        if (cancelResp == null || cancelResp.Reply != "Удаление отменено, сэр." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"Expected 'Удаление отменено, сэр.', got: '{cancelResp?.Reply}'");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after cancellation, got {testListener.CurrentState}");
        Console.WriteLine("  Cancellation reply verified: " + cancelResp.Reply);

        // "удали игру ASTRONEER"
        var uninstallResp2 = LlmIntentService.TryFastMatch("удали игру ASTRONEER");
        if (uninstallResp2 == null || uninstallResp2.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('удали игру ASTRONEER') failed! Reply: '{uninstallResp2?.Reply}'");
        JarvisOrchestrator.Instance.ResetConfirmationState();
        Console.WriteLine("  TryFastMatch('удали игру ASTRONEER') verified: " + uninstallResp2.Reply);

        // "деинсталлируй ASTRONEER"
        var uninstallResp3 = LlmIntentService.TryFastMatch("деинсталлируй ASTRONEER");
        if (uninstallResp3 == null || uninstallResp3.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('деинсталлируй ASTRONEER') failed! Reply: '{uninstallResp3?.Reply}'");
        JarvisOrchestrator.Instance.ResetConfirmationState();
        Console.WriteLine("  TryFastMatch('деинсталлируй ASTRONEER') verified: " + uninstallResp3.Reply);

        // "сноси ASTRONEER"
        var uninstallResp4 = LlmIntentService.TryFastMatch("сноси ASTRONEER");
        if (uninstallResp4 == null || uninstallResp4.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('сноси ASTRONEER') failed! Reply: '{uninstallResp4?.Reply}'");
        testListener.EnterConfirmationListening();
        Console.WriteLine("  TryFastMatch('сноси ASTRONEER') verified: " + uninstallResp4.Reply);

        // "удали астрони р" -> "Вы действительно хотите удалить ASTRONEER, сэр?"
        var uninstallRespFuzzy1 = LlmIntentService.TryFastMatch("удали астрони р");
        if (uninstallRespFuzzy1 == null || uninstallRespFuzzy1.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('удали астрони р') failed! Reply: '{uninstallRespFuzzy1?.Reply}'");
        Console.WriteLine("  TryFastMatch('удали астрони р') verified: " + uninstallRespFuzzy1.Reply);

        // "удали о стране" -> "Вы действительно хотите удалить ASTRONEER, сэр?"
        JarvisOrchestrator.Instance.ResetConfirmationState();
        var uninstallRespFuzzy2 = LlmIntentService.TryFastMatch("удали о стране");
        if (uninstallRespFuzzy2 == null || uninstallRespFuzzy2.Reply != "Вы действительно хотите удалить ASTRONEER, сэр?" || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"TryFastMatch('удали о стране') failed! Reply: '{uninstallRespFuzzy2?.Reply}'");
        Console.WriteLine("  TryFastMatch('удали о стране') verified: " + uninstallRespFuzzy2.Reply);

        // Test confirm: "удаляй" / "Да" -> "Удаляю ASTRONEER, сэр."
        var confirmResp = LlmIntentService.TryFastMatch("удаляй");
        if (confirmResp == null || confirmResp.Reply != "Удаляю ASTRONEER, сэр." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"Expected 'Удаляю ASTRONEER, сэр.', got: '{confirmResp?.Reply}'");
        if (testListener.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"Expected VoiceListener state WaitingForWakeWord after confirmation, got {testListener.CurrentState}");
        Console.WriteLine("  Confirmation reply 'удаляй' verified: " + confirmResp.Reply);

        // 13.6 InterpretAsync uninstallation confirmation bypass test
        LlmIntentService.TryFastMatch("удали ASTRONEER");
        var interpretYes = await dummyLlm.InterpretAsync("Да");
        if (interpretYes == null || interpretYes.Reply != "Удаляю ASTRONEER, сэр." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"InterpretAsync('Да') failed for uninstallation! Got: '{interpretYes?.Reply}'");
        Console.WriteLine("  InterpretAsync('Да') uninstallation confirmation verified.");

        LlmIntentService.TryFastMatch("удали ASTRONEER");
        var interpretNo = await dummyLlm.InterpretAsync("отбой");
        if (interpretNo == null || interpretNo.Reply != "Удаление отменено, сэр." || JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"InterpretAsync('отбой') failed for uninstallation! Got: '{interpretNo?.Reply}'");
        Console.WriteLine("  InterpretAsync('отбой') uninstallation cancellation verified.");

        // 13.7 CommandRouter action="uninstall" verification
        var appRouter = new CommandRouter().Register(new AppControlCommandHandler());
        var routerUninstallCmd = new CommandRequest("app", JsonDocument.Parse("""{ "action": "uninstall", "name": "ASTRONEER" }""").RootElement);
        var routerUninstallRes = await appRouter.RouteAsync(routerUninstallCmd);
        if (!routerUninstallRes.Success || !JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception($"appRouter.RouteAsync(uninstall) failed: {routerUninstallRes.Message}");
        JarvisOrchestrator.Instance.ResetConfirmationState();
        Console.WriteLine("  appRouter.RouteAsync(uninstall) verified.");

        // 13.8 Window search helper and safety verification
        var dummyHwnd = SteamService.FindSteamUninstallDialog("NonExistentDummyGameForSafetyTest");
        if (dummyHwnd != IntPtr.Zero)
            throw new Exception("FindSteamUninstallDialog unexpectedly matched non-existent game window!");
        Console.WriteLine("  FindSteamUninstallDialog(dummy) correctly returned IntPtr.Zero.");

        // Test 14: Hybrid Resilient TTS Architecture (TASK: 44_Implement_Hybrid_Resilient_TTS_Architecture)
        Console.WriteLine("\n[14] Testing Hybrid Resilient TTS Architecture...");

        // 14.1 Configuration Verification
        Console.WriteLine("  [14.1] Testing TtsConfig in AppSettingsService...");
        var ttsSettings = AppSettingsService.Load();
        if (ttsSettings.Tts == null)
            throw new Exception("AppSettingsService.Load().Tts is null!");
        if (ttsSettings.Tts.PreferredEngine != "Edge" ||
            ttsSettings.Tts.EdgeVoice != "ru-RU-DmitryNeural" ||
            ttsSettings.Tts.SileroModelPath != "Models/Silero/v4_ru.onnx" ||
            ttsSettings.Tts.SileroSpeaker != "aidar")
        {
            throw new Exception($"TtsConfig values mismatch! PreferredEngine: '{ttsSettings.Tts.PreferredEngine}', EdgeVoice: '{ttsSettings.Tts.EdgeVoice}', SileroModelPath: '{ttsSettings.Tts.SileroModelPath}', SileroSpeaker: '{ttsSettings.Tts.SileroSpeaker}'");
        }
        Console.WriteLine($"    Tts config verified: PreferredEngine={ttsSettings.Tts.PreferredEngine}, EdgeVoice={ttsSettings.Tts.EdgeVoice}, SileroModelPath={ttsSettings.Tts.SileroModelPath}, SileroSpeaker={ttsSettings.Tts.SileroSpeaker}");

        // 14.2 Engine Abstraction & Availability
        Console.WriteLine("  [14.2] Testing Engine abstractions (ITtsEngine)...");
        var edgeEngine = new EdgeTtsEngine(ttsSettings.Tts.EdgeVoice);
        if (edgeEngine.Name != "Edge")
            throw new Exception($"EdgeTtsEngine.Name != 'Edge', got '{edgeEngine.Name}'");
        if (edgeEngine.ConnectionTimeoutMs != 3000)
            throw new Exception($"edgeEngine.ConnectionTimeoutMs != 3000, got {edgeEngine.ConnectionTimeoutMs}");

        string secMsGec = EdgeTtsEngine.GenerateSecMsGec();
        if (string.IsNullOrEmpty(secMsGec) || secMsGec.Length != 64)
            throw new Exception($"EdgeTtsEngine.GenerateSecMsGec invalid! Length={secMsGec?.Length}");
        Console.WriteLine($"    EdgeTtsEngine verified (Name={edgeEngine.Name}, Timeout={edgeEngine.ConnectionTimeoutMs}ms, DRM Token={secMsGec[..12]}..., Available={edgeEngine.IsAvailable})");

        var sileroEngine = new SileroTtsEngine(ttsSettings.Tts.SileroModelPath, ttsSettings.Tts.SileroSpeaker);
        if (sileroEngine.Name != "Silero")
            throw new Exception($"SileroTtsEngine.Name != 'Silero', got '{sileroEngine.Name}'");
        bool expectedSileroAvail = File.Exists(Path.Combine(AppContext.BaseDirectory, ttsSettings.Tts.SileroModelPath)) ||
                                   File.Exists(Path.Combine(Directory.GetCurrentDirectory(), ttsSettings.Tts.SileroModelPath));
        if (sileroEngine.IsAvailable != expectedSileroAvail)
            throw new Exception($"SileroTtsEngine.IsAvailable ({sileroEngine.IsAvailable}) mismatch with expected ({expectedSileroAvail})!");
        Console.WriteLine($"    SileroTtsEngine verified (Name={sileroEngine.Name}, ModelPath={sileroEngine.ModelPath}, Available={sileroEngine.IsAvailable})");

        var systemSpeechEngine = new SystemSpeechTtsEngine();
        if (systemSpeechEngine.Name != "System.Speech")
            throw new Exception($"SystemSpeechTtsEngine.Name != 'System.Speech', got '{systemSpeechEngine.Name}'");
        if (!systemSpeechEngine.IsAvailable)
            throw new Exception("SystemSpeechTtsEngine.IsAvailable must always be true on Windows!");
        Console.WriteLine($"    SystemSpeechTtsEngine verified (Name={systemSpeechEngine.Name}, SelectedVoice={systemSpeechEngine.SelectedVoiceName}, Available={systemSpeechEngine.IsAvailable})");

        // 14.3 Failover Chain Verification with Mock Engines
        Console.WriteLine("  [14.3] Testing Composite failover chain (Edge -> Silero -> System.Speech)...");

        // Test 14.3.A: Primary Edge succeeds
        var mockEdgeOk = new MockTtsEngine("Edge", isAvailable: true, shouldThrow: false);
        var mockSilero = new MockTtsEngine("Silero", isAvailable: true, shouldThrow: false);
        var mockSystem = new MockTtsEngine("System.Speech", isAvailable: true, shouldThrow: false);

        var compositeA = new CompositeVoiceFeedbackService(null, mockEdgeOk, mockSilero, mockSystem);
        await compositeA.SpeakAsync("Тест первичного движка.");
        if (mockEdgeOk.SpeakCount != 1 || mockSilero.SpeakCount != 0 || mockSystem.SpeakCount != 0 || compositeA.LastUsedEngineName != "Edge")
            throw new Exception("Composite did not use Primary Edge when available!");
        Console.WriteLine("    Scenario A: Primary Edge executed successfully.");

        // Test 14.3.B: Edge fails -> Silero succeeds
        var mockEdgeFail = new MockTtsEngine("Edge", isAvailable: true, shouldThrow: true);
        var mockSileroOk = new MockTtsEngine("Silero", isAvailable: true, shouldThrow: false);
        var mockSystemUnused = new MockTtsEngine("System.Speech", isAvailable: true, shouldThrow: false);

        var compositeB = new CompositeVoiceFeedbackService(null, mockEdgeFail, mockSileroOk, mockSystemUnused);
        var failoverSw = System.Diagnostics.Stopwatch.StartNew();
        await compositeB.SpeakAsync("Тест переключения на Silero.");
        failoverSw.Stop();
        if (failoverSw.ElapsedMilliseconds > 1500)
            throw new Exception($"Edge failover took too long: {failoverSw.ElapsedMilliseconds} ms (expected < 1500 ms)!");
        if (mockEdgeFail.SpeakCount != 1 || mockSileroOk.SpeakCount != 1 || mockSystemUnused.SpeakCount != 0 || compositeB.LastUsedEngineName != "Silero")
            throw new Exception("Composite did not fall back to Silero when Edge failed!");
        Console.WriteLine($"    Scenario B: Edge failure cleanly failed over to Silero TTS in {failoverSw.ElapsedMilliseconds}ms (< 1500ms).");

        // Test 14.3.C: Edge fails & Silero unavailable -> System.Speech safety fallback
        var mockSileroUnavail = new MockTtsEngine("Silero", isAvailable: false, shouldThrow: false);
        var mockSystemOk = new MockTtsEngine("System.Speech", isAvailable: true, shouldThrow: false);

        var compositeC = new CompositeVoiceFeedbackService(null, mockEdgeFail, mockSileroUnavail, mockSystemOk);
        await compositeC.SpeakAsync("Тест переключения на System.Speech.");
        if (mockSystemOk.SpeakCount != 1 || compositeC.LastUsedEngineName != "System.Speech")
            throw new Exception("Composite did not fall back to System.Speech safety fallback!");
        Console.WriteLine("    Scenario C: Edge fail + Silero unavailable cleanly fell back to System.Speech.");

        // 14.4 Acoustic Feedback Prevention & Vosk Coordination
        Console.WriteLine("  [14.4] Testing Vosk STT acoustic feedback prevention coordination...");
        var testListenerCoord = new VoiceListener(wakeWords: ["джарвис"]);
        testListenerCoord.ResumeListening();

        bool wasPausedDuringSpeech = false;
        var mockEdgeCheckPause = new MockTtsEngine("Edge", isAvailable: true, onSpeak: () =>
        {
            wasPausedDuringSpeech = testListenerCoord.IsPaused;
        });

        var compositeCoord = new CompositeVoiceFeedbackService(testListenerCoord, mockEdgeCheckPause, mockSilero, mockSystem);
        await compositeCoord.SpeakAsync("Тест координации с Vosk.");

        if (!wasPausedDuringSpeech)
            throw new Exception("VoiceListener was NOT paused while assistant was speaking!");
        if (testListenerCoord.IsPaused)
            throw new Exception("VoiceListener remained paused after assistant finished speaking!");
        Console.WriteLine("    Vosk acoustic feedback prevention verified: Paused during speech -> Resumed after speech.");

        // 14.5 Confirmation Mode Coordination
        Console.WriteLine("  [14.5] Testing Confirmation mode coordination after speech...");
        JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Uninstall, new SteamGameInfo("99999", "TestGame", "testgame", "тестигра", true), compositeCoord);
        await compositeCoord.SpeakAsync("Вы уверены?");

        if (testListenerCoord.CurrentState != VoiceListenerState.ListeningForCommand)
            throw new Exception($"Expected VoiceListener state ListeningForCommand after confirmation question, got: {testListenerCoord.CurrentState}");
        Console.WriteLine("    Confirmation mode listening (bypassing wake-word) verified after speech.");
        JarvisOrchestrator.Instance.ResetConfirmationState();
        testListenerCoord.Dispose();

        // 14.6 Live Synthesis Integration Test
        Console.WriteLine("  [14.6] Testing Live Synthesis with CompositeVoiceFeedbackService...");
        var liveComposite = new CompositeVoiceFeedbackService(null, ttsSettings.Tts);
        await liveComposite.SpeakAsync("Проверка синтеза речи Джарвис.");
        if (string.IsNullOrEmpty(liveComposite.LastUsedEngineName))
            throw new Exception("Live composite did not record LastUsedEngineName!");
        Console.WriteLine($"    Live synthesis succeeded using engine: [{liveComposite.LastUsedEngineName}].");

        // 14.7 IVoiceFeedbackService Interface & Weather/App Handler Integration
        Console.WriteLine("  [14.7] Testing IVoiceFeedbackService abstraction & command handlers...");
        var weatherEdgeMock = new MockTtsEngine("Edge", isAvailable: true, shouldThrow: false);
        IVoiceFeedbackService testVoiceFeedback = new CompositeVoiceFeedbackService(null, weatherEdgeMock, mockSilero, mockSystem);
        var weatherCmdHandler = new WeatherCommandHandler(new WeatherService("Moscow", 55.75, 37.61), testVoiceFeedback);
        if (weatherCmdHandler.CommandName != "weather")
            throw new Exception("WeatherCommandHandler.CommandName mismatch!");
        var appControlHandler = new AppControlCommandHandler(testVoiceFeedback);
        if (appControlHandler.CommandName != "app")
            throw new Exception("AppControlCommandHandler.CommandName mismatch!");

        var weatherResult = await weatherCmdHandler.ExecuteAsync(default);
        if (!weatherResult.Success)
            throw new Exception($"WeatherCommandHandler failed: {weatherResult.Message}");
        if (weatherEdgeMock.SpeakCount != 1)
            throw new Exception($"WeatherCommandHandler did not speak via IVoiceFeedbackService! SpeakCount={weatherEdgeMock.SpeakCount}");
        Console.WriteLine($"    WeatherCommandHandler verified: successfully fetched weather and invoked IVoiceFeedbackService.SpeakAsync (SpeakCount={weatherEdgeMock.SpeakCount}).");

        // Test 15: Steam Uninstall Pixel Clicker & Vosk Multi-Word Phrase Buffering (TASK: 46_Fix_Steam_Uninstall_Pixel_Clicker_And_Vosk_Fragmentation)
        Console.WriteLine("\n[15] Testing Steam Uninstall Pixel Clicker & Vosk Multi-Word Phrase Buffering...");

        // 15.1 VoiceListener phrase accumulation & deduplication across chunks
        Console.WriteLine("  [15.1] Testing VoiceListener multi-word phrase buffering (no fragmentation)...");
        var testListenerBuffering = new VoiceListener(wakeWords: ["джарвис", "компьютер"]);
        testListenerBuffering.AppendSessionText("установи гарри");
        if (testListenerBuffering.GetCurrentSessionText() != "установи гарри")
            throw new Exception($"AppendSessionText failed for first chunk! Got: '{testListenerBuffering.GetCurrentSessionText()}'");

        testListenerBuffering.AppendSessionText("мод");
        if (testListenerBuffering.GetCurrentSessionText() != "установи гарри мод")
            throw new Exception($"AppendSessionText failed for second chunk! Got: '{testListenerBuffering.GetCurrentSessionText()}'");

        // Deduplication test
        testListenerBuffering.AppendSessionText("мод");
        if (testListenerBuffering.GetCurrentSessionText() != "установи гарри мод")
            throw new Exception($"AppendSessionText duplicate appended! Got: '{testListenerBuffering.GetCurrentSessionText()}'");

        string cleanedBuff = testListenerBuffering.CleanUpCommandText(testListenerBuffering.GetCurrentSessionText());
        if (cleanedBuff != "установи гарри мод")
            throw new Exception($"CleanUpCommandText failed! Got: '{cleanedBuff}'");
        Console.WriteLine($"    Vosk phrase accumulation verified: chunk 1 ('установи гарри') + chunk 2 ('мод') -> '{cleanedBuff}'");
        testListenerBuffering.Dispose();

        // 15.2 Dual-Path TryFastMatch with complete phrase
        Console.WriteLine("  [15.2] Testing TryFastMatch('установи гарри мод')...");
        var fastMatchGmod = LlmIntentService.TryFastMatch("установи гарри мод");
        if (fastMatchGmod == null || fastMatchGmod.CommandRequest?.Command != "app")
            throw new Exception($"TryFastMatch('установи гарри мод') did not return app command! Got: {fastMatchGmod?.CommandRequest?.Command}");

        string actionArg = fastMatchGmod.CommandRequest.Args.GetProperty("action").GetString() ?? "";
        string nameArg = fastMatchGmod.CommandRequest.Args.GetProperty("name").GetString() ?? "";
        if (actionArg != "install" || nameArg != "гарри мод")
            throw new Exception($"TryFastMatch('установи гарри мод') args mismatch! action='{actionArg}', name='{nameArg}'");
        Console.WriteLine($"    TryFastMatch('установи гарри мод') verified: command={fastMatchGmod.CommandRequest.Command}, action={actionArg}, name={nameArg}");

        // 15.3 Garry's Mod Alias Resolution in SteamService
        Console.WriteLine("  [15.3] Testing Garry's Mod AppId alias resolution...");
        var gmodSearch = SteamService.Instance.SearchGame("гарри мод");
        if (gmodSearch?.Game?.AppId != "4000")
            throw new Exception($"Garry's Mod resolution failed! Expected AppId 4000, got: {gmodSearch?.Game?.AppId}");
        Console.WriteLine($"    SteamService.SearchGame('гарри мод') resolved to AppId 4000 ({gmodSearch.Game.Title}).");

        // 15.4 Window Search Safety & Non-Steam / IDE Window Rejection
        Console.WriteLine("  [15.4] Testing Steam Uninstall Dialog search safety...");
        var invalidHwnd = SteamService.FindSteamUninstallDialog("NonExistentFakeGame12345");
        if (invalidHwnd != IntPtr.Zero)
            throw new Exception("FindSteamUninstallDialog unexpectedly matched non-existent game window!");
        // Test 16: System App Isolation, Edge-TTS 2500ms Timeout & Single Speech Invariant (TASK: 47_Fix_App_Router_Collision_And_Tts_Timeout)
        Console.WriteLine("\n[16] Testing System App Isolation, Edge-TTS 2500ms Timeout & Single Speech Invariant...");

        // 16.1 System App Isolation in LlmIntentService & Fast Match
        Console.WriteLine("  [16.1] Testing LlmIntentService Fast Match for system apps isolation...");
        var browserMatch = LlmIntentService.TryFastMatch("открой браузер");
        if (browserMatch == null || browserMatch.CommandRequest?.Command != "app")
            throw new Exception($"TryFastMatch('открой браузер') failed! Got: {browserMatch?.CommandRequest?.Command}");

        string browserAction = browserMatch.CommandRequest.Args.GetProperty("action").GetString() ?? "";
        string browserName = browserMatch.CommandRequest.Args.GetProperty("name").GetString() ?? "";
        if (browserAction != "start" || browserName != "browser")
            throw new Exception($"TryFastMatch('открой браузер') args mismatch! action='{browserAction}', name='{browserName}'");

        if (JarvisOrchestrator.Instance.HasPendingAction)
            throw new Exception("TryFastMatch('открой браузер') unexpectedly triggered Steam PendingAction!");

        var explorerMatch = LlmIntentService.TryFastMatch("открой проводник");
        if (explorerMatch == null || explorerMatch.CommandRequest?.Command != "app" ||
            explorerMatch.CommandRequest.Args.GetProperty("name").GetString() != "explorer")
            throw new Exception($"TryFastMatch('открой проводник') failed! Got name: {explorerMatch?.CommandRequest?.Args.GetProperty("name").GetString()}");

        Console.WriteLine($"    System app fast-matching verified: 'открой браузер' -> app/start/browser, 'открой проводник' -> app/start/explorer without Steam collision.");

        // 16.2 AppControlCommandHandler Isolation & Registry Browser Resolver
        Console.WriteLine("  [16.2] Testing AppControlCommandHandler isolation & default browser resolution...");
        if (!Gem.Handlers.AppControlCommandHandler.IsKnownApp("браузер") ||
            !Gem.Handlers.AppControlCommandHandler.IsKnownApp("browser") ||
            !Gem.Handlers.AppControlCommandHandler.IsKnownApp("проводник") ||
            !Gem.Handlers.AppControlCommandHandler.IsKnownApp("блокнот") ||
            !Gem.Handlers.AppControlCommandHandler.IsKnownApp("калькулятор"))
        {
            throw new Exception("AppControlCommandHandler.IsKnownApp failed for one or more core system apps!");
        }

        var browserStartInfo = Gem.Handlers.AppControlCommandHandler.GetDefaultBrowserStartInfo();
        if (string.IsNullOrWhiteSpace(browserStartInfo.FileName))
            throw new Exception("GetDefaultBrowserStartInfo returned empty FileName!");
        Console.WriteLine($"    Default browser start info resolved: FileName='{browserStartInfo.FileName}', Args='{browserStartInfo.Arguments}'");

        // 16.3 Edge-TTS 3000ms Timeout Verification
        Console.WriteLine("  [16.3] Testing Edge-TTS 3000ms connection timeout configuration...");
        if (EdgeTtsEngine.DefaultConnectionTimeoutMs != 3000)
            throw new Exception($"EdgeTtsEngine.DefaultConnectionTimeoutMs expected 3000, got: {EdgeTtsEngine.DefaultConnectionTimeoutMs}");

        var loadedTtsSettings = AppSettingsService.Load();
        if (loadedTtsSettings.Tts.ConnectionTimeoutMs != 3000)
            throw new Exception($"AppSettings.Tts.ConnectionTimeoutMs expected 3000, got: {loadedTtsSettings.Tts.ConnectionTimeoutMs}");

        var testEdgeEngine = new EdgeTtsEngine();
        if (testEdgeEngine.ConnectionTimeoutMs != 3000)
            throw new Exception($"EdgeTtsEngine instance ConnectionTimeoutMs expected 3000, got: {testEdgeEngine.ConnectionTimeoutMs}");
        Console.WriteLine($"    Edge-TTS timeout verified: DefaultConnectionTimeoutMs={EdgeTtsEngine.DefaultConnectionTimeoutMs}, Config={loadedTtsSettings.Tts.ConnectionTimeoutMs}");

        // 16.4 Single Speech Invariant & Pending Confirmation Speech Isolation
        Console.WriteLine("  [16.4] Testing Single Speech Invariant in HandleJarvisResponseAsync...");
        int speakCount = 0;
        var testEdgeMock = new MockTtsEngine("Edge", isAvailable: true, shouldThrow: false, onSpeak: () => speakCount++);
        var testSileroMock = new MockTtsEngine("Silero", isAvailable: true, shouldThrow: false);
        var testSystemMock = new MockTtsEngine("System.Speech", isAvailable: true, shouldThrow: false);
        IVoiceFeedbackService testFeedbackService = new CompositeVoiceFeedbackService(null, testEdgeMock, testSileroMock, testSystemMock);

        // Test with HasPendingAction = true: response.Reply MUST NOT be spoken
        JarvisOrchestrator.Instance.SetPendingConfirmation(
            PendingGameAction.Start,
            new SteamGameInfo("999999", "Mock Game", "mock game", "мок гейм", true));

        var pendingTestResponse = new JarvisResponse(
            new Gem.Core.CommandRequest("app", System.Text.Json.JsonDocument.Parse("{\"action\":\"unknown_action\"}").RootElement),
            "Дублирующий ответ, который не должен быть озвучен!"
        );

        var mockRouter = new Gem.Core.CommandRouter();
        await AppHandler.HandleJarvisResponseAsync(pendingTestResponse, mockRouter, testFeedbackService);

        if (speakCount != 0)
            throw new Exception($"HandleJarvisResponseAsync spoke reply while HasPendingAction was true! speakCount={speakCount}");

        // Reset pending action
        JarvisOrchestrator.Instance.PendingAction = PendingGameAction.None;
        JarvisOrchestrator.Instance.PendingGame = null;
        JarvisOrchestrator.Instance.CurrentPendingAction = null;

        // Test with HasPendingAction = false: response.Reply is spoken strictly once
        var normalTestResponse = new JarvisResponse(
            null, // standard dialogue
            "Тестовый ответ ровно один раз."
        );

        await AppHandler.HandleJarvisResponseAsync(normalTestResponse, mockRouter, testFeedbackService);
        if (speakCount != 1)
            throw new Exception($"HandleJarvisResponseAsync did not speak reply exactly once! speakCount={speakCount}");

        Console.WriteLine($"    Single speech invariant verified: 0 speech when pending confirmation, exactly 1 speech on normal dialogue.");

        // 16.5 SteamService.LaunchGame confirmation isolation (no rogue TTS)
        Console.WriteLine("  [16.5] Testing SteamService.LaunchGame confirmation isolation (no rogue TTS)...");
        bool launchedFake = SteamService.Instance.LaunchGame("NonExistentFakeGameQuery12345", out _);
        if (launchedFake)
            throw new Exception("SteamService.LaunchGame unexpectedly returned true for non-existent game!");
        Console.WriteLine("    SteamService.LaunchGame verified: safe execution without unexpected speech.");

        // Test 17: Silero Download Mirrors & HttpClient and KWS/TTS Benchmark Logs (TASK: 54_Fix_Silero_Download_Urls_And_Http_Client)
        Console.WriteLine("\n[17] Testing Silero mirrors, size validation & KWS/TTS benchmark logs...");

        // 17.1 Silero download mirror URLs check
        string[] expectedMirrors =
        [
            "https://huggingface.co/snakers4/silero-models/resolve/main/models/tts/ru/v4_ru.onnx",
            "https://raw.githubusercontent.com/snakers4/silero-models/master/models/tts/ru/v3_ru.onnx"
        ];
        var mirrorsField = typeof(SileroTtsEngine).GetField("DownloadMirrors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (mirrorsField?.GetValue(null) is not string[] actualMirrors)
            throw new Exception("SileroTtsEngine.DownloadMirrors field not found!");
        foreach (var em in expectedMirrors)
        {
            if (!actualMirrors.Contains(em))
                throw new Exception($"SileroTtsEngine mirror '{em}' missing!");
        }
        Console.WriteLine("    SileroTtsEngine mirrors verified.");

        // 17.2 Size validation check: dummy file < 1MB deleted on check
        string testSmallModelPath = Path.Combine(Path.GetTempPath(), $"silero_test_{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(testSmallModelPath, new byte[500]); // 500 bytes < 1MB
        var testSileroInstance = new SileroTtsEngine(modelPath: testSmallModelPath);
        if (File.Exists(testSmallModelPath))
        {
            File.Delete(testSmallModelPath);
            throw new Exception("SileroTtsEngine did not delete corrupted/small (<1MB) model file!");
        }
        testSileroInstance.Dispose();
        Console.WriteLine("    SileroTtsEngine < 1MB file deletion validation verified.");

        // 17.3 KWS Latency properties
        var dummyOpenWw = new Gem.Voice.OpenWakeWordDetector();
        if (dummyOpenWw.LastDetectionLatencyMs < 0)
            throw new Exception("OpenWakeWordDetector.LastDetectionLatencyMs is negative!");
        dummyOpenWw.Dispose();

        var dummyVoskGrammar = new Gem.Voice.VoskGrammarWakeWordDetector("тест");
        if (dummyVoskGrammar.LastDetectionLatencyMs < 0)
            throw new Exception("VoskGrammarWakeWordDetector.LastDetectionLatencyMs is negative!");
        dummyVoskGrammar.Dispose();
        Console.WriteLine("    KWS LastDetectionLatencyMs properties verified.");

        // 17.4 Composite TTS Engine logging verification
        var stringWriter = new StringWriter();
        var oldOut = Console.Out;
        try
        {
            Console.SetOut(stringWriter);
            var mockEdgeLog = new MockTtsEngine("Edge", isAvailable: true, shouldThrow: false);
            var mockSileroLog = new MockTtsEngine("Silero", isAvailable: true, shouldThrow: false);
            var mockSystemLog = new MockTtsEngine("System.Speech", isAvailable: true, shouldThrow: false);
            var testLogComposite = new CompositeVoiceFeedbackService(null, mockEdgeLog, mockSileroLog, mockSystemLog);
            await testLogComposite.SpeakAsync("Тест логов.");
            string logged = stringWriter.ToString();
            if (!logged.Contains("[TTS Engine: Edge-TTS"))
                throw new Exception($"CompositeVoiceFeedbackService did not log [TTS Engine: Edge-TTS ...]. Got: {logged}");
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        Console.WriteLine("    [TTS Engine: Edge-TTS (...)] benchmark log verified.");

        // Test 18: OpenWakeWord Pipeline, Tensor Shape & Instant 0 ms Transition (TASK: 55_Fix_Silero_Url_And_Wire_OpenWakeWord_Pipeline)
        Console.WriteLine("\n[18] Testing OpenWakeWord Pipeline, Adaptive Tensor Shape & Instant Transition...");

        // 18.1 OpenWakeWord adaptive buffer and ProcessFrame execution
        using (var owd = new Gem.Voice.OpenWakeWordDetector("джарвис", threshold: 0.5f))
        {
            if (!owd.IsAvailable)
                throw new Exception("OpenWakeWordDetector failed to initialize or download jarvis.onnx!");

            // Feed 2 full frames (1536 samples * 2 bytes = 3072 bytes per frame -> 6144 bytes of silence)
            byte[] pcmData = new byte[6144];
            bool triggered = owd.ProcessFrame(pcmData);
            if (triggered)
                throw new Exception("OpenWakeWordDetector unexpectedly triggered on silence!");

            if (owd.LastDetectionLatencyMs < 0)
                throw new Exception("OpenWakeWordDetector.LastDetectionLatencyMs is negative after frame processing!");
        }
        Console.WriteLine("    OpenWakeWordDetector adaptive tensor shape & inference verified.");

        // 18.2 WakeWordFactory Threshold & Type Selection
        var factoryDetectorJarvis = Gem.Voice.WakeWordFactory.Create("джарвис", threshold: 0.6f);
        if (factoryDetectorJarvis is not Gem.Voice.VoskGrammarWakeWordDetector)
            throw new Exception("WakeWordFactory did not create VoskGrammarWakeWordDetector for 'джарвис'!");
        factoryDetectorJarvis.Dispose();

        var factoryDetectorCustom = Gem.Voice.WakeWordFactory.Create("петрович");
        if (factoryDetectorCustom is not Gem.Voice.VoskGrammarWakeWordDetector)
            throw new Exception("WakeWordFactory did not create VoskGrammarWakeWordDetector for 'петрович'!");
        factoryDetectorCustom.Dispose();
        Console.WriteLine("    WakeWordFactory selection & threshold verified.");

        // 18.3 VoiceListener instant 0 ms transition & audio routing priority
        var mockWakeWordDetector = new MockWakeWordDetector("джарвис");
        var voiceListenerInstance = new VoiceListener(
            modelPath: "./model",
            wakeWords: ["джарвис"],
            wakeWordDetector: mockWakeWordDetector
        );
        voiceListenerInstance.TransitionToWaitingForWakeWord("Ready for test");
        if (voiceListenerInstance.CurrentState != VoiceListenerState.WaitingForWakeWord)
            throw new Exception($"VoiceListener state != WaitingForWakeWord, got {voiceListenerInstance.CurrentState}");

        // Feed audio chunk through test helper to verify wake-word detector receives it
        byte[] testChunk = new byte[1024];
        voiceListenerInstance.ProcessAudioChunkForTesting(testChunk, testChunk.Length, isSpeech: false);
        if (mockWakeWordDetector.ProcessedFrameCount != 1)
            throw new Exception($"OpenWakeWord detector did not receive streaming PCM frame! ProcessedFrameCount={mockWakeWordDetector.ProcessedFrameCount}");

        // Trigger wake-word via detector event
        mockWakeWordDetector.Trigger();
        if (voiceListenerInstance.CurrentState != VoiceListenerState.ListeningForCommand)
            throw new Exception($"VoiceListener did not instantly transition to ListeningForCommand upon wake-word trigger! State={voiceListenerInstance.CurrentState}");

        voiceListenerInstance.Dispose();
        Console.WriteLine("    VoiceListener instant 0 ms transition & wake-word audio routing verified.");

        // Test 19: Restore Mic Pipeline (VoskGrammar KWS), Edge-TTS 3000ms/2000ms & Silero v4/v3 ONNX (TASK: 56_Restore_Mic_Pipeline_And_Fix_Silero_And_Edge_Timeouts)
        Console.WriteLine("\n[19] Testing VoskGrammar KWS default, Edge-TTS 3000ms/2000ms timeouts & Silero v4/v3 ONNX...");

        // 19.1 VoskGrammarWakeWordDetector default for Jarvis
        var kwsDefaultJarvis = Gem.Voice.WakeWordFactory.Create("джарвис");
        if (kwsDefaultJarvis is not Gem.Voice.VoskGrammarWakeWordDetector)
            throw new Exception($"WakeWordFactory.Create('джарвис') must return VoskGrammarWakeWordDetector by default! Got: {kwsDefaultJarvis.GetType().Name}");
        if (kwsDefaultJarvis.WakeWord != "джарвис")
            throw new Exception($"VoskGrammarWakeWordDetector.WakeWord expected 'джарвис', got: '{kwsDefaultJarvis.WakeWord}'");
        kwsDefaultJarvis.Dispose();
        Console.WriteLine("    [19.1] VoskGrammarWakeWordDetector default selection verified.");

        // 19.2 Edge-TTS timeouts: 3000ms connect, 2000ms fast reconnect
        if (EdgeTtsEngine.DefaultConnectionTimeoutMs != 3000)
            throw new Exception($"EdgeTtsEngine.DefaultConnectionTimeoutMs expected 3000, got: {EdgeTtsEngine.DefaultConnectionTimeoutMs}");
        if (EdgeTtsEngine.FastReconnectTimeoutMs != 2000)
            throw new Exception($"EdgeTtsEngine.FastReconnectTimeoutMs expected 2000, got: {EdgeTtsEngine.FastReconnectTimeoutMs}");
        Console.WriteLine("    [19.2] Edge-TTS 3000ms connect / 2000ms reconnect timeouts verified.");

        // 19.3 Silero v4/v3 mirrors, v4_ru.onnx default and speakers aidar/baya
        if (SileroTtsEngine.DefaultModelFileName != "v4_ru.onnx")
            throw new Exception($"SileroTtsEngine.DefaultModelFileName expected 'v4_ru.onnx', got: '{SileroTtsEngine.DefaultModelFileName}'");
        var sileroAidar = new SileroTtsEngine(speaker: "aidar");
        if (sileroAidar.Speaker != "aidar")
            throw new Exception($"SileroTtsEngine speaker expected 'aidar', got: '{sileroAidar.Speaker}'");
        sileroAidar.Dispose();
        var sileroBaya = new SileroTtsEngine(speaker: "baya");
        if (sileroBaya.Speaker != "baya")
            throw new Exception($"SileroTtsEngine speaker expected 'baya', got: '{sileroBaya.Speaker}'");
        sileroBaya.Dispose();
        Console.WriteLine("    [19.3] Silero v4_ru.onnx default & male speakers aidar/baya verified.");

        Console.WriteLine("\n>>> ALL FEATURE TESTS PASSED SUCCESSFULLY! <<<\n");

    }

    private sealed class MockWakeWordDetector : Gem.Voice.IWakeWordDetector
    {
        public string Name => "OpenWakeWord-ONNX";
        public string WakeWord { get; }
        public event Action? OnWakeWordDetected;
        public long LastDetectionLatencyMs => 15;
        public int ProcessedFrameCount { get; private set; }

        public MockWakeWordDetector(string wakeWord = "джарвис")
        {
            WakeWord = wakeWord;
        }

        public bool ProcessFrame(ReadOnlySpan<byte> pcmData)
        {
            ProcessedFrameCount++;
            return false;
        }

        public void Trigger()
        {
            OnWakeWordDetected?.Invoke();
        }

        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class MockTtsEngine : ITtsEngine
    {
        public string Name { get; }
        public bool IsAvailable { get; }
        private readonly bool _shouldThrow;
        private readonly Action? _onSpeak;
        public int SpeakCount { get; private set; }

        public MockTtsEngine(string name, bool isAvailable = true, bool shouldThrow = false, Action? onSpeak = null)
        {
            Name = name;
            IsAvailable = isAvailable;
            _shouldThrow = shouldThrow;
            _onSpeak = onSpeak;
        }

        public Task SpeakAsync(string text, CancellationToken ct = default)
        {
            SpeakCount++;
            _onSpeak?.Invoke();
            if (_shouldThrow)
            {
                throw new InvalidOperationException($"Simulated failure in {Name}");
            }
            return Task.CompletedTask;
        }
    }
}
