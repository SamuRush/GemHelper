using System.Text.Json;
using Gem.Core;
using Gem.Services.Routing;

namespace Gem.Services;

public sealed partial class LlmIntentService
{
    /// <summary>
    /// Fast-path heuristic matcher for unambiguous deterministic commands.
    /// Handles media, timers, volume, apps, and standard hotkeys with zero latency.
    /// </summary>
    public static JarvisResponse? TryFastMatch(string spokenText)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            return null;
        }

        string lower = spokenText.ToLowerInvariant().Trim();

        // 0. Pending confirmation reply fast-match
        if (JarvisOrchestrator.Instance.HasPendingAction)
        {
            if (JarvisOrchestrator.IsAffirmativeReply(lower))
            {
                var action = JarvisOrchestrator.Instance.PendingAction;
                var game = JarvisOrchestrator.Instance.PendingGame!;
                JarvisOrchestrator.Instance.ResetConfirmationState();

                if (action == PendingGameAction.Uninstall)
                {
                    _ = Task.Run(async () =>
                    {
                        if (uint.TryParse(game.AppId, out uint numId))
                        {
                            await SteamService.Instance.UninstallGameAsync(numId, game.Title);
                        }
                    });
                    return new JarvisResponse(null, $"Удаляю {game.Name}, сэр.");
                }
                else if (action == PendingGameAction.Install)
                {
                    _ = Task.Run(async () => await SteamService.Instance.InstallGameAsync(game));
                    return new JarvisResponse(null, $"Принято. Начинаю установку {game.Name}, сэр.");
                }
                else
                {
                    SteamService.Instance.LaunchGame(game.AppId, out _);
                    return new JarvisResponse(null, $"Запускаю {game.Name}, сэр.");
                }
            }

            if (JarvisOrchestrator.IsNegativeReply(lower))
            {
                var action = JarvisOrchestrator.Instance.PendingAction;
                JarvisOrchestrator.Instance.ResetConfirmationState();
                string reply = action == PendingGameAction.Uninstall ? "Удаление отменено, сэр." : "Понял, отменяю.";
                return new JarvisResponse(null, reply);
            }

            JarvisOrchestrator.Instance.ResetConfirmationState();
        }

        // 1. Media Playback & Track Controls
        var mediaMatch = MediaFastMatcher.MatchMediaCommand(lower);
        if (mediaMatch != null) return mediaMatch;

        // 2. Timer & Reminders
        var timerMatch = TimerFastMatcher.MatchTimerCommand(lower);
        if (timerMatch != null) return timerMatch;

        // 3. Volume commands
        var volumeMatch = VolumeFastMatcher.MatchVolumeCommand(lower);
        if (volumeMatch != null) return volumeMatch;

        // 4. Weather commands
        var weatherMatch = SystemFastMatcher.MatchWeather(lower);
        if (weatherMatch != null) return weatherMatch;

        // 5. Uninstallation commands
        var uninstallMatch = AppFastMatcher.MatchUninstall(spokenText);
        if (uninstallMatch != null) return uninstallMatch;

        // 6. Installation commands
        var installMatch = AppFastMatcher.MatchInstall(lower);
        if (installMatch != null) return installMatch;

        // 7. Apps & System Applications
        var appMatch = AppFastMatcher.MatchApps(lower);
        if (appMatch != null) return appMatch;

        // 8. Hotkeys & System shortcuts
        var hotkeyMatch = SystemFastMatcher.MatchHotkeys(lower);
        if (hotkeyMatch != null) return hotkeyMatch;

        return null;
    }

    public static JarvisResponse? MatchMediaCommand(string lower) => MediaFastMatcher.MatchMediaCommand(lower);
    public static JarvisResponse? MatchTimerCommand(string lower) => TimerFastMatcher.MatchTimerCommand(lower);
    public static bool TryParseTimerSetCommand(string text, out TimeSpan duration, out string label, out string timeDisplay) =>
        TimerFastMatcher.TryParseTimerSetCommand(text, out duration, out label, out timeDisplay);
    public static int? ParseVolumeNumber(string text) => VolumeFastMatcher.ParseVolumeNumber(text);

    /// <summary>
    /// Offline heuristic fallback so Jarvis can execute common commands without an active LLM key.
    /// </summary>
    public static JarvisResponse OfflineFallbackInterpreter(string spokenText)
    {
        var fast = TryFastMatch(spokenText);
        if (fast != null)
        {
            return fast;
        }

        string lower = spokenText.ToLowerInvariant();
        var weather = SystemFastMatcher.MatchOfflineWeather(lower);
        if (weather != null) return weather;

        if (lower.StartsWith("установи ") || lower.StartsWith("скачай ") || lower.StartsWith("поставь ") || lower.StartsWith("install "))
        {
            string gameName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "install", "name": {{JsonSerializer.Serialize(gameName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Инициирую установку, сэр.");
            }
        }

        if (lower.StartsWith("запусти ") || lower.StartsWith("включи ") || lower.StartsWith("открой "))
        {
            string appName = lower.Substring(lower.IndexOf(' ') + 1).Trim();
            if (!string.IsNullOrWhiteSpace(appName))
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "start", "name": {{JsonSerializer.Serialize(appName)}} }""");
                return new JarvisResponse(new CommandRequest("app", args), "Запускаю игру, сэр.");
            }
        }

        return new JarvisResponse(null, "Вас понял, сэр.");
    }
}
