using System.Text.RegularExpressions;
using Gem.Core;
using Vosk;

namespace Gem.Voice;

public enum VoiceListenerState
{
    Stopped,
    WaitingForWakeWord,
    ListeningForCommand
}

/// <summary>
/// Background microphone listener based on Vosk speech recognition.
/// Uses a thread-safe singleton Vosk Model instance to guarantee zero unmanaged memory leaks.
/// Orchestrates wake-word detection, command recognition, and state machine transitions.
/// </summary>
public sealed partial class VoiceListener : IDisposable
{
    public static VoiceListener? Instance { get; internal set; }

    public static void PlayReadySoundAsync()
    {
        // Silent mode: ready beep signal is disabled
    }

    public static Model GetOrInitModel(string modelPath) => VoskModelManager.GetOrInitModel(modelPath);

    private readonly string _modelPath;
    private readonly string[] _wakeWords;
    private readonly string _wakeWordsRegexPattern;
    private double _silenceThresholdRms;
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _commandWaitTimeout;
    private readonly IWakeWordDetector _wakeWordDetector;
    private readonly SpeechPhraseBuffer _phraseBuffer = new();
    private readonly MicrophoneAudioRecorder _audioRecorder;

    public int DeviceNumber => _audioRecorder.DeviceNumber;
    public double SilenceThresholdRms
    {
        get => _silenceThresholdRms;
        set => SetNoiseGateRms(value);
    }

    public void SetNoiseGateRms(double noiseGateRms)
    {
        _silenceThresholdRms = Math.Clamp(noiseGateRms, 50.0, 600.0);
        if (_wakeWordDetector is VoskGrammarWakeWordDetector voskDetector)
        {
            voskDetector.NoiseGateRms = _silenceThresholdRms;
        }
    }

    public void SetAudioDevice(int deviceNumber)
    {
        lock (_lock)
        {
            _audioRecorder.SetDeviceNumber(deviceNumber);
        }
    }

    private VoskRecognizer? _recognizer;
    private VoiceListenerState _state = VoiceListenerState.Stopped;
    private readonly object _lock = new();

    private bool _wakeWordTriggered = false;
    private string _triggeredWakeWord = string.Empty;

    private DateTime _stateEnteredTime = DateTime.MinValue;
    private DateTime _lastSpeechTime = DateTime.MinValue;
    private bool _voiceDetectedInCommand = false;
    private string _pendingCommandText = string.Empty;
    private string _lastLoggedPartial = string.Empty;
    private string _lastPartialText = string.Empty;
    private bool _disposed = false;
    private volatile bool _isPaused = false;
    private volatile bool _isSpeaking = false;
    private volatile bool _isProcessing = false;

    public void AppendSessionText(string chunk) => _phraseBuffer.AppendSessionText(chunk);
    public string GetCurrentSessionText() => _phraseBuffer.GetCurrentSessionText();

    public event Action<string>? OnWakeWordDetected;
    public event Action<string>? OnCommandSpoken;
    public event Action<float>? OnRmsLevelChanged;
    public event Action<byte[], int>? OnAudioBufferAvailable;
    public event Action<string>? OnPartialResult;
    public event Action<string>? OnFinalResult;
    public event Action<VoiceListenerState, string>? OnStatusChanged;

    public VoiceListenerState CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public bool IsRunning => CurrentState != VoiceListenerState.Stopped;
    public bool IsPaused => _isPaused;
    public bool IsProcessing => _isProcessing;
    public bool IsSpeaking => _isSpeaking;

    public IReadOnlyList<string> WakeWords => _wakeWords;
    public string WakeWordsRegexPattern => _wakeWordsRegexPattern;

    private void ClearPendingState(string initialText = "")
    {
        _pendingCommandText = initialText;
        _phraseBuffer.Reset(initialText);
        _lastLoggedPartial = initialText;
        _lastPartialText = initialText;
        _voiceDetectedInCommand = !string.IsNullOrWhiteSpace(initialText);
    }

    public void PauseListening()
    {
        lock (_lock)
        {
            _isPaused = true;
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();
        }
    }

    public void ResumeListening()
    {
        lock (_lock)
        {
            _isPaused = false;
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();
            _lastSpeechTime = DateTime.UtcNow;
            _stateEnteredTime = DateTime.UtcNow;
        }
    }

    public void NotifySpeakingStarted()
    {
        _isSpeaking = true;
        lock (_lock)
        {
            _recognizer?.Reset();
            ResetWakeWordState();
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] TTS начат — микрофон заблокирован (Acoustic Echo Suppression).");
        Console.ResetColor();
    }

    public void NotifySpeakingFinished()
    {
        _isSpeaking = false;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] TTS завершён — флаг _isSpeaking снят.");
        Console.ResetColor();
    }

    public void NotifyProcessingStarted()
    {
        lock (_lock)
        {
            _isProcessing = true;
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Команда отправлена в обработку (LLM / Router) — микрофон заблокирован (_isProcessing = true).");
        Console.ResetColor();
    }

    public void NotifyProcessingFinished()
    {
        lock (_lock)
        {
            _isProcessing = false;
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();
            _lastSpeechTime = DateTime.UtcNow;
            _stateEnteredTime = DateTime.UtcNow;
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Обработка и TTS завершены + cooldown истёк — микрофон разблокирован (_isProcessing = false).");
        Console.ResetColor();
    }

    public void EnterConfirmationListening(string reason = "Awaiting confirmation reply (bypassing wake-word)...")
    {
        lock (_lock)
        {
            _isPaused = false;
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();
            _lastSpeechTime = DateTime.UtcNow;
            _stateEnteredTime = DateTime.UtcNow;
            ChangeState(VoiceListenerState.ListeningForCommand, reason);
        }
        Gem.Services.JarvisOrchestrator.Instance.RestartConfirmationTimeout();
    }

    private static readonly string[] DefaultWakeWords =
    [
        "джарвис", "jarvis", "рис", "вис",
        "джемини", "гемини", "джеминай", "gemini",
        "димон", "петрович", "алиса", "компьютер"
    ];

    public static bool IsFastCommand(string text) => SpeechPhraseBuffer.IsFastCommand(text);

    public VoiceListener(
        string modelPath = VoskModelHelper.DefaultModelFolder,
        string[]? wakeWords = null,
        TimeSpan? silenceTimeout = null,
        double silenceThresholdRms = 350.0,
        TimeSpan? commandWaitTimeout = null,
        IWakeWordDetector? wakeWordDetector = null,
        int? deviceNumber = null)
    {
        Instance = this;
        _modelPath = modelPath;

        var settings = AppSettingsService.Load();
        var configured = settings.WakeWords;
        var wordsSource = (wakeWords != null && wakeWords.Length > 0)
            ? wakeWords
            : (configured != null && configured.Count > 0)
                ? configured.ToArray()
                : DefaultWakeWords;

        _wakeWords = wordsSource
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        _wakeWordsRegexPattern = @"^(" + string.Join("|", _wakeWords.Select(Regex.Escape)) + @")\s*";
        _silenceTimeout = silenceTimeout ?? TimeSpan.FromMilliseconds(700);

        double configuredRms = settings.WakeWord?.NoiseGateRms ?? 350.0;
        _silenceThresholdRms = (silenceThresholdRms != 450.0 && silenceThresholdRms != 350.0 && silenceThresholdRms > 0) ? silenceThresholdRms : configuredRms;
        _commandWaitTimeout = commandWaitTimeout ?? TimeSpan.FromSeconds(4.5);

        int devIndex = deviceNumber ?? settings.Audio?.DeviceIndex ?? 0;
        _audioRecorder = new MicrophoneAudioRecorder(devIndex);
        _audioRecorder.OnAudioDataAvailable += HandleAudioData;

        string primaryWord = settings.WakeWord?.Name ?? _wakeWords.FirstOrDefault() ?? "джарвис";
        float threshold = (float)(settings.WakeWord?.Threshold ?? 0.5);
        int minDurationMs = settings.WakeWord?.MinDurationMs ?? 150;

        _wakeWordDetector = wakeWordDetector ?? WakeWordFactory.Create(
            primaryWord,
            settings.WakeWord?.OnnxModelPath,
            settings.WakeWord?.SmallModelPath,
            threshold,
            minDurationMs,
            _silenceThresholdRms);

        _wakeWordDetector.OnWakeWordDetected += OnAdaptiveWakeWordDetected;
    }

    private void OnAdaptiveWakeWordDetected()
    {
        lock (_lock)
        {
            if (_state != VoiceListenerState.WaitingForWakeWord || _isPaused || _isSpeaking || _isProcessing)
            {
                return;
            }

            string engineName = (_wakeWordDetector is OpenWakeWordDetector || _wakeWordDetector.Name.Contains("OpenWakeWord", StringComparison.OrdinalIgnoreCase))
                ? "OpenWakeWord (ONNX)"
                : $"VoskGrammar (\"{_wakeWordDetector.WakeWord}\")";
            long latencyMs = _wakeWordDetector.LastDetectionLatencyMs;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [WakeWord: {engineName}] [Detection Latency: {latencyMs} ms]");
            Console.ResetColor();

            _wakeWordDetector.Reset();
            TransitionToListeningForCommand(_wakeWordDetector.WakeWord, null);
            OnWakeWordDetected?.Invoke(_wakeWordDetector.WakeWord);
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_state != VoiceListenerState.Stopped) return;

            var model = GetOrInitModel(_modelPath);
            DisposeRecognizer();

            _recognizer = new VoskRecognizer(model, 16000.0f);
            _recognizer.SetMaxAlternatives(0);

            ChangeState(VoiceListenerState.WaitingForWakeWord, "Started listening for wake-word...");
            _audioRecorder.StartRecording();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_state == VoiceListenerState.Stopped) return;

            ChangeState(VoiceListenerState.Stopped, "Microphone listener stopped.");
            _audioRecorder.StopRecording();
            DisposeRecognizer();
        }
    }

    private void DisposeRecognizer()
    {
        if (_recognizer != null)
        {
            try { _recognizer.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[VoiceListener Warning] Ошибка Dispose VoskRecognizer: {ex.Message}"); }
            _recognizer = null;
        }
    }

    private void HandleAudioData(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded == 0 || _isPaused || _isSpeaking || _isProcessing) return;

        lock (_lock)
        {
            if (_state == VoiceListenerState.Stopped || _isPaused || _isSpeaking || _isProcessing || _recognizer == null)
            {
                return;
            }

            double rms = AudioEnergyGate.CalculateRms(buffer, bytesRecorded);
            OnRmsLevelChanged?.Invoke((float)rms);
            OnAudioBufferAvailable?.Invoke(buffer, bytesRecorded);
            bool isAudioSpeech = rms >= _silenceThresholdRms;

            if (_state == VoiceListenerState.WaitingForWakeWord)
            {
                if (!isAudioSpeech) return;
                ProcessWakeWordListening(buffer, bytesRecorded, isAudioSpeech);
            }
            else if (_state == VoiceListenerState.ListeningForCommand)
            {
                ProcessCommandListening(buffer, bytesRecorded, isAudioSpeech);
            }
        }
    }


    private bool TryFindWakeWord(string text, out string matchedWakeWord) =>
        SpeechPhraseBuffer.TryFindWakeWord(text, _wakeWords, out matchedWakeWord);

    private void ResetWakeWordState()
    {
        _wakeWordTriggered = false;
        _triggeredWakeWord = string.Empty;
        _wakeWordDetector?.Reset();
    }

    private void TransitionToListeningForCommand(string wakeWord, string? pendingText)
    {
        _recognizer?.Reset();
        ResetWakeWordState();
        ClearPendingState(pendingText ?? string.Empty);
        _lastSpeechTime = DateTime.UtcNow;

        ChangeState(VoiceListenerState.ListeningForCommand, $"Wake-word '{wakeWord}' detected! Listening for command...");
    }

    public void TransitionToWaitingForWakeWord(string reason)
    {
        lock (_lock)
        {
            _recognizer?.Reset();
            ResetWakeWordState();
            ClearPendingState();

            ChangeState(VoiceListenerState.WaitingForWakeWord, reason);
            MemoryTrimmer.TrimMemory("VoiceListener Idle", minIntervalSeconds: 15.0);
        }
    }

    public string CleanUpCommandText(string text) => SpeechPhraseBuffer.CleanUpCommandText(text, _wakeWords);

    private void ChangeState(VoiceListenerState newState, string reason)
    {
        _state = newState;
        _stateEnteredTime = DateTime.UtcNow;
        if (newState == VoiceListenerState.WaitingForWakeWord)
        {
            ResetWakeWordState();
        }
        OnStatusChanged?.Invoke(newState, reason);
    }

    public static double CalculateRms(byte[] buffer, int bytesRecorded) =>
        AudioEnergyGate.CalculateRms(buffer, bytesRecorded);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _audioRecorder.Dispose();
        _wakeWordDetector?.Dispose();
        if (Instance == this)
        {
            Instance = null;
        }
        OnWakeWordDetected = null;
        OnCommandSpoken = null;
        OnStatusChanged = null;
        OnRmsLevelChanged = null;
        OnAudioBufferAvailable = null;
        OnPartialResult = null;
        OnFinalResult = null;
    }
}
