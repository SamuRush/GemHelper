using Microsoft.Extensions.Configuration;

namespace Gem.Services;

/// <summary>
/// Hybrid resilient TTS orchestrator.
/// Coordinates Edge-TTS (Primary), Silero ONNX (Secondary / Offline), and System.Speech (Safety Fallback)
/// with seamless failover and strict Vosk STT acoustic feedback prevention.
/// </summary>
public class CompositeVoiceFeedbackService : IVoiceFeedbackService, IDisposable
{
    private readonly ITtsEngine _edgeTts;
    private readonly ITtsEngine _sileroTts;
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
    public ITtsEngine SileroEngine => _sileroTts;
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
            sileroTts: new SileroTtsEngine(
                configuration?["Tts:SileroModelPath"] ?? SileroTtsEngine.DefaultModelPath,
                configuration?["Tts:SileroSpeaker"] ?? "aidar"),
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
            sileroTts: new SileroTtsEngine(ttsConfig.SileroModelPath, ttsConfig.SileroSpeaker),
            systemSpeech: new SystemSpeechTtsEngine(),
            enableEdgeTts: ttsConfig.EnableEdgeTts)
    {
    }

    public CompositeVoiceFeedbackService(
        VoiceListener? voiceListener,
        ITtsEngine edgeTts,
        ITtsEngine sileroTts,
        ITtsEngine systemSpeech,
        bool enableEdgeTts = true)
    {
        _voiceListener = voiceListener;
        _edgeTts = edgeTts;
        _sileroTts = sileroTts;
        _systemSpeech = systemSpeech;
        _enableEdgeTts = enableEdgeTts;

        Instance = this;

        string edgeVoice = (_edgeTts as EdgeTtsEngine)?.Voice ?? "ru-RU-DmitryNeural";
        string sileroSpeaker = (_sileroTts as SileroTtsEngine)?.Speaker ?? "aidar";
        string systemVoice = (_systemSpeech as SystemSpeechTtsEngine)?.SelectedVoiceName ?? "Default";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] [TTS] Инициализирована гибридная архитектура озвучки:");
        Console.WriteLine($"    - EnableEdgeTts:       {_enableEdgeTts}");
        Console.WriteLine($"    - Primary (Online):    {_edgeTts.Name} ({edgeVoice}){(!_enableEdgeTts ? " [ОТКЛЮЧЁН]" : "")}");
        Console.WriteLine($"    - Offline / Fallback:  SAPI5 ({systemVoice})");
        Console.ResetColor();

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
            OnSpeakingStarted?.Invoke();

            bool spoken = false;
            string edgeVoice = (_edgeTts as EdgeTtsEngine)?.Voice ?? "ru-RU-DmitryNeural";
            string sileroSpeaker = (_sileroTts as SileroTtsEngine)?.Speaker ?? "aidar";
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
                        Console.WriteLine("[TTS: Warning] Сбой Edge-TTS: сетевой интерфейс недоступен (нет сети). Переключение на Silero TTS...");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: Warning] Сбой Edge-TTS: {ex.Message}. Переключение на Silero TTS...");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("[TTS] Edge-TTS отключён (EnableEdgeTts=false). Первичный движок — System.Speech (Silero SAPI5 Aidar).");
                Console.ResetColor();
            }

            // Step 2: Secondary Engine (Silero ONNX Offline)
            if (!spoken)
            {
                try
                {
                    if (_sileroTts.IsAvailable)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[TTS Engine: Silero ({sileroSpeaker})]");
                        Console.ResetColor();

                        await _sileroTts.SpeakAsync(text, cancellationToken);
                        LastUsedEngineName = _sileroTts.Name;
                        spoken = true;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[TTS: Silero] Воспроизведение завершено.");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("[TTS: Warning] Сбой Silero ONNX: модель не найдена или не инициализирована. Переключение на System.Speech.");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: Warning] Сбой Silero ONNX: {ex.Message}. Переключение на System.Speech.");
                    Console.ResetColor();
                }
            }

            // Step 3: Safety Fallback Engine (System.Speech)
            if (!spoken)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("[TTS Engine: System.Speech Fallback]");
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

            // 2. Cooldown delay to allow speaker acoustic reverberation to dissipate
            await Task.Delay(300, cancellationToken);
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
                // 3. Strict coordination with Vosk (STT): resume listening or enter confirmation listening
                var listener = _voiceListener ?? VoiceListener.Instance;
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

        try { (_sileroTts as IDisposable)?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[TTS: Warning] Ошибка освобождения SileroTts: {ex.Message}"); }

        try { (_systemSpeech as IDisposable)?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[TTS: Warning] Ошибка освобождения SystemSpeech: {ex.Message}"); }
    }
}
