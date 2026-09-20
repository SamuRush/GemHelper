namespace Gem.Services;

/// <summary>
/// Unified contract for text-to-speech voice feedback across all JARVIS components.
/// Coordinates resilient failover (Edge-TTS -> Silero ONNX -> System.Speech)
/// and acoustic feedback suppression with VoiceListener STT.
/// </summary>
public interface IVoiceFeedbackService : IDisposable
{
    /// <summary>
    /// Speaks the given text asynchronously using the hybrid resilient fallback pipeline.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Speaks text synchronously, blocking the caller until playback is finished.
    /// </summary>
    void Speak(string text);

    /// <summary>
    /// Primary online neural TTS engine (Microsoft Edge TTS).
    /// </summary>
    ITtsEngine EdgeEngine { get; }

    /// <summary>
    /// Secondary local offline TTS engine (Silero ONNX).
    /// </summary>
    ITtsEngine SileroEngine { get; }

    /// <summary>
    /// Safety fallback TTS engine (Windows System.Speech SAPI).
    /// </summary>
    ITtsEngine SystemSpeechEngine { get; }

    /// <summary>
    /// Name of the engine that was used in the most recent successful speech synthesis.
    /// </summary>
    string? LastUsedEngineName { get; }

    /// <summary>
    /// Event triggered when speech playback starts (used to mute STT or update HUD).
    /// </summary>
    event Action? OnSpeakingStarted;

    /// <summary>
    /// Event triggered when speech playback finishes.
    /// </summary>
    event Action? OnSpeakingFinished;
}
