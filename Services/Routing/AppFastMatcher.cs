using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Services.Routing;

/// <summary>
/// Fast-path recognition for app and game lifecycle commands (install, uninstall, launch, close).
/// </summary>
public static class AppFastMatcher
{
    public static JarvisResponse? MatchUninstall(string spokenText)
    {
        var uninstallMatch = Regex.Match(spokenText.Trim(), @"^(?:удали|деинсталлируй|сноси)\s+(?:игру\s+)?(.+)$", RegexOptions.IgnoreCase);
        if (uninstallMatch.Success)
        {
            string gameQuery = uninstallMatch.Groups[1].Value.Trim().TrimEnd('.', '!', '?', ',');
            if (!string.IsNullOrWhiteSpace(gameQuery))
            {
                var installedGame = SteamService.FindInstalledGame(gameQuery, initiateConfirmation: true);
                if (installedGame == null)
                {
                    return new JarvisResponse(null, $"Установленная игра '{gameQuery}' не найдена, сэр.");
                }

                // Деинсталляция ВСЕГДА требует обязательного голосового подтверждения через PendingActionState
                JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Uninstall, installedGame);
                try
                {
                    Gem.Voice.VoiceListener.Instance?.EnterConfirmationListening();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VoiceListener] Не удалось переключить режим ожидания подтверждения: {ex.Message}");
                }

                return new JarvisResponse(null, $"Вы действительно хотите удалить {installedGame.Title}, сэр?");
            }
        }

        return null;
    }

    public static JarvisResponse? MatchInstall(string lower)
    {
        if (lower.StartsWith("установи ") || lower.StartsWith("скачай ") || lower.StartsWith("поставь ") || lower.StartsWith("install "))
        {
            string gameName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                var search = SteamService.Instance.SearchGame(gameName);
                if (search != null && search.RequiresConfirmation && search.Game != null)
                {
                    JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Install, search.Game);
                    return new JarvisResponse(null, $"Вы имели в виду {search.Game.Name}, сэр?");
                }

                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "install", "name": {{JsonSerializer.Serialize(gameName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Инициирую установку, сэр.");
            }
        }

        return null;
    }

    public static JarvisResponse? MatchApps(string lower)
    {
        bool isCloseAction = lower.Contains("закрой") || lower.Contains("выключи") || lower.Contains("убей") ||
                             lower.Contains("заверши") || lower.Contains("close") || lower.Contains("kill");

        // Self-shutdown: "закройся", "закрывайся", "выключись", "стоп приложение", "завершись", "отключись", "выход"
        if (lower == "закройся" || lower == "закрывайся" || lower == "выключись" || lower == "стоп приложение" || lower == "завершись" || lower == "отключись" ||
            lower == "выход" || (isCloseAction && (lower.Contains("джарвис") || lower.Contains("jarvis") || lower.Contains("ассистент") || lower.Contains("себя"))))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "jarvis" }""");
            return new JarvisResponse(new CommandRequest("system", args), "Завершаю работу. До свидания, сэр.");
        }

        // Monster Hunter Wilds
        if (lower.Contains("вайлд") || lower.Contains("уайлд") || lower.Contains("wilds"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "mhw_wilds" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Monster Hunter Wilds, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "mhw_wilds" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Monster Hunter Wilds, сэр.");
            }
        }

        // Monster Hunter World
        if (lower.Contains("ворлд") || lower.Contains("world") || lower.Contains("монстер хантер") || lower.Contains("mhw"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "mhw_world" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Monster Hunter World, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "mhw_world" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Monster Hunter World, сэр.");
            }
        }

        // Steam ("steam", "tim", "стим")
        if (lower.Contains("стим") || lower.Contains("steam") || lower.Contains(" tim") || lower.StartsWith("tim") || lower.Contains(" тим") || lower.StartsWith("тим"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "steam", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Steam, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "steam" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Steam, сэр.");
            }
        }

        // Discord ("discord", "дискорд", "диск")
        if (lower.Contains("дискорд") || lower.Contains("discord") || lower.Contains("диск"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "discord", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Discord, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "discord" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Discord, сэр.");
            }
        }

        // Telegram ("telegram", "телега", "телеграм")
        if (lower.Contains("телеграм") || lower.Contains("телега") || lower.Contains("telegram") || lower.Contains("телегу"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "telegram", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю Telegram, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "telegram" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю Telegram, сэр.");
            }
        }

        // Browser ("browser", "браузер", "brave", "хром", "chrome", etc.)
        if (lower.Contains("браузер") || lower.Contains("хром") || lower.Contains("chrome") || lower.Contains("брейв") || lower.Contains("brave") || lower.Contains("browser"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "browser", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю браузер, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "browser" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю браузер, сэр.");
            }
        }

        // Notepad ("notepad", "блокнот")
        if (lower.Contains("блокнот") || lower.Contains("notepad"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "notepad", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю блокнот, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "notepad" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю блокнот, сэр.");
            }
        }

        // Calc ("calc", "калькулятор")
        if (lower.Contains("калькулятор") || lower.Contains("calc"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "calc", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю калькулятор, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "calc" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю калькулятор, сэр.");
            }
        }

        if (lower.Contains("проводник") || lower.Contains("explorer"))
        {
            if (isCloseAction)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "close", "name": "explorer", "force": false }""");
                return new JarvisResponse(new CommandRequest("app", args), "Закрываю проводник, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "start", "name": "explorer" }""");
                return new JarvisResponse(new CommandRequest("app", args), "Открываю проводник, сэр.");
            }
        }

        // Launch generic Steam game commands ("запусти ...", "включи ...", "открой ...")
        if (lower.StartsWith("запусти ") || lower.StartsWith("включи ") || lower.StartsWith("открой "))
        {
            string candidate = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                bool isMediaOrService = candidate.Contains("видео") || candidate.Contains("ролик") ||
                                        candidate.Contains("фильм") || candidate.Contains("сериал") ||
                                        candidate.Contains("музык") || candidate.Contains("трек") ||
                                        candidate.Contains("песн") || SteamService.IsServiceEntry(candidate);

                if (!isMediaOrService)
                {
                    var search = SteamService.Instance.SearchGame(candidate);
                    if (search != null && search.RequiresConfirmation && search.Game != null)
                    {
                        JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Start, search.Game);
                        return new JarvisResponse(null, $"Вы имели в виду {search.Game.Name}, сэр?");
                    }

                    if (search != null && search.Game != null)
                    {
                        var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "start", "name": {{JsonSerializer.Serialize(candidate)}} }""");
                        return new JarvisResponse(new CommandRequest("app", args), "Запускаю игру, сэр.");
                    }
                }
            }
        }

        return null;
    }
}
