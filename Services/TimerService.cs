using System.Collections.Concurrent;

namespace Gem.Services;

/// <summary>
/// Service managing local countdown timers and reminders with voice alerts.
/// </summary>
public sealed class TimerService : ITimerService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ActiveTimer> _timers = new();
    private readonly IVoiceFeedbackService? _voiceFeedback;
    private bool _disposed = false;

    /// <summary>
    /// Global singleton accessor for the active TimerService.
    /// </summary>
    public static TimerService? Instance { get; set; }

    /// <summary>
    /// Event triggered when a timer completes its countdown.
    /// </summary>
    public event Action<ActiveTimer>? OnTimerElapsed;

    public TimerService(IVoiceFeedbackService? voiceFeedback = null)
    {
        _voiceFeedback = voiceFeedback;
        Instance = this;
    }

    /// <summary>
    /// Sets and starts a new countdown timer.
    /// </summary>
    /// <param name="duration">Timer duration.</param>
    /// <param name="label">Timer label or reminder note.</param>
    /// <returns>Unique identifier for the created timer.</returns>
    public Guid SetTimer(TimeSpan duration, string label = "таймер")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string effectiveLabel = string.IsNullOrWhiteSpace(label) ? "таймер" : label.Trim();
        DateTime now = DateTime.UtcNow;

        var timer = new ActiveTimer
        {
            Id = Guid.NewGuid(),
            Label = effectiveLabel,
            Duration = duration,
            CreatedAt = now,
            TriggerAt = now + duration,
            Cts = new CancellationTokenSource()
        };

        _timers[timer.Id] = timer;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TimerService] Установлен таймер '{timer.Label}' на {duration.TotalSeconds:0.#} сек. (ID: {timer.Id})");
        Console.ResetColor();

        // Background countdown worker
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(duration, timer.Cts.Token);

                // Remove once delay completes
                _timers.TryRemove(timer.Id, out _);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[TimerService] Таймер '{timer.Label}' завершился! (ID: {timer.Id})");
                Console.ResetColor();

                // Fire event for listeners/tests
                try
                {
                    OnTimerElapsed?.Invoke(timer);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TimerService] Ошибка в обработчике события OnTimerElapsed: {ex.Message}");
                    Console.ResetColor();
                }

                // Voice feedback alert
                string message = timer.Label.Equals("таймер", StringComparison.OrdinalIgnoreCase)
                    ? "Сэр, время таймера вышло."
                    : $"Сэр, время таймера '{timer.Label}' вышло.";

                var feedback = _voiceFeedback ?? VoiceFeedbackService.Instance;
                if (feedback != null)
                {
                    await feedback.SpeakAsync(message);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected normal cancellation - do not log false errors
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[TimerService] Таймер '{timer.Label}' штатно отменен.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[TimerService] Ошибка исполнения таймера '{timer.Label}': {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                _timers.TryRemove(timer.Id, out _);
                try
                {
                    timer.Cts.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimerService Warning] Ошибка Dispose Cts: {ex.Message}");
                }
            }
        }, timer.Cts.Token);

        return timer.Id;
    }

    /// <summary>
    /// Gets a snapshot of currently active timers ordered by scheduled trigger time.
    /// </summary>
    public IReadOnlyList<ActiveTimer> GetActiveTimers()
    {
        return _timers.Values
            .Where(t => !t.IsExpired && !t.Cts.IsCancellationRequested)
            .OrderBy(t => t.TriggerAt)
            .ToList();
    }

    /// <summary>
    /// Cancels a specific timer or all active timers if no id is given.
    /// </summary>
    /// <param name="id">Optional specific timer id.</param>
    /// <returns>True if at least one timer was cancelled; otherwise false.</returns>
    public bool CancelTimer(Guid? id = null)
    {
        if (id.HasValue)
        {
            if (_timers.TryRemove(id.Value, out var timer))
            {
                try
                {
                    timer.Cts.Cancel();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimerService Warning] Ошибка отмены Cts: {ex.Message}");
                }
                return true;
            }
            return false;
        }

        if (_timers.IsEmpty)
        {
            return false;
        }

        bool cancelledAny = false;
        foreach (var kvp in _timers)
        {
            if (_timers.TryRemove(kvp.Key, out var timer))
            {
                try
                {
                    timer.Cts.Cancel();
                    cancelledAny = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimerService Warning] Ошибка отмены Cts: {ex.Message}");
                }
            }
        }

        return cancelledAny;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Instance == this)
        {
            Instance = null;
        }

        foreach (var kvp in _timers)
        {
            if (_timers.TryRemove(kvp.Key, out var timer))
            {
                try
                {
                    timer.Cts.Cancel();
                    timer.Cts.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimerService Warning] Ошибка очистки Cts при Dispose: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Formats TimeSpan duration into natural Russian words.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        int totalSec = (int)Math.Ceiling(duration.TotalSeconds);
        if (totalSec % 3600 == 0)
        {
            int h = totalSec / 3600;
            return h == 1 ? "1 час" : $"{h} {GetPlural(h, "час", "часа", "часов")}";
        }

        if (totalSec >= 3600)
        {
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            var parts = new List<string> { $"{h} {GetPlural(h, "час", "часа", "часов")}" };
            if (m > 0) parts.Add($"{m} {GetPlural(m, "минуту", "минуты", "минут")}");
            if (s > 0) parts.Add($"{s} {GetPlural(s, "секунду", "секунды", "секунд")}");
            return string.Join(" ", parts);
        }

        if (totalSec % 60 == 0)
        {
            int m = totalSec / 60;
            return $"{m} {GetPlural(m, "минуту", "минуты", "минут")}";
        }

        if (totalSec > 60)
        {
            int m = totalSec / 60;
            int s = totalSec % 60;
            return $"{m} {GetPlural(m, "минуту", "минуты", "минут")} {s} {GetPlural(s, "секунду", "секунды", "секунд")}";
        }

        return $"{totalSec} {GetPlural(totalSec, "секунду", "секунды", "секунд")}";
    }

    private static string GetPlural(int number, string one, string twoToFour, string fiveAndMore)
    {
        int n = Math.Abs(number) % 100;
        int n1 = n % 10;
        if (n > 10 && n < 20) return fiveAndMore;
        if (n1 > 1 && n1 < 5) return twoToFour;
        if (n1 == 1) return one;
        return fiveAndMore;
    }
}
