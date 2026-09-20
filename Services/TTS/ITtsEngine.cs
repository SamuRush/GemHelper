namespace Gem.Services;

/// <summary>
/// Text-to-speech synthesis engine abstraction.
/// </summary>
public interface ITtsEngine
{
    /// <summary>
    /// Human-readable engine identifier (e.g. "Edge", "Silero", "System.Speech").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Indicates whether the engine is ready and available for synthesis.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Synthesizes and plays the specified text asynchronously.
    /// Awaits until audio playback completes or cancellation is requested.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken ct = default);
}
