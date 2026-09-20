namespace Gem.Services;

/// <summary>
/// Represents an active countdown timer or reminder.
/// </summary>
public sealed class ActiveTimer
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Custom label or reminder note (e.g. "выключить плиту" or default "таймер").
    /// </summary>
    public string Label { get; init; } = "таймер";

    /// <summary>
    /// Total duration of the timer.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// UTC timestamp when the timer was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when the timer is scheduled to expire.
    /// </summary>
    public DateTime TriggerAt { get; init; }

    /// <summary>
    /// Cancellation token source for canceling this timer.
    /// </summary>
    public CancellationTokenSource Cts { get; init; } = new();

    /// <summary>
    /// Remaining time until timer expiration.
    /// </summary>
    public TimeSpan Remaining => TriggerAt > DateTime.UtcNow ? TriggerAt - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>
    /// Indicates whether the timer has reached or passed its trigger time.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= TriggerAt;
}
