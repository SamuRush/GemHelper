using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Services.App;

namespace Gem.Services;

public sealed class AppHandler
{
    private readonly IVoiceFeedbackService? _voiceFeedback;

    public AppHandler(IVoiceFeedbackService? voiceFeedback = null)
    {
        _voiceFeedback = voiceFeedback;
    }

    public static Dictionary<string, string> SteamGames => AppCatalog.SteamGames;
    public static Dictionary<string, string> SteamProcessNames => AppCatalog.SteamProcessNames;
    public static Dictionary<string, string> AppTargets => AppCatalog.AppTargets;
    public static HashSet<string> ExitNames => AppCatalog.ExitNames;

    public static bool IsExitTarget(string name) => AppCatalog.IsExitTarget(name);

    public static void SetSteamGames(IDictionary<string, string> games) => AppCatalog.SetSteamGames(games);

    public void CloseJarvis(string? farewellPhrase = null) =>
        JarvisResponseDispatcher.ShutdownJarvis(farewellPhrase, _voiceFeedback);

    public static void ShutdownJarvis(string? farewellPhrase = null, IVoiceFeedbackService? voiceFeedback = null) =>
        JarvisResponseDispatcher.ShutdownJarvis(farewellPhrase, voiceFeedback);

    public static Task HandleJarvisResponseAsync(
        JarvisResponse response,
        CommandRouter router,
        IVoiceFeedbackService feedbackService,
        WeatherService? weatherService = null) =>
        JarvisResponseDispatcher.HandleJarvisResponseAsync(response, router, feedbackService, weatherService);

    public static bool IsSystemExitRequest(CommandRequest request) =>
        JarvisResponseDispatcher.IsSystemExitRequest(request);

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

            // 3. Проверяем словарь известных приложений с особыми путями
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

            // 4. Стандартный запуск исполняемого файла через Process.Start с UseShellExecute = true
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
}
