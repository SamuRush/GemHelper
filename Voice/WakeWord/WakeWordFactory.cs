namespace Gem.Voice;

/// <summary>
/// Factory that creates the appropriate IWakeWordDetector implementation:
/// - OpenWakeWordDetector (ONNX Runtime, ultra-low latency <80ms) for default Jarvis names ("джарвис", "jarvis", "рис", "вис").
/// - VoskGrammarWakeWordDetector (strictly constrained grammar on vosk-model-small-ru) for custom names ("петрович", "гена", "цицерон", etc.).
/// </summary>
public static class WakeWordFactory
{
    private static readonly HashSet<string> JarvisAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "джарвис",
        "jarvis",
        "рис",
        "вис"
    };

    /// <summary>
    /// Creates an adaptive wake-word detector based on the configured wake word.
    /// </summary>
    /// <param name="wakeWord">Target name to activate on.</param>
    /// <param name="onnxModelPath">Optional path to the jarvis.onnx model.</param>
    /// <param name="smallModelPath">Optional path to the vosk-model-small-ru directory.</param>
    /// <returns>Instance of IWakeWordDetector.</returns>
    public static IWakeWordDetector Create(
        string? wakeWord = null,
        string? onnxModelPath = null,
        string? smallModelPath = null,
        float threshold = 0.5f,
        int minDurationMs = VoskGrammarWakeWordDetector.DefaultMinDurationMs,
        double noiseGateRms = VoskGrammarWakeWordDetector.DefaultNoiseGateRms)
    {
        string target = string.IsNullOrWhiteSpace(wakeWord) ? "джарвис" : wakeWord.Trim().ToLowerInvariant();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[WakeWordFactory] Выбран Vosk Grammar KWS детектор по умолчанию для имени '{target}' " +
                          $"(малая модель vosk-model-small-ru, minDuration: {minDurationMs} мс, RMS Noise Gate: {noiseGateRms:0}).");
        Console.ResetColor();

        return new VoskGrammarWakeWordDetector(target, smallModelPath, minDurationMs, noiseGateRms);
    }
}
