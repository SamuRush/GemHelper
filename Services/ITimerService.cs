namespace Gem.Services;

/// <summary>
/// Contract for managing countdown timers and reminders.
/// </summary>
public interface ITimerService
{
    /// <summary>
    /// Event triggered when any active timer completes its countdown.
    /// </summary>
    event Action<ActiveTimer>? OnTimerElapsed;

    /// <summary>
    /// Sets and starts a new timer with the specified duration and optional label.
    /// </summary>
    /// <param name="duration">Countdown duration.</param>
    /// <param name="label">Optional description or note (e.g. "выключить плиту").</param>
    /// <returns>Unique identifier of the created timer.</returns>
    Guid SetTimer(TimeSpan duration, string label = "таймер");

    /// <summary>
    /// Gets a snapshot of currently running timers ordered by earliest trigger time.
    /// </summary>
    IReadOnlyList<ActiveTimer> GetActiveTimers();

    /// <summary>
    /// Cancels a specific timer by id, or cancels all active timers if id is null.
    /// </summary>
    /// <param name="id">Optional specific timer id.</param>
    /// <returns>True if at least one timer was cancelled; otherwise false.</returns>
    bool CancelTimer(Guid? id = null);
}
