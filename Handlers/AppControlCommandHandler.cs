using System.Diagnostics;
using System.Text.Json;
using Gem.Core;

namespace Gem.Handlers;

/// <summary>
/// Command handler for launching and terminating applications by name or path.
/// Includes smart alias mapping for popular apps and common Vosk phonetic misrecognitions
/// ("steam"/"tim"/"стим", "discord"/"диск", "telegram"/"телега", "chrome"/"хром"/"браузер", etc.).
/// </summary>
public sealed class AppControlCommandHandler : ICommandHandler
{
    public string CommandName => "app";

    private readonly Gem.Services.IVoiceFeedbackService? _voiceFeedback;

    public AppControlCommandHandler(Gem.Services.IVoiceFeedbackService? voiceFeedback = null)
    {
        _voiceFeedback = voiceFeedback;
    }

    private Gem.Services.IVoiceFeedbackService? VoiceFeedback => _voiceFeedback ?? Gem.Services.CompositeVoiceFeedbackService.Instance;

    private record AppDefinition(
        string CanonicalId,
        string[] Aliases,
        string[] ProcessNames,
        Func<string, ProcessStartInfo> CreateStartInfo
    );

    /// <summary>
    /// Медиа-сущности, которые не являются исполняемыми файлами.
    /// При получении команды app/start с таким именем делегируем в MediaKeyService.PlayPause()
    /// вместо попытки Process.Start, чтобы предотвратить Win32Exception.
    /// </summary>
    private static readonly HashSet<string> _mediaAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "музыка", "музыку", "музыки",
        "трек", "треки", "трека",
        "песня", "песню", "песни",
        "плеер", "проигрыватель",
        "воспроизведение", "медиа", "аудио",
        "spotify", "спотифай",
        "яндекс музыка", "яндекс.музыка", "яндекс музыку"
    };


    private static readonly List<AppDefinition> KnownApps = new()
    {
        // 1. Steam ("steam", "tim", "стим")
        new AppDefinition(
            CanonicalId: "steam",
            Aliases: new[] { "steam", "tim", "тим", "стим", "стима" },
            ProcessNames: new[] { "steam", "Steam" },
            CreateStartInfo: (args) =>
            {
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string steamExe = Path.Combine(programFilesX86, "Steam", "steam.exe");

                if (File.Exists(steamExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = args,
                        UseShellExecute = true
                    };
                }

                // Fallback: Steam URI protocol registered in Windows Shell
                return new ProcessStartInfo
                {
                    FileName = "steam://open/main",
                    UseShellExecute = true
                };
            }
        ),

        // 2. Discord ("discord", "дискорд", "диск")
        new AppDefinition(
            CanonicalId: "discord",
            Aliases: new[] { "discord", "дискорд", "диск", "дискорда" },
            ProcessNames: new[] { "Discord" },
            CreateStartInfo: (args) =>
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string updateExe = Path.Combine(localAppData, "Discord", "Update.exe");

                if (File.Exists(updateExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = updateExe,
                        Arguments = "--processStart Discord.exe" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args),
                        UseShellExecute = true
                    };
                }

                // Check app-* directory inside Discord folder
                string discordDir = Path.Combine(localAppData, "Discord");
                if (Directory.Exists(discordDir))
                {
                    var appDirs = Directory.GetDirectories(discordDir, "app-*");
                    if (appDirs.Length > 0)
                    {
                        string latestExe = Path.Combine(appDirs.OrderByDescending(d => d).First(), "Discord.exe");
                        if (File.Exists(latestExe))
                        {
                            return new ProcessStartInfo
                            {
                                FileName = latestExe,
                                Arguments = args,
                                UseShellExecute = true
                            };
                        }
                    }
                }

                // Fallback: discord protocol or PATH
                return new ProcessStartInfo
                {
                    FileName = "discord",
                    Arguments = args,
                    UseShellExecute = true
                };
            }
        ),

        // 3. Telegram ("telegram", "телега", "телеграм")
        new AppDefinition(
            CanonicalId: "telegram",
            Aliases: new[] { "telegram", "телега", "телеграм", "телегу", "телеграмм", "tg" },
            ProcessNames: new[] { "Telegram" },
            CreateStartInfo: (args) =>
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string telegramExe = Path.Combine(appData, "Telegram Desktop", "Telegram.exe");

                if (File.Exists(telegramExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = telegramExe,
                        Arguments = args,
                        UseShellExecute = true
                    };
                }

                // Fallback: tg protocol or PATH
                return new ProcessStartInfo
                {
                    FileName = "telegram",
                    Arguments = args,
                    UseShellExecute = true
                };
            }
        ),

        // 4. Browser ("browser", "браузер", "brave", "хром", "chrome", etc.)
        new AppDefinition(
            CanonicalId: "browser",
            Aliases: new[]
            {
                "browser", "браузер", "браузера", "веб", "web",
                "brave", "брейв", "хром", "хрома", "chrome",
                "гугл", "гугл хром", "google chrome",
                "интернет", "сеть", "edge", "эдж", "firefox", "файрфокс", "опера", "opera", "яндекс", "yandex"
            },
            ProcessNames: new[] { "brave", "chrome", "msedge", "firefox", "opera", "browser" },
            CreateStartInfo: (args) => GetDefaultBrowserStartInfo(args)
        ),

        // 5. Notepad ("notepad", "блокнот")
        new AppDefinition(
            CanonicalId: "notepad",
            Aliases: new[] { "notepad", "блокнот", "блокнота" },
            ProcessNames: new[] { "notepad", "Notepad" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "notepad",
                Arguments = args,
                UseShellExecute = true
            }
        ),

        // 6. Calculator ("calc", "калькулятор")
        new AppDefinition(
            CanonicalId: "calc",
            Aliases: new[] { "calc", "калькулятор", "калькулятора" },
            ProcessNames: new[] { "CalculatorApp", "Calculator", "calc" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "calc",
                Arguments = args,
                UseShellExecute = true
            }
        ),

        // 7. Explorer ("explorer", "проводник")
        new AppDefinition(
            CanonicalId: "explorer",
            Aliases: new[] { "explorer", "проводник", "проводника", "файлы", "папка", "папку" },
            ProcessNames: new[] { "explorer" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "explorer",
                Arguments = args,
                UseShellExecute = true
            }
        )
    };

    public static bool IsKnownApp(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return false;
        string norm = nameOrAlias.Trim().ToLowerInvariant();
        if (norm.EndsWith(".exe")) norm = norm[..^4];
        return FindAppDefinition(norm) != null;
    }

    public static ProcessStartInfo GetDefaultBrowserStartInfo(string args = "")
    {
        string urlOrTarget = string.IsNullOrWhiteSpace(args) ? "https://" : args.Trim();

        // 1. Попытка получить зарегистрированный браузер по умолчанию из реестра Windows
        try
        {
            using var userChoiceKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string? progId = userChoiceKey?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
            {
                using var httpChoiceKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                progId = httpChoiceKey?.GetValue("ProgId") as string;
            }

            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                string? rawCmd = cmdKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(rawCmd))
                {
                    string exePath = rawCmd;
                    if (rawCmd.StartsWith('"'))
                    {
                        int nextQuote = rawCmd.IndexOf('"', 1);
                        if (nextQuote > 1)
                        {
                            exePath = rawCmd.Substring(1, nextQuote - 1);
                        }
                    }
                    else
                    {
                        int space = rawCmd.IndexOf(' ');
                        if (space > 0)
                        {
                            exePath = rawCmd[..space];
                        }
                    }

                    if (File.Exists(exePath))
                    {
                        return new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = string.IsNullOrWhiteSpace(args) ? string.Empty : args,
                            UseShellExecute = true
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppHandler] Ошибка чтения браузера по умолчанию из реестра: {ex.Message}");
        }

        // 2. Универсальный запуск браузера по умолчанию через shell cmd start без отображения консольного окна
        return new ProcessStartInfo("cmd", $"/c start {urlOrTarget}")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
    }

    public async Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "start";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var actionProp))
        {
            action = actionProp.GetString()?.ToLowerInvariant() ?? "start";
        }

        switch (action)
        {
            case "start":
                return await StartApplicationAsync(args, cancellationToken);

            case "install":
                return await InstallApplicationAsync(args, cancellationToken);

            case "uninstall":
                return await UninstallApplicationAsync(args, cancellationToken);

            case "close":
            case "stop":
            case "kill":
                return await CloseApplicationAsync(args, cancellationToken);

            case "status":
                return CheckApplicationStatus(args);

            default:
                return CommandResult.Fail($"Unsupported action '{action}' for app command. Available: start, install, uninstall, close, status.");
        }
    }

    private async Task<CommandResult> UninstallApplicationAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string? target = null;
        if (args.TryGetProperty("name", out var nameProp))
        {
            target = nameProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Fail("Action 'uninstall' requires 'name' property.");
        }

        var installedGame = Gem.Services.SteamService.FindInstalledGame(target, initiateConfirmation: true);
        if (installedGame == null)
        {
            return CommandResult.Fail($"Установленная игра '{target}' не найдена, сэр.");
        }

        Gem.Services.JarvisOrchestrator.Instance.SetPendingConfirmation(Gem.Services.PendingGameAction.Uninstall, installedGame);
        if (VoiceFeedback != null)
        {
            await VoiceFeedback.SpeakAsync($"Вы действительно хотите удалить {installedGame.Title}, сэр?", cancellationToken);
        }

        return CommandResult.Ok($"Запрошено подтверждение на удаление игры '{installedGame.Title}'.", new { Pending = true, Game = installedGame.Title, AppId = installedGame.AppId });
    }

    private async Task<CommandResult> InstallApplicationAsync(JsonElement args, CancellationToken cancellationToken)
    {
        string? target = null;
        if (args.TryGetProperty("name", out var nameProp))
        {
            target = nameProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Fail("Action 'install' requires 'name' property.");
        }

        var searchResult = Gem.Services.SteamService.Instance.SearchGame(target);
        if (searchResult != null && searchResult.RequiresConfirmation && searchResult.Game != null)
        {
            Gem.Services.JarvisOrchestrator.Instance.SetPendingConfirmation(Gem.Services.PendingGameAction.Install, searchResult.Game);
            if (VoiceFeedback != null)
            {
                await VoiceFeedback.SpeakAsync($"Вы имели в виду {searchResult.Game.Name}, сэр?", cancellationToken);
            }
            return CommandResult.Ok($"Запрошено подтверждение для установки '{searchResult.Game.Name}'.", new { Pending = true, Game = searchResult.Game.Name });
        }

        string resolvedTitle = target;
        bool ok = await Gem.Services.SteamService.Instance.InstallGameAsync(target, title =>
        {
            resolvedTitle = title;
        });

        return ok
            ? CommandResult.Ok($"Installation initiated for '{resolvedTitle}'.", new { Game = resolvedTitle })
            : CommandResult.Fail($"Could not find Steam game '{target}' for installation.");
    }

    private async Task<CommandResult> StartApplicationAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string? target = null;
        if (args.TryGetProperty("name", out var nameProp))
        {
            target = nameProp.GetString();
        }
        else if (args.TryGetProperty("path", out var pathProp))
        {
            target = pathProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Fail("Action 'start' requires 'name' or 'path' property.");
        }

        string arguments = string.Empty;
        if (args.TryGetProperty("arguments", out var argumentsProp) && argumentsProp.ValueKind == JsonValueKind.String)
        {
            arguments = argumentsProp.GetString() ?? string.Empty;
        }

        string normalizedTarget = target.Trim().ToLowerInvariant();

        // 0. МЕДИА-АЛИАСЫ: если LLM сгенерировал app/start/музыка (или аналог),
        // делегируем напрямую в MediaKeyService.PlayPause() без вызова Process.Start.
        // Это предотвращает Win32Exception: «Не удается найти указанный файл».
        if (_mediaAliases.Contains(normalizedTarget))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[AppHandler] Медиа-алиас '{normalizedTarget}' -> делегируем в MediaKeyService.PlayPause()");
            Console.ResetColor();
            bool sent = Gem.Services.MediaKeyService.PlayPause();
            return sent
                ? CommandResult.Ok("Переключаю воспроизведение.", new { MediaAction = "PlayPause", Alias = normalizedTarget })
                : CommandResult.Fail("Не удалось отправить MediaKey PlayPause.");
        }

        // 1. ИЗОЛЯЦИЯ: Сначала проверяем системные и зарегистрированные приложения (браузер, блокнот, калькулятор, проводник, discord, telegram, steam).
        // Если команда — запуск системной программы, поиск по библиотеке Steam запускаться НЕ ДОЛЖЕН.
        var matchedApp = FindAppDefinition(normalizedTarget);

        if (matchedApp != null)
        {
            try
            {
                ProcessStartInfo startInfo = matchedApp.CreateStartInfo(arguments);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[AppHandler] Запуск системного приложения: FileName='{startInfo.FileName}', Args='{startInfo.Arguments}'");
                Console.ResetColor();

                var process = Process.Start(startInfo);
                string appLabel = matchedApp.CanonicalId;

                return CommandResult.Ok(
                    $"Application '{appLabel}' started successfully.",
                    new
                    {
                        Target = target,
                        ResolvedApp = appLabel,
                        Executable = startInfo.FileName,
                        ProcessId = process?.Id,
                        ProcessName = process?.ProcessName
                    }
                );
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[AppHandler Error] Не удалось запустить системное приложение '{target}': {ex.Message}");
                Console.ResetColor();

                return CommandResult.Fail($"Ошибка запуска приложения '{target}': {ex.Message}");
            }
        }

        // 2. Проверяем прямой путь к исполняемому файлу
        if (File.Exists(target) || target.Contains('\\') || target.Contains('/'))
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = target,
                    Arguments = arguments,
                    UseShellExecute = true
                };

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[AppHandler] Запуск по прямому пути: FileName='{startInfo.FileName}', Args='{startInfo.Arguments}'");
                Console.ResetColor();

                var process = Process.Start(startInfo);
                return CommandResult.Ok(
                    $"Executable '{target}' started successfully.",
                    new { Target = target, ProcessId = process?.Id }
                );
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Ошибка запуска файла '{target}': {ex.Message}");
            }
        }

        // 3. Только если цель НЕ является системным приложением и НЕ является прямым путем — проверяем игры Steam
        var searchResult = Gem.Services.SteamService.Instance.SearchGame(normalizedTarget);
        if (searchResult != null && searchResult.RequiresConfirmation && searchResult.Game != null)
        {
            Gem.Services.JarvisOrchestrator.Instance.SetPendingConfirmation(Gem.Services.PendingGameAction.Start, searchResult.Game);
            if (VoiceFeedback != null)
            {
                await VoiceFeedback.SpeakAsync($"Вы имели в виду {searchResult.Game.Name}, сэр?", cancellationToken);
            }
            return CommandResult.Ok($"Запрошено подтверждение для запуска '{searchResult.Game.Name}'.", new { Pending = true, Game = searchResult.Game.Name });
        }

        // Проверяем игры Steam через SteamService
        if (Gem.Services.SteamService.Instance.LaunchGame(normalizedTarget, out string gameTitle))
        {
            return CommandResult.Ok($"Steam game '{gameTitle}' started successfully.", new { Target = target, App = gameTitle });
        }

        // Проверяем игры Steam через AppHandler
        if (Gem.Services.AppHandler.SteamGames.ContainsKey(normalizedTarget))
        {
            var appHandler = new Gem.Services.AppHandler();
            bool ok = appHandler.Handle("start", normalizedTarget);
            return ok
                ? CommandResult.Ok($"Steam game '{normalizedTarget}' started successfully.", new { Target = target, App = normalizedTarget })
                : CommandResult.Fail($"Failed to launch Steam game '{normalizedTarget}'.");
        }

        // 4. Попытка стандартного запуска произвольной команды через системный шелл
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                Arguments = arguments,
                UseShellExecute = true
            };

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[AppHandler] Запуск процесса через Shell: FileName='{startInfo.FileName}', Args='{startInfo.Arguments}'");
            Console.ResetColor();

            var process = Process.Start(startInfo);
            return CommandResult.Ok(
                $"Application '{target}' started successfully.",
                new
                {
                    Target = target,
                    ResolvedApp = target,
                    Executable = startInfo.FileName,
                    ProcessId = process?.Id,
                    ProcessName = process?.ProcessName
                }
            );
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[AppHandler Error] Не удалось запустить приложение '{target}': {ex.Message}");
            Console.ResetColor();

            return CommandResult.Fail($"Ошибка запуска приложения '{target}': {ex.Message}");
        }
    }

    private static async Task<CommandResult> CloseApplicationAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return CommandResult.Fail("Action 'close' requires 'name' property (e.g. 'notepad', 'discord', 'steam').");
        }

        string rawName = nameProp.GetString()!;
        string normalizedName = rawName.Trim().ToLowerInvariant();
        if (normalizedName.EndsWith(".exe"))
        {
            normalizedName = normalizedName[..^4];
        }

        // Self-shutdown: завершаем текущий процесс ассистента через AppHandler с блокирующим голосовым прощанием
        if (Gem.Services.AppHandler.IsExitTarget(normalizedName))
        {
            string? replyText = null;
            if (args.TryGetProperty("reply", out var replyProp) && replyProp.ValueKind == JsonValueKind.String)
            {
                replyText = replyProp.GetString();
            }

            var appHandler = new Gem.Services.AppHandler();
            appHandler.Handle("close", normalizedName, replyText);
            return CommandResult.Ok("Завершение работы.");
        }

        // 1. ИЗОЛЯЦИЯ: Сначала проверяем системные приложения
        var matchedApp = FindAppDefinition(normalizedName);
        string[] processCandidateNames;
        if (matchedApp != null)
        {
            processCandidateNames = matchedApp.ProcessNames;
        }
        // 2. Проверяем игры Steam через AppHandler
        else if (Gem.Services.AppHandler.SteamProcessNames.ContainsKey(normalizedName) ||
                 Gem.Services.AppHandler.SteamGames.ContainsKey(normalizedName) ||
                 normalizedName.Contains("monsterhunter", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("mhw", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("wilds", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("world", StringComparison.OrdinalIgnoreCase))
        {
            var appHandler = new Gem.Services.AppHandler();
            bool ok = appHandler.Handle("close", normalizedName);
            return ok
                ? CommandResult.Ok($"Игра '{rawName}' успешно закрыта.", new { Target = rawName, App = normalizedName })
                : CommandResult.Fail($"Процессы для '{rawName}' не найдены для закрытия.");
        }
        else
        {
            processCandidateNames = new[] { normalizedName };
        }

        bool force = false;
        if (args.TryGetProperty("force", out var forceProp) &&
            (forceProp.ValueKind == JsonValueKind.True || forceProp.ValueKind == JsonValueKind.False))
        {
            force = forceProp.GetBoolean();
        }

        var processes = new List<Process>();
        foreach (var procName in processCandidateNames)
        {
            processes.AddRange(Process.GetProcessesByName(procName));
        }

        if (processes.Count == 0)
        {
            string display = matchedApp?.CanonicalId ?? normalizedName;
            return CommandResult.Fail($"No running processes found matching '{display}'.");
        }

        var closedPids = new List<int>();
        var killedPids = new List<int>();

        foreach (var proc in processes)
        {
            try
            {
                int pid = proc.Id;
                if (!force && proc.MainWindowHandle != IntPtr.Zero)
                {
                    // Attempt graceful window close
                    proc.CloseMainWindow();
                    bool exited = false;

                    for (int i = 0; i < 15; i++)
                    {
                        if (proc.HasExited)
                        {
                            exited = true;
                            break;
                        }
                        await Task.Delay(100, cancellationToken);
                    }

                    if (exited)
                    {
                        closedPids.Add(pid);
                        continue;
                    }
                }

                // Force kill if graceful close wasn't requested or didn't complete
                proc.Kill(entireProcessTree: true);
                killedPids.Add(pid);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[AppHandler Warning] Ошибка закрытия процесса PID {proc.Id}: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                proc.Dispose();
            }
        }

        string appLabel = matchedApp?.CanonicalId ?? normalizedName;
        return CommandResult.Ok(
            $"Processed {processes.Count} process(es) for '{appLabel}'. Gracefully closed: {closedPids.Count}, Force-killed: {killedPids.Count}.",
            new
            {
                Application = appLabel,
                TotalFound = processes.Count,
                GracefullyClosed = closedPids,
                ForceKilled = killedPids
            }
        );
    }

    private static CommandResult CheckApplicationStatus(JsonElement args)
    {
        if (!args.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return CommandResult.Fail("Action 'status' requires 'name' property.");
        }

        string rawName = nameProp.GetString()!;
        string normalizedName = rawName.Trim().ToLowerInvariant();
        if (normalizedName.EndsWith(".exe"))
        {
            normalizedName = normalizedName[..^4];
        }

        var matchedApp = FindAppDefinition(normalizedName);
        string[] processCandidateNames = matchedApp != null
            ? matchedApp.ProcessNames
            : new[] { normalizedName };

        var processes = new List<Process>();
        foreach (var procName in processCandidateNames)
        {
            processes.AddRange(Process.GetProcessesByName(procName));
        }

        var pids = processes.Select(p => p.Id).ToList();
        foreach (var p in processes)
        {
            p.Dispose();
        }

        string appLabel = matchedApp?.CanonicalId ?? normalizedName;
        return CommandResult.Ok(
            $"Application '{appLabel}' running status: {(processes.Count > 0 ? "Running" : "Not running")}.",
            new
            {
                Application = appLabel,
                IsRunning = processes.Count > 0,
                InstanceCount = processes.Count,
                ProcessIds = pids
            }
        );
    }

    private static AppDefinition? FindAppDefinition(string nameOrAlias)
    {
        return KnownApps.FirstOrDefault(app =>
            app.CanonicalId.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase) ||
            app.Aliases.Any(alias => alias.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase))
        );
    }
}
