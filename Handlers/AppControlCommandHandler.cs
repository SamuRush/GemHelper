using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Handlers.App;

namespace Gem.Handlers;

/// <summary>
/// Command handler for launching and terminating applications by name or path.
/// Includes smart alias mapping for popular apps and common Vosk phonetic misrecognitions
/// ("steam"/"tim"/"стим", "discord"/"диск", "telegram"/"телега", "chrome"/"хром"/"браузер", etc.).
/// </summary>
public sealed partial class AppControlCommandHandler : ICommandHandler
{
    public string CommandName => "app";

    private readonly Gem.Services.IVoiceFeedbackService? _voiceFeedback;
    private readonly Gem.Services.SteamService? _steamService;
    private readonly Gem.Services.JarvisOrchestrator? _orchestrator;
    private readonly Gem.Services.IMediaSessionService? _mediaSessionService;

    public AppControlCommandHandler(
        Gem.Services.IVoiceFeedbackService? voiceFeedback = null,
        Gem.Services.SteamService? steamService = null,
        Gem.Services.JarvisOrchestrator? orchestrator = null,
        Gem.Services.IMediaSessionService? mediaSessionService = null)
    {
        _voiceFeedback = voiceFeedback;
        _steamService = steamService;
        _orchestrator = orchestrator;
        _mediaSessionService = mediaSessionService;
    }

    private Gem.Services.IVoiceFeedbackService? VoiceFeedback => _voiceFeedback ?? Gem.Services.CompositeVoiceFeedbackService.Instance;
    private Gem.Services.SteamService Steam => _steamService ?? Gem.Services.SteamService.Instance;
    private Gem.Services.JarvisOrchestrator Orchestrator => _orchestrator ?? Gem.Services.JarvisOrchestrator.Instance;
    private Gem.Services.IMediaSessionService MediaSession => _mediaSessionService ?? Gem.Services.MediaSessionService.Instance;

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

    private static readonly HashSet<string> _videoAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "видео", "ролик", "ролики",
        "фильм", "фильмы",
        "сериал", "сериалы",
        "кино"
    };

    public static bool IsKnownApp(string nameOrAlias) => KnownAppRegistry.IsKnownApp(nameOrAlias);

    public static ProcessStartInfo GetDefaultBrowserStartInfo(string args = "") =>
        DefaultBrowserResolver.GetDefaultBrowserStartInfo(args);

    private static AppDefinition? FindAppDefinition(string nameOrAlias) =>
        KnownAppRegistry.FindAppDefinition(nameOrAlias);

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

        Orchestrator.SetPendingConfirmation(Gem.Services.PendingGameAction.Uninstall, installedGame);
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

        var searchResult = Steam.SearchGame(target);
        if (searchResult != null && searchResult.Game != null)
        {
            if (Steam.IsGameInstalled(searchResult.Game))
            {
                Orchestrator.SetPendingConfirmation(Gem.Services.PendingGameAction.Start, searchResult.Game);
                if (VoiceFeedback != null)
                {
                    await VoiceFeedback.SpeakAsync("Эта игра уже установлена, сэр. Запустить её?", cancellationToken);
                }
                return CommandResult.Ok("Эта игра уже установлена, сэр. Запустить её?", new { Game = searchResult.Game.Title, AppId = searchResult.Game.AppId, AlreadyInstalled = true, Pending = true });
            }

            if (searchResult.RequiresConfirmation)
            {
                Orchestrator.SetPendingConfirmation(Gem.Services.PendingGameAction.Install, searchResult.Game);
                if (VoiceFeedback != null)
                {
                    await VoiceFeedback.SpeakAsync($"Вы имели в виду {searchResult.Game.Name}, сэр?", cancellationToken);
                }
                return CommandResult.Ok($"Запрошено подтверждение для установки '{searchResult.Game.Name}'.", new { Pending = true, Game = searchResult.Game.Name });
            }
        }

        string resolvedTitle = target;
        bool ok = await Steam.InstallGameAsync(target, title =>
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

        // 0. ВИДЕО-АЛИАСЫ: если запрошен запуск видео / фильма / сериала
        if (_videoAliases.Contains(normalizedTarget) ||
            normalizedTarget.Contains("видео") || normalizedTarget.Contains("ролик") ||
            normalizedTarget.Contains("фильм") || normalizedTarget.Contains("сериал"))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[AppHandler] Видео-алиас '{normalizedTarget}' -> делегируем в MediaSession.ControlVideoAsync(pause: false)");
            Console.ResetColor();
            bool sent = await MediaSession.ControlVideoAsync(pause: false, cancellationToken);
            return sent
                ? CommandResult.Ok("Включаю видео, сэр.", new { MediaAction = "ControlVideo", Alias = normalizedTarget })
                : CommandResult.Fail("Не удалось включить видео.");
        }

        // 0.1 МЕДИА-АЛИАСЫ: если запрошен запуск музыки или музыкального приложения,
        // делегируем в MediaSession.PlayMusicAsync вместо попытки Process.Start или слепого PlayPause.
        if (_mediaAliases.Contains(normalizedTarget) || normalizedTarget == "yandexmusic" || normalizedTarget == "yandex music")
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[AppHandler] Медиа-алиас '{normalizedTarget}' -> делегируем в MediaSession.PlayMusicAsync()");
            Console.ResetColor();
            bool sent = await MediaSession.PlayMusicAsync("yandexmusic", cancellationToken);
            return sent
                ? CommandResult.Ok("Запускаю воспроизведение музыки, сэр.", new { MediaAction = "PlayMusic", Alias = normalizedTarget })
                : CommandResult.Fail("Не удалось запустить воспроизведение музыки.");
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
        var searchResult = Steam.SearchGame(normalizedTarget);
        if (searchResult != null && searchResult.RequiresConfirmation && searchResult.Game != null)
        {
            Orchestrator.SetPendingConfirmation(Gem.Services.PendingGameAction.Start, searchResult.Game);
            if (VoiceFeedback != null)
            {
                await VoiceFeedback.SpeakAsync($"Вы имели в виду {searchResult.Game.Name}, сэр?", cancellationToken);
            }
            return CommandResult.Ok($"Запрошено подтверждение для запуска '{searchResult.Game.Name}'.", new { Pending = true, Game = searchResult.Game.Name });
        }

        // Проверяем игры Steam через SteamService
        if (Steam.LaunchGame(normalizedTarget, out string gameTitle))
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
}

