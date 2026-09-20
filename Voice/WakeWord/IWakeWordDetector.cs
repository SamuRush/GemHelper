namespace Gem.Voice;

/// <summary>
/// Common contract for streaming wake-word detectors.
/// Processes audio frames on the fly without audio accumulation.
/// </summary>
public interface IWakeWordDetector : IDisposable
{
    /// <summary>
    /// Descriptive name of the detection engine (e.g. "OpenWakeWord-ONNX", "Vosk-Grammar").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The target wake-word name being listened for (e.g. "джарвис", "петрович").
    /// </summary>
    string WakeWord { get; }

    /// <summary>
    /// Event fired when the target wake-word has been successfully detected.
    /// </summary>
    event Action OnWakeWordDetected;

    /// <summary>
    /// Processes a streaming 16kHz 16-bit mono PCM audio chunk.
    /// Returns true if the wake-word was detected in this frame.
    /// </summary>
    /// <param name="pcmData">Raw PCM audio bytes span.</param>
    /// <returns>True if detected; otherwise false.</returns>
    bool ProcessFrame(ReadOnlySpan<byte> pcmData);

    /// <summary>
    /// Resets internal buffers and detection state after a trigger or state switch.
    /// </summary>
    void Reset();
}
