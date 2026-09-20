using System.Diagnostics;
using System.Windows;

namespace Gem.Services;

public sealed class AppHandler
{
    private readonly IVoiceFeedbackService? _voiceFeedback;

    public AppHandler(IVoiceFeedbackService? voiceFeedback = null)
    {
        _voiceFeedback = voiceFeedback;
    }

    // Словарь соответствия игр Steam (StringComparer.OrdinalIgnoreCase)
    public static readonly Dictionary<string, string> SteamGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Monster Hunter Wilds: ключи "mhw_wilds", "wilds", "вайлдс" -> AppID "2246340"
        ["mhw_wilds"]             = "2246340",
        ["wilds"]                 = "2246340",
        ["вайлдс"]                = "2246340",
        ["уайлд"]                 = "2246340",
        ["уайлдс"]                = "2246340",
        ["монстер хантер вайлдс"] = "2246340",
        ["монстер хантер уайлд"]  = "2246340",

        // Monster Hunter World: ключи "mhw_world", "world", "ворлд", "монстер хантер", "монстер хантер ворлд" -> AppID "582010"
        ["mhw_world"]             = "582010",
        ["world"]                 = "582010",
        ["ворлд"]                 = "582010",
        ["монстер хантер"]        = "582010",
        ["монстер хантер ворлд"]  = "582010",

        // Dota 2
        ["дота"]                  = "570",
        ["dota"]                  = "570",

        // CS
        ["кс"]                    = "730",
        ["cs"]                    = "730",
        ["контра"]                = "730",

        // Garry's Mod
        ["гарри мод"]             = "4000",
        ["гаррис мод"]            = "4000",
        ["garrys mod"]            = "4000",
        ["garry's mod"]           = "4000",
        ["gmod"]                  = "4000",
    };

    // Словарь соответствия процессов игр для завершения через Process.Kill()
    public static readonly Dictionary<string, string> SteamProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mhw_wilds"]             = "MonsterHunterWilds",
        ["mhw_world"]             = "MonsterHunterWorld",
        ["wilds"]                 = "MonsterHunterWilds",
        ["вайлдс"]                = "MonsterHunterWilds",
        ["уайлд"]                 = "MonsterHunterWilds",
        ["уайлдс"]                = "MonsterHunterWilds",
        ["world"]                 = "MonsterHunterWorld",
        ["ворлд"]                 = "MonsterHunterWorld",
        ["монстер хантер"]        = "MonsterHunterWorld",
        ["монстер хантер вайлдс"] = "MonsterHunterWilds",
        ["монстер хантер уайлд"]  = "MonsterHunterWilds",
        ["монстер хантер ворлд"]  = "MonsterHunterWorld",
        ["MonsterHunterWilds"]    = "MonsterHunterWilds",
        ["MonsterHunterWorld"]    = "MonsterHunterWorld",
        ["гарри мод"]             = "hl2",
        ["гаррис мод"]            = "hl2",
        ["gmod"]                  = "hl2",
    };

    // Обычные приложения
    public static readonly Dictionary<string, string> AppTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Steam
        ["steam"]    = "steam://open/main",
        ["стим"]     = "steam://open/main",
        ["тим"]      = "steam://open/main",
        ["тем"]      = "steam://open/main",

        // Brave / Chrome
        ["brave"]    = "brave.exe",
        ["брейв"]    = "brave.exe",
        ["браузер"]  = "brave.exe",
        ["хром"]     = "brave.exe",
        ["chrome"]   = "brave.exe",

        // Discord
        ["discord"]  = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",
        ["дискорд"]  = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",
        ["диск"]     = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",

        // Notepad
        ["notepad"]  = "notepad.exe",
        ["блокнот"]  = "notepad.exe",

        // Calc
        ["calc"]     = "calc.exe",
        ["калькулятор"] = "calc.exe",
    };

    public static readonly HashSet<string> ExitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "jarvis", "джарвис", "себя", "ассистент", "gem"
    };

    /// <summary>
    /// Проверяет, является ли имя указанием на завершение работы самого ассистента JARVIS.
    /// </summary>
    public static bool IsExitTarget(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim().ToLowerInvariant();
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }
        return ExitNames.Contains(key);
    }

    /// <summary>
    /// Обновляет словарь игр Steam из настроек.
    /// </summary>
    public static void SetSteamGames(IDictionary<string, string> games)
    {
        foreach (var kv in games)
        {
            SteamGames[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Основной метод обработки команд запуска и завершения приложений.
    /// </summary>
    public bool Handle(string action, string appName, string? farewellPhrase = null)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            Console.WriteLine("[AppHandler Warning] Имя приложения не указано.");
            return false;
        }

        string normalizedAction = action.Trim().ToLowerInvariant();
        string key = appName.Trim().ToLowerInvariant();

        // 1. При имени "jarvis" или "джарвис" для команды завершения выполняем голосовое прощание и выход
        if ((normalizedAction == "close" || normalizedAction == "stop" || normalizedAction == "kill" || normalizedAction == "exit")
            && IsExitTarget(key))
        {
            CloseJarvis(farewellPhrase);
            return true;
        }

        // 2. При action == "install":
        if (normalizedAction == "install")
        {
            return SteamService.Instance.InstallGameAsync(appName, title =>
            {
                _voiceFeedback?.SpeakAsync($"Начинаю установку {title}, сэр.");
            }).GetAwaiter().GetResult();
        }

        // 3. При action == "start":
        if (normalizedAction == "start")
        {
            if (SteamService.Instance.LaunchGame(appName, out _))
            {
                return true;
            }
            return StartAppOrGame(key, appName);
        }

        if (normalizedAction == "close" || normalizedAction == "stop" || normalizedAction == "kill")
        {
            return CloseApp(key);
        }

        return false;
    }


    /// <summary>
    /// Выполняет корректное завершение работы JARVIS с предварительным блокирующим голосовым прощанием.
    /// </summary>
    public void CloseJarvis(string? farewellPhrase = null)
    {
        ShutdownJarvis(farewellPhrase, _voiceFeedback);
    }

    /// <summary>
    /// Централизованный метод обработки ответов JARVIS (Погода, Завершение, Диалог, Команды).
    /// </summary>
    public static async Task HandleJarvisResponseAsync(
        JarvisResponse response,
        Gem.Core.CommandRouter router,
        IVoiceFeedbackService feedbackService,
        WeatherService? weatherService = null)
    {
        var commandRequest = response.CommandRequest;

        // 1. ПОГОДА: commandRequest?.command == "weather"
        if (commandRequest != null && commandRequest.Command.Equals("weather", StringComparison.OrdinalIgnoreCase))
        {
            var weather = weatherService ?? WeatherService.Instance ?? new WeatherService();

            string? targetCity = null;
            if (commandRequest.Args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                commandRequest.Args.TryGetProperty("city", out var cityProp) &&
                cityProp.ValueKind == System.Text.Json.JsonValueKind.String)
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
            if (commandRequest.Args.ValueKind == System.Text.Json.JsonValueKind.Object &&
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


    public static bool IsSystemExitRequest(Gem.Core.CommandRequest request)
    {
        if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Object)
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

        return isCloseAction && IsExitTarget(name);
    }

    /// <summary>
    /// Статический метод для озвучивания прощания и корректного завершения процесса приложения.
    /// </summary>
    public static void ShutdownJarvis(string? farewellPhrase = null, IVoiceFeedbackService? voiceFeedback = null)
    {
        string phrase = !string.IsNullOrWhiteSpace(farewellPhrase)
            ? farewellPhrase
            : "До свидания, сэр.";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[AppHandler] Завершение работы JARVIS: \"{phrase}\"");
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
            Console.WriteLine($"[AppHandler Error] Ошибка воспроизведения прощания: {ex.Message}");
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
                                Console.WriteLine($"[AppHandler Warning] Ошибка закрытия окна: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AppHandler Warning] Ошибка перечисления окон: {ex.Message}");
                    }

                    try
                    {
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AppHandler Warning] Ошибка Application.Current.Shutdown: {ex.Message}");
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
            Console.WriteLine($"[AppHandler Warning] Ошибка при завершении WPF: {ex.Message}");
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
            Console.WriteLine($"[AppHandler Warning] Ошибка Environment.Exit: {ex.Message}");
        }
    }

    private bool StartAppOrGame(string key, string originalName)
    {
        try
        {
            // 1. Проверяем запуск через SteamService (поддержка фонетики, каталога Steam, алиасов)
            if (SteamService.Instance.LaunchGame(originalName, out _))
            {
                return true;
            }

            // 2. Проверяем, есть ли имя в словаре игр Steam
            if (SteamGames.TryGetValue(key, out string? appId))

            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[AppHandler] Запуск Steam игры '{originalName}' (AppID: {appId})...");
                Console.ResetColor();

                var steamStartInfo = new ProcessStartInfo($"steam://run/{appId}")
                {
                    UseShellExecute = true
                };
                Process.Start(steamStartInfo);
                return true;
            }

            // 2. Проверяем словарь известных приложений с особыми путями
            if (AppTargets.TryGetValue(key, out string? rawTarget))
            {
                string expanded = Environment.ExpandEnvironmentVariables(rawTarget);
                string fileName = expanded;
                string arguments = string.Empty;

                int spaceIndex = expanded.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    fileName = expanded[..spaceIndex];
                    arguments = expanded[(spaceIndex + 1)..];
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[AppHandler] Запуск настроенного приложения: '{fileName}' {arguments}");
                Console.ResetColor();

                var targetStartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true
                };
                Process.Start(targetStartInfo);
                return true;
            }

            // 3. Стандартный запуск исполняемого файла через Process.Start с UseShellExecute = true
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[AppHandler] Стандартный запуск исполняемого файла: '{originalName}'");
            Console.ResetColor();

            var startInfo = new ProcessStartInfo
            {
                FileName = originalName,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[AppHandler Error] Ошибка запуска приложения или игры '{originalName}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    private bool CloseApp(string key)
    {
        try
        {
            string processName = key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? key[..^4] : key;

            // 1. Проверяем в словаре соответствия процессов (например, mhw_wilds -> MonsterHunterWilds)
            string targetName = processName;
            if (SteamProcessNames.TryGetValue(processName, out var mappedName) && !string.IsNullOrWhiteSpace(mappedName))
            {
                targetName = mappedName;
            }

            var processes = Process.GetProcessesByName(targetName);
            if (processes.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[AppHandler] Найдено {processes.Length} процессов '{targetName}'. Завершаю через Process.Kill()...");
                Console.ResetColor();

                foreach (var p in processes)
                {
                    try
                    {
                        Console.WriteLine($"[AppHandler] Завершение процесса PID={p.Id} ({p.ProcessName}) через Kill()...");
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"[AppHandler Warning] Ошибка при завершении процесса PID={p.Id}: {ex.Message}");
                        Console.ResetColor();
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }

                Console.WriteLine($"[AppHandler] Успешно завершено процессов '{targetName}': {processes.Length}");
                return true;
            }

            // 2. Если точное совпадение не найдено, выполни поиск среди запущенных процессов по условию proc.ProcessName.Contains("MonsterHunter", StringComparison.OrdinalIgnoreCase)
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[AppHandler] Точное совпадение с '{targetName}' не найдено. Выполняю поиск процессов с 'MonsterHunter'...");
            Console.ResetColor();

            var allProcesses = Process.GetProcesses();
            var mhProcesses = new List<Process>();
            foreach (var p in allProcesses)
            {
                try
                {
                    if (p.ProcessName.Contains("MonsterHunter", StringComparison.OrdinalIgnoreCase))
                    {
                        mhProcesses.Add(p);
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
                catch
                {
                    p.Dispose();
                }
            }

            if (mhProcesses.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[AppHandler] Найдено {mhProcesses.Count} процессов MonsterHunter. Завершаю с логированием...");
                Console.ResetColor();

                foreach (var p in mhProcesses)
                {
                    try
                    {
                        Console.WriteLine($"[AppHandler] Завершение процесса MonsterHunter PID={p.Id} ({p.ProcessName}) через Kill()...");
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"[AppHandler Warning] Ошибка завершения MonsterHunter PID={p.Id}: {ex.Message}");
                        Console.ResetColor();
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }

                Console.WriteLine($"[AppHandler] Завершено процессов MonsterHunter: {mhProcesses.Count}");
                return true;
            }

            // Обычные процессы не найдены
            Console.WriteLine($"[AppHandler] Процессы '{targetName}' / MonsterHunter не найдены для закрытия.");
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[AppHandler Error] Ошибка при закрытии процесса '{key}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    private static void ExecuteMediaFallback(System.Text.Json.JsonElement args)
    {
        string action = "play_pause";
        if (args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty("action", out var actProp))
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
