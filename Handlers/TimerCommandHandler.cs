using System.Text.Json;
using Gem.Core;
using Gem.Services;

namespace Gem.Handlers;

/// <summary>
/// Command handler for timer and reminder actions ("set", "cancel", "status").
/// </summary>
public sealed class TimerCommandHandler : ICommandHandler
{
    public string CommandName => "timer";

    private readonly ITimerService _timerService;

    public TimerCommandHandler(ITimerService? timerService = null)
    {
        _timerService = timerService ?? TimerService.Instance ?? new TimerService();
    }

    public Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var actProp))
        {
            action = actProp.GetString()?.ToLowerInvariant() ?? "status";
        }

        switch (action)
        {
            case "set":
            {
                int seconds = 0;
                if (args.TryGetProperty("seconds", out var secProp))
                {
                    if (secProp.ValueKind == JsonValueKind.Number && secProp.TryGetInt32(out int s))
                    {
                        seconds = s;
                    }
                    else if (secProp.ValueKind == JsonValueKind.String && int.TryParse(secProp.GetString(), out int parsedS))
                    {
                        seconds = parsedS;
                    }
                }

                if (seconds <= 0)
                {
                    return Task.FromResult(CommandResult.Fail("Параметр 'seconds' должен быть больше 0."));
                }

                string label = "таймер";
                if (args.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                {
                    string? l = labelProp.GetString();
                    if (!string.IsNullOrWhiteSpace(l))
                    {
                        label = l.Trim();
                    }
                }

                var duration = TimeSpan.FromSeconds(seconds);
                var id = _timerService.SetTimer(duration, label);

                string formattedDuration = FormatDuration(duration);
                return Task.FromResult(CommandResult.Ok(
                    $"Таймер на {formattedDuration} установлен, сэр.",
                    new { id, seconds, label, duration = formattedDuration }
                ));
            }

            case "cancel":
            case "stop":
            case "reset":
            {
                Guid? timerId = null;
                if (args.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    if (Guid.TryParse(idProp.GetString(), out var parsedId))
                    {
                        timerId = parsedId;
                    }
                }

                bool cancelled = _timerService.CancelTimer(timerId);
                if (cancelled)
                {
                    return Task.FromResult(CommandResult.Ok("Таймер отменен, сэр.", new { cancelled = true }));
                }

                return Task.FromResult(CommandResult.Ok("Нет активных таймеров для отмены, сэр.", new { cancelled = false }));
            }

            case "status":
            case "get":
            default:
            {
                var timers = _timerService.GetActiveTimers();
                if (timers.Count == 0)
                {
                    return Task.FromResult(CommandResult.Ok("Нет активных таймеров, сэр.", new { count = 0 }));
                }

                var nextTimer = timers[0];
                var remaining = nextTimer.Remaining;
                int totalSec = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
                int minutes = totalSec / 60;
                int seconds = totalSec % 60;

                string statusMessage = $"Осталось {minutes} минут {seconds} секунд, сэр.";
                return Task.FromResult(CommandResult.Ok(
                    statusMessage,
                    new
                    {
                        count = timers.Count,
                        remainingSeconds = totalSec,
                        minutes,
                        seconds,
                        label = nextTimer.Label,
                        timers = timers.Select(t => new { t.Id, t.Label, t.Remaining.TotalSeconds }).ToList()
                    }
                ));
            }
        }
    }

    /// <summary>
    /// Formats TimeSpan duration into natural Russian words.
    /// </summary>
    public static string FormatDuration(TimeSpan duration) => TimerService.FormatDuration(duration);
}
