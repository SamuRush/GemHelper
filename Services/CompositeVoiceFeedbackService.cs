using Microsoft.Extensions.Configuration;

namespace Gem.Services;

/// <summary>
/// Hybrid resilient TTS orchestrator.
/// Coordinates Edge-TTS (Primary) and System.Speech SAPI5 (Offline / Safety Fallback)
/// with seamless failover and strict Vosk STT acoustic feedback prevention.
/// </summary>
public class CompositeVoiceFeedbackService : IVoiceFeedbackService, IDisposable
{
    private readonly ITtsEngine _edgeTts;
    private readonly ITtsEngine _systemSpeech;
    private readonly VoiceListener? _voiceListener;
    private readonly SemaphoreSlim _speakingSemaphore = new(1, 1);
    private readonly bool _enableEdgeTts;
    private bool _disposed = false;

    /// <summary>
    /// Global reference to the active composite feedback service instance.
    /// </summary>
    public static CompositeVoiceFeedbackService? Instance { get; set; }

    public event Action? OnSpeakingStarted;
    public event Action? OnSpeakingFinished;

    public ITtsEngine EdgeEngine => _edgeTts;
    public ITtsEngine SystemSpeechEngine => _systemSpeech;

    public string? LastUsedEngineName { get; private set; }

    public CompositeVoiceFeedbackService(
        VoiceListener? voiceListener = null,
        IConfiguration? configuration = null)
        : this(
            voiceListener: voiceListener,
            edgeTts: new EdgeTtsEngine(
                configuration?["Tts:EdgeVoice"] ?? "ru-RU-DmitryNeural",
                int.TryParse(configuration?["Tts:ConnectionTimeoutMs"], out int tMs) && tMs > 0 ? tMs : EdgeTtsEngine.DefaultConnectionTimeoutMs),
            systemSpeech: new SystemSpeechTtsEngine(),
            enableEdgeTts: !bool.TryParse(configuration?["Tts:EnableEdgeTts"], out bool edgeFlag) || edgeFlag)
    {
    }

    public CompositeVoiceFeedbackService(
        VoiceListener? voiceListener,
        TtsConfig ttsConfig)
        : this(
            voiceListener: voiceListener,
            edgeTts: new EdgeTtsEngine(ttsConfig.EdgeVoice, ttsConfig.ConnectionTimeoutMs),
            systemSpeech: new SystemSpeechTtsEngine(),
            enableEdgeTts: ttsConfig.EnableEdgeTts)
    {
    }

    public CompositeVoiceFeedbackService(
        VoiceListener? voiceListener,
        ITtsEngine edgeTts,
        ITtsEngine systemSpeech,
        bool enableEdgeTts = true)
    {
        _voiceListener = voiceListener;
        _edgeTts = edgeTts;
        _systemSpeech = systemSpeech;
        _enableEdgeTts = enableEdgeTts;

        Instance = this;

        string edgeVoice = (_edgeTts as EdgeTtsEngine)?.Voice ?? "ru-RU-DmitryNeural";
        string systemVoice = (_systemSpeech as SystemSpeechTtsEngine)?.SelectedVoiceName ?? "Default";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] [TTS] Инициализирована гибридная архитектура озвучки:");
        Console.WriteLine($"    - EnableEdgeTts:       {_enableEdgeTts}");
        Console.WriteLine($"    - Primary (Online):    {_edgeTts.Name} ({edgeVoice}){(!_enableEdgeTts ? " [ОТКЛЮЧЁН]" : "")}");
        Console.WriteLine($"    - Offline / Fallback:  SAPI5 ({systemVoice})");
        Console.ResetColor();
    }

    // Overload for backward compatibility with legacy 4-argument calls
    public CompositeVoiceFeedbackService(
        VoiceListener? voiceListener,
        ITtsEngine edgeTts,
        ITtsEngine? secondaryTts,
        ITtsEngine systemSpeech,
        bool enableEdgeTts = true)
        : this(voiceListener, edgeTts, systemSpeech, enableEdgeTts)
    {
    }

    /// <summary>
    /// Speaks the given text asynchronously using the hybrid resilient fallback pipeline.
    /// Strictly pauses VoiceListener to avoid acoustic feedback and resumes it on completion.
    /// </summary>
    public virtual async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        await _speakingSemaphore.WaitAsync(cancellationToken);
        try
        {
            // 1. Pause microphone capture before speaking across all engines
            var listener = _voiceListener ?? VoiceListener.Instance;
            listener?.PauseListening();
            // Acoustic Echo Suppression: выставляем _isSpeaking и сбрасываем KWS/STT буферы мгновенно
            listener?.NotifySpeakingStarted();
            OnSpeakingStarted?.Invoke();

            bool spoken = false;
            string edgeVoice = (_edgeTts as EdgeTtsEngine)?.Voice ?? "ru-RU-DmitryNeural";
            string systemVoice = (_systemSpeech as SystemSpeechTtsEngine)?.SelectedVoiceName ?? "Default";

            // Step 1: Primary Engine (Edge-TTS) — только если включён в конфиге
            if (_enableEdgeTts)
            {
                try
                {
                    if (_edgeTts.IsAvailable)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[TTS Engine: Edge-TTS ({edgeVoice})]");
                        Console.ResetColor();

                        await _edgeTts.SpeakAsync(text, cancellationToken);
                        LastUsedEngineName = _edgeTts.Name;
                        spoken = true;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[TTS: Edge] Воспроизведение завершено.");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("[TTS: Warning] Сбой Edge-TTS: сетевой интерфейс недоступен (нет сети). Переключение на SAPI5...");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: Warning] Сбой Edge-TTS: {ex.Message}. Переключение на SAPI5...");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("[TTS] Edge-TTS отключён (EnableEdgeTts=false). Первичный движок — System.Speech SAPI5.");
                Console.ResetColor();
            }

            // Step 2: Offline / Fallback Engine (System.Speech SAPI5)
            if (!spoken)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[TTS Engine: System.Speech SAPI5 ({systemVoice})]");
                    Console.ResetColor();

                    await _systemSpeech.SpeakAsync(text, cancellationToken);
                    LastUsedEngineName = _systemSpeech.Name;

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[TTS: System.Speech] Воспроизведение завершено.");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[TTS: Error] Сбой System.Speech: {ex.Message}");
                    Console.ResetColor();
                }
            }

            // Cooldown перенесён в finally для гарантированного выполнения (см. ниже)
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[TTS] Критическая ошибка воспроизведения речи: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            try
            {
                // 2. Cooldown 250 мс: ждём затухания акустического эха колонок (строго в finally — не прерывается cancellation)
                // Используем независимый CancellationToken, чтобы cooldown не прерывался вместе с основным speech-токеном
                await Task.Delay(250).ConfigureAwait(false);

                // 3. Снять флаги _isSpeaking и _isProcessing строго ПОСЛЕ cooldown (250 мс)
                var listener = _voiceListener ?? VoiceListener.Instance;
                listener?.NotifySpeakingFinished();
                listener?.NotifyProcessingFinished();

                // 4. Strict coordination with Vosk (STT): resume listening or enter confirmation listening
                if (JarvisOrchestrator.Instance.HasPendingAction)
                {
                    listener?.EnterConfirmationListening("Awaiting confirmation reply (bypassing wake-word)...");
                }
                else
                {
                    listener?.ResumeListening();
                }
                OnSpeakingFinished?.Invoke();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[TTS: Warning] Ошибка возобновления микрофона Vosk: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                _speakingSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Speaks text synchronously, blocking the caller until playback is finished.
    /// </summary>
    public void Speak(string text)
    {
        SpeakAsync(text).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Global synchronous speech helper for emergency shutdowns and legacy calls.
    /// </summary>
    public static void SpeakSynchronous(string text)
    {
        if (Instance != null)
        {
            Instance.Speak(text);
            return;
        }

        using var fallbackService = new CompositeVoiceFeedbackService();
        fallbackService.Speak(text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Instance == this)
        {
            Instance = null;
        }

        try { _speakingSemaphore.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[TTS: Warning] Ошибка освобождения Semaphore: {ex.Message}"); }

        try { (_edgeTts as IDisposable)?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[TTS: Warning] Ошибка освобождения EdgeTts: {ex.Message}"); }

        try { (_systemSpeech as IDisposable)?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[TTS: Warning] Ошибка освобождения SystemSpeech: {ex.Message}"); }
    }
}
