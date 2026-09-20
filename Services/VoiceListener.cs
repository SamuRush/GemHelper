using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
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
/// Uses a thread-safe singleton Vosk Model instance (_sharedModel) to guarantee zero unmanaged model memory leaks.
/// Audio buffers are streamed directly to the recognizer without storing in memory collections.
/// Recognizers are strictly disposed before any recreation on state transitions.
/// </summary>
public sealed class VoiceListener : IDisposable
{
    /// <summary>
    /// Global reference to the active VoiceListener instance.
    /// </summary>
    public static VoiceListener? Instance { get; internal set; }

    /// <summary>
    /// Plays the wake-word readiness signal asynchronously so it does not block the audio capture thread.
    /// Completely muted: method body is empty (silent mode).
    /// </summary>
    public static void PlayReadySoundAsync()
    {
        // Полное отключение звукового сигнала готовности (бип) — работа в полностью бесшумном режиме.
    }

    // =========================================================================
    // 1. SINGLETON VOSK MODEL (Loaded strictly ONCE for the app lifetime)
    // =========================================================================
    private static Model? _sharedModel;
    private static readonly object _modelLock = new();

    /// <summary>
    /// Thread-safe singleton accessor for Vosk.Model.
    /// Ensures the heavy acoustic model (~45MB-3.5GB unmanaged memory) is loaded
    /// strictly ONCE across the entire application lifecycle.
    /// </summary>
    public static Model GetOrInitModel(string modelPath)
    {
        if (_sharedModel != null)
        {
            return _sharedModel;
        }

        lock (_modelLock)
        {
            if (_sharedModel == null)
            {
                if (!Directory.Exists(modelPath))
                {
                    throw new DirectoryNotFoundException(
                        $"Vosk model directory not found at '{modelPath}'. Please download a Vosk model (e.g. vosk-model-small-ru) or run model setup."
                    );
                }

                // Suppress verbose Vosk C-library logs
                Vosk.Vosk.SetLogLevel(-1);

                // Optimization: disable heavy offline rescorers (rescore/rnnlm) if present
                // to maintain memory footprint strictly under 1.5 GB RAM while keeping full speech accuracy
                OptimizeModelDirectoryForRealtime(modelPath);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] [Vosk Singleton] Загрузка модели из '{modelPath}' (выполняется 1 раз за жизнь приложения)...");
                Console.ResetColor();

                _sharedModel = new Model(modelPath);
            }
            return _sharedModel;
        }
    }

    /// <summary>
    /// Optimizes model directory for real-time streaming:
    /// Isolates heavy offline rescoring graphs (rescore/rnnlm ~2.3GB), reducing RAM usage
    /// from ~3GB down to ~1.2GB while maintaining full recognition accuracy.
    /// </summary>
    private static void OptimizeModelDirectoryForRealtime(string modelPath)
    {
        try
        {
            string rescoreDir = Path.Combine(modelPath, "rescore");
            string rescoreDisabled = Path.Combine(modelPath, "rescore.disabled");
            if (Directory.Exists(rescoreDir) && !Directory.Exists(rescoreDisabled))
            {
                Directory.Move(rescoreDir, rescoreDisabled);
            }

            string rnnlmDir = Path.Combine(modelPath, "rnnlm");
            string rnnlmDisabled = Path.Combine(modelPath, "rnnlm.disabled");
            if (Directory.Exists(rnnlmDir) && !Directory.Exists(rnnlmDisabled))
            {
                Directory.Move(rnnlmDir, rnnlmDisabled);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceListener Warning] Ошибка оптимизации каталога модели Vosk: {ex.Message}");
        }
    }

    private readonly string _modelPath;
    private readonly string[] _wakeWords;
    private readonly string _wakeWordsRegexPattern;
    private readonly double _silenceThresholdRms;
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _commandWaitTimeout;
    private readonly IWakeWordDetector _wakeWordDetector;

    private VoskRecognizer? _recognizer;
    private WaveInEvent? _waveIn;

    private VoiceListenerState _state = VoiceListenerState.Stopped;
    private readonly object _lock = new();

    // Hybrid wake-word tracking & silence measurement
    private bool _wakeWordTriggered = false;
    private string _triggeredWakeWord = string.Empty;
    private Stopwatch _silenceTimer = new();
    private bool _soundPlayed = false;
    private bool _hasSpokenAfterWakeWord = false;

    private DateTime _stateEnteredTime = DateTime.MinValue;
    private DateTime _lastSpeechTime = DateTime.MinValue;
    private bool _voiceDetectedInCommand = false;
    private string _pendingCommandText = string.Empty;
    private string _sessionAccumulatedText = string.Empty;
    private string _currentPartialText = string.Empty;
    private string _lastLoggedPartial = string.Empty;
    private string _lastPartialText = string.Empty;
    private bool _disposed = false;
    private volatile bool _isPaused = false;

    /// <summary>
    /// Appends a speech chunk to the current session accumulated command text,
    /// deduplicating overlaps and prefixes.
    /// </summary>
    public void AppendSessionText(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return;
        chunk = chunk.Trim();
        if (string.IsNullOrWhiteSpace(_sessionAccumulatedText))
        {
            _sessionAccumulatedText = chunk;
        }
        else
        {
            if (!_sessionAccumulatedText.EndsWith(chunk, StringComparison.OrdinalIgnoreCase))
            {
                if (chunk.StartsWith(_sessionAccumulatedText, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionAccumulatedText = chunk;
                }
                else
                {
                    _sessionAccumulatedText = $"{_sessionAccumulatedText} {chunk}".Trim();
                }
            }
        }
    }

    /// <summary>
    /// Gets the full candidate text for the current command session (accumulated chunks + current partial).
    /// </summary>
    public string GetCurrentSessionText()
    {
        if (string.IsNullOrWhiteSpace(_sessionAccumulatedText))
        {
            return _currentPartialText;
        }
        if (string.IsNullOrWhiteSpace(_currentPartialText))
        {
            return _sessionAccumulatedText;
        }
        if (_currentPartialText.StartsWith(_sessionAccumulatedText, StringComparison.OrdinalIgnoreCase))
        {
            return _currentPartialText;
        }
        if (_sessionAccumulatedText.EndsWith(_currentPartialText, StringComparison.OrdinalIgnoreCase))
        {
            return _sessionAccumulatedText;
        }
        return $"{_sessionAccumulatedText} {_currentPartialText}".Trim();
    }

    /// <summary>
    /// Event fired when the wake-word is detected.
    /// </summary>
    public event Action<string>? OnWakeWordDetected;

    /// <summary>
    /// Event fired when a voice command is captured following the wake-word.
    /// </summary>
    public event Action<string>? OnCommandSpoken;

    /// <summary>
    /// Event fired on internal status updates or state changes.
    /// </summary>
    public event Action<VoiceListenerState, string>? OnStatusChanged;

    public VoiceListenerState CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public bool IsRunning => CurrentState != VoiceListenerState.Stopped;

    public bool IsPaused => _isPaused;

    public IReadOnlyList<string> WakeWords => _wakeWords;
    public string WakeWordsRegexPattern => _wakeWordsRegexPattern;

    /// <summary>
    /// Temporarily pauses processing of microphone audio (e.g. while Jarvis is speaking).
    /// Prevents acoustic feedback/looping where assistant recognizes its own voice.
    /// </summary>
    public void PauseListening()
    {
        lock (_lock)
        {
            _isPaused = true;
            _recognizer?.Reset();
            ResetWakeWordState();
            _pendingCommandText = string.Empty;
            _sessionAccumulatedText = string.Empty;
            _currentPartialText = string.Empty;
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;
        }
    }

    /// <summary>
    /// Resumes microphone audio processing.
    /// </summary>
    public void ResumeListening()
    {
        lock (_lock)
        {
            _isPaused = false;
            _recognizer?.Reset();
            ResetWakeWordState();
            _pendingCommandText = string.Empty;
            _sessionAccumulatedText = string.Empty;
            _currentPartialText = string.Empty;
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;
            _lastSpeechTime = DateTime.UtcNow;
            _stateEnteredTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Enters confirmation listening mode directly, bypassing wake-word detection.
    /// Used when assistant asks a confirmation question ("Did you mean X?") and waits for yes/no.
    /// </summary>
    public void EnterConfirmationListening(string reason = "Awaiting confirmation reply (bypassing wake-word)...")
    {
        lock (_lock)
        {
            _isPaused = false;
            _recognizer?.Reset();
            ResetWakeWordState();
            _pendingCommandText = string.Empty;
            _sessionAccumulatedText = string.Empty;
            _currentPartialText = string.Empty;
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;
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

    private static readonly HashSet<string> FastCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "закройся",
        "закрывайся",
        "выключись",
        "стоп",
        "отмена",
        "закрой монстер хантер",
        "закрой игру"
    };

    /// <summary>
    /// Checks whether the spoken text matches any of the fast command templates for instant execution.
    /// </summary>
    public static bool IsFastCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim().ToLowerInvariant();
        if (FastCommands.Contains(trimmed)) return true;
        foreach (var cmd in FastCommands)
        {
            if (trimmed.Equals(cmd, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(cmd + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Initializes a new instance of VoiceListener.
    /// </summary>
    /// <param name="modelPath">Path to Vosk speech recognition model directory.</param>
    /// <param name="wakeWords">List of wake-words to trigger on (default: loaded from config / DefaultWakeWords).</param>
    /// <param name="silenceTimeout">Duration of natural silence after speaking to finish the command.</param>
    /// <param name="silenceThresholdRms">RMS audio energy threshold to consider audio as speech.</param>
    /// <param name="commandWaitTimeout">Max duration to wait for user to start speaking after wake-word.</param>
    public VoiceListener(
        string modelPath = VoskModelHelper.DefaultModelFolder,
        string[]? wakeWords = null,
        TimeSpan? silenceTimeout = null,
        double silenceThresholdRms = 350.0,
        TimeSpan? commandWaitTimeout = null,
        IWakeWordDetector? wakeWordDetector = null)
    {
        Instance = this;
        _modelPath = modelPath;

        // 1. Загрузи список WakeWords из конфигурации (приведи все к нижнему регистру).
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
        _silenceThresholdRms = silenceThresholdRms;
        _commandWaitTimeout = commandWaitTimeout ?? TimeSpan.FromSeconds(4.5);

        // 2. Инициализация адаптивного детектора вейк-ворда через WakeWordFactory
        string primaryWord = settings.WakeWord?.Name ?? _wakeWords.FirstOrDefault() ?? "джарвис";
        _wakeWordDetector = wakeWordDetector ?? WakeWordFactory.Create(
            primaryWord,
            settings.WakeWord?.OnnxModelPath,
            settings.WakeWord?.SmallModelPath);

        _wakeWordDetector.OnWakeWordDetected += OnAdaptiveWakeWordDetected;
    }

    private void OnAdaptiveWakeWordDetected()
    {
        lock (_lock)
        {
            if (_state != VoiceListenerState.WaitingForWakeWord || _isPaused)
            {
                return;
            }

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Адаптивный детектор: зафиксировано имя '{_wakeWordDetector.WakeWord}' (задержка <80 мс, бесшумный режим).");
            Console.ResetColor();

            _wakeWordDetector.Reset();
            TransitionToListeningForCommand(_wakeWordDetector.WakeWord, null);
            OnWakeWordDetected?.Invoke(_wakeWordDetector.WakeWord);
        }
    }

    /// <summary>
    /// Starts background audio capture and Vosk recognition.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_state != VoiceListenerState.Stopped)
            {
                return;
            }

            // 1. Get or initialize the static singleton Model (never call new Model() repeatedly!)
            var model = GetOrInitModel(_modelPath);

            // 2. Dispose previous recognizer instance if any exists before creating a new one
            DisposeRecognizer();

            _recognizer = new VoskRecognizer(model, 16000.0f);
            _recognizer.SetMaxAlternatives(0);

            // 3. Setup NAudio wave in capture (direct streaming without memory accumulation)
            int bufferMs = 35;
            if (bufferMs > 50) bufferMs = 35;
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = bufferMs,
                NumberOfBuffers = 3
            };

            _waveIn.DataAvailable += OnAudioDataAvailable;

            ChangeState(VoiceListenerState.WaitingForWakeWord, "Started listening for wake-word...");
            _waveIn.StartRecording();
        }
    }

    /// <summary>
    /// Stops audio capture and frees audio resources.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_state == VoiceListenerState.Stopped)
            {
                return;
            }

            ChangeState(VoiceListenerState.Stopped, "Microphone listener stopped.");

            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnAudioDataAvailable;
                try { _waveIn.StopRecording(); }
                catch (Exception ex) { Console.WriteLine($"[VoiceListener Warning] Ошибка StopRecording: {ex.Message}"); }
                _waveIn.Dispose();
                _waveIn = null;
            }

            // Dispose recognizer on stop
            DisposeRecognizer();

            // NOTE: _sharedModel is preserved as a permanent singleton and never disposed or recreated here!
        }
    }

    /// <summary>
    /// Releases the previous VoskRecognizer unmanaged resources immediately.
    /// </summary>
    private void DisposeRecognizer()
    {
        if (_recognizer != null)
        {
            try
            {
                _recognizer.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceListener Warning] Ошибка Dispose VoskRecognizer: {ex.Message}");
            }
            _recognizer = null;
        }
    }

    /// <summary>
    /// Direct streaming of audio buffers from NAudio to Vosk.
    /// Audio bytes are passed immediately to recognizer.AcceptWaveform without
    /// storing or accumulating in any List or MemoryStream collections.
    /// </summary>
    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _isPaused)
        {
            return;
        }

        lock (_lock)
        {
            if (_state == VoiceListenerState.Stopped || _isPaused || _recognizer == null)
            {
                return;
            }

            double rms = CalculateRms(e.Buffer, e.BytesRecorded);
            bool isAudioSpeech = rms >= _silenceThresholdRms;

            if (_state == VoiceListenerState.WaitingForWakeWord)
            {
                ProcessWakeWordListening(e.Buffer, e.BytesRecorded, isAudioSpeech);
            }
            else if (_state == VoiceListenerState.ListeningForCommand)
            {
                ProcessCommandListening(e.Buffer, e.BytesRecorded, isAudioSpeech);
            }
        }
    }

    private bool TryFindWakeWord(string text, out string matchedWakeWord)
    {
        matchedWakeWord = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var words = text.Split(new[] { ' ', '\t', ',', '.', '!', '?', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            string cleanWord = word.Trim();
            foreach (var trigger in _wakeWords)
            {
                if (string.Equals(cleanWord, trigger, StringComparison.OrdinalIgnoreCase))
                {
                    matchedWakeWord = trigger;
                    return true;
                }
            }
        }
        return false;
    }

    private void ProcessWakeWordListening(byte[] buffer, int bytesRecorded, bool isAudioSpeech)
    {
        // 1. Адаптивный ультра-быстрый Wake-Word детектор (<80 мс, OpenWakeWord ONNX / Vosk Grammar)
        if (_wakeWordDetector.ProcessFrame(buffer.AsSpan(0, bytesRecorded)))
        {
            return;
        }

        // 2. Fallback сквозного распознавания Vosk для длинных слитных фраз
        bool isFinal = _recognizer!.AcceptWaveform(buffer, bytesRecorded);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string rawText = ExtractTextFromJson(json, isFinal).Trim();

        if (isFinal)
        {
            ProcessWakeWordFinalResult(rawText);
            return;
        }

        // Processing PartialResult
        if (string.IsNullOrWhiteSpace(rawText))
        {
            if (_wakeWordTriggered)
            {
                CheckSilenceSoundTrigger(isAudioSpeech);
            }
            return;
        }

        string text = rawText.ToLowerInvariant().Trim();
        if (!text.Equals(_lastPartialText, StringComparison.OrdinalIgnoreCase))
        {
            _lastPartialText = text;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Partial] \"{text}\"");
            Console.ResetColor();
        }

        // 2. В PartialResult проверяй вхождение ЛЮБОГО слова из списка WakeWords.
        if (!_wakeWordTriggered)
        {
            if (TryFindWakeWord(text, out string matchedName))
            {
                // НЕ вызывай recognizer.Reset()!
                // Запомни, какое именно имя сработало.
                // Установи _wakeWordTriggered = true, запусти Stopwatch _silenceTimer = Stopwatch.StartNew().
                // Флаг _soundPlayed = false.
                _wakeWordTriggered = true;
                _triggeredWakeWord = matchedName;
                _silenceTimer = Stopwatch.StartNew();
                _soundPlayed = false;
                _hasSpokenAfterWakeWord = false;

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Сквозное распознавание: зафиксировано имя '{_triggeredWakeWord}' в PartialResult.");
                Console.ResetColor();

                OnWakeWordDetected?.Invoke(_triggeredWakeWord);
            }
        }

        if (_wakeWordTriggered)
        {
            // Проверяем, говорит ли пользователь дальше после имени
            string cleaned = Regex.Replace(text, _wakeWordsRegexPattern, "", RegexOptions.IgnoreCase).Trim();

            // Если имя стояло не в начале, отсекаем его
            if (cleaned.Equals(text, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_triggeredWakeWord))
            {
                int idx = cleaned.IndexOf(_triggeredWakeWord, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    cleaned = cleaned[(idx + _triggeredWakeWord.Length)..].Trim();
                }
            }

            // FAST PATH: Если во фразе уже распознана быстрая команда, реагируем мгновенно
            if (!string.IsNullOrWhiteSpace(cleaned) && IsFastCommand(cleaned))
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] FAST PATH команда зафиксирована (мгновенно): '{cleaned}'");
                Console.ResetColor();

                _recognizer.Reset();
                ResetWakeWordState();

                OnCommandSpoken?.Invoke(cleaned);
                TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
                return;
            }

            // Проверяем, появились ли после вейк-ворда любые другие символы/слова
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                _hasSpokenAfterWakeWord = true;
            }
            else if (!string.IsNullOrWhiteSpace(_triggeredWakeWord))
            {
                int idx = text.IndexOf(_triggeredWakeWord, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string afterWakeWord = text[(idx + _triggeredWakeWord.Length)..].Trim();
                    if (!string.IsNullOrWhiteSpace(afterWakeWord))
                    {
                        _hasSpokenAfterWakeWord = true;
                    }
                }
            }

            // 3. Логика звукового сигнала (без перебивания слитной речи)
            CheckSilenceSoundTrigger(isAudioSpeech);

            // Silence fallback: если после имени тишина длится долго (>= 1100 мс) и пользователь молчит
            if (!_hasSpokenAfterWakeWord && _silenceTimer.ElapsedMilliseconds >= 1100)
            {
                string finalJson = _recognizer.FinalResult();
                string finalText = ExtractTextFromJson(finalJson, isFinal: true).Trim();
                ProcessWakeWordFinalResult(finalText);
            }
        }
    }

    private void CheckSilenceSoundTrigger(bool isAudioSpeech)
    {
        if (!_wakeWordTriggered || _soundPlayed)
        {
            return;
        }

        // Если в PartialResult после вейк-ворда уже появились любые другие символы/слова (пользователь начал говорить команду), звуковой сигнал воспроизводить ЗАПРЕЩЕНО.
        if (_hasSpokenAfterWakeWord)
        {
            return;
        }

        if (isAudioSpeech)
        {
            // Пользователь всё ещё говорит (завершает произносить имя или продолжает речь) -> обновляем таймер тишины
            _silenceTimer.Restart();
            return;
        }

        // Порог тишины для воспроизведения сигнала готовности: 550 мс
        if (_silenceTimer.ElapsedMilliseconds >= 550)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Пауза после имени ({_silenceTimer.ElapsedMilliseconds} мс >= 550 мс) — сигнал готовности (бесшумный режим).");
            Console.ResetColor();

            _soundPlayed = true;
        }
    }

    private void ProcessWakeWordFinalResult(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            if (_wakeWordTriggered)
            {
                if (!_soundPlayed && !_hasSpokenAfterWakeWord)
                {
                    _soundPlayed = true;
                }
                TransitionToListeningForCommand(_triggeredWakeWord, null);
            }
            return;
        }

        if (!_wakeWordTriggered)
        {
            if (TryFindWakeWord(input, out string matchedName))
            {
                _wakeWordTriggered = true;
                _triggeredWakeWord = matchedName;
            }
            else
            {
                return;
            }
        }

        // 4. Очистка имени при получении финального текста (isFinal / FinalResult):
        // Динамически сформируй регулярное выражение из списка WakeWords:
        var pattern = @"^(" + string.Join("|", _wakeWords.Select(Regex.Escape)) + @")\s*";
        var cleaned = Regex.Replace(input, pattern, "", RegexOptions.IgnoreCase).Trim();

        // Дополнительно: если имя было не в начале
        if (cleaned.Equals(input, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_triggeredWakeWord))
        {
            int idx = cleaned.IndexOf(_triggeredWakeWord, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[(idx + _triggeredWakeWord.Length)..].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            // Если cleaned не пустой (пользователь сказал "джемини установи игру" или "джарвис привет"):
            // Отправляй в обработку очищенный текст команды.
            // Возвращай состояние в ожидание вейк-ворда.
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Команда зафиксирована (очищена от имени): '{cleaned}'");
            Console.ResetColor();

            _recognizer?.Reset();
            ResetWakeWordState();

            OnCommandSpoken?.Invoke(cleaned);
            TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
        }
        else
        {
            // Если cleaned пустой (пользователь сказал только имя и молчит):
            // Переходи в режим явного ожидания команды, сыграй бип (если еще не играл) и слушай дальше.
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Произнесено только имя '{_triggeredWakeWord}'. Переход в режим явного ожидания команды...");
            Console.ResetColor();

            if (!_soundPlayed && !_hasSpokenAfterWakeWord)
            {
                _soundPlayed = true;
            }

            TransitionToListeningForCommand(_triggeredWakeWord, null);
        }
    }

    private void ResetWakeWordState()
    {
        _wakeWordTriggered = false;
        _triggeredWakeWord = string.Empty;
        _soundPlayed = false;
        _hasSpokenAfterWakeWord = false;
        _silenceTimer.Reset();
        _wakeWordDetector?.Reset();
    }

    /// <summary>
    /// Transitions into ListeningForCommand mode.
    /// Uses recognizer.Reset() to guarantee zero native memory churn and fast transition.
    /// </summary>
    private void TransitionToListeningForCommand(string wakeWord, string? pendingText)
    {
        _recognizer?.Reset();
        ResetWakeWordState();

        _pendingCommandText = pendingText ?? string.Empty;
        _sessionAccumulatedText = pendingText ?? string.Empty;
        _currentPartialText = string.Empty;
        _lastLoggedPartial = pendingText ?? string.Empty;
        _lastPartialText = pendingText ?? string.Empty;
        _voiceDetectedInCommand = !string.IsNullOrWhiteSpace(pendingText);
        _lastSpeechTime = DateTime.UtcNow;

        ChangeState(VoiceListenerState.ListeningForCommand, $"Wake-word '{wakeWord}' detected! Listening for command...");
    }

    /// <summary>
    /// Transitions back into WaitingForWakeWord mode.
    /// Uses recognizer.Reset() without reallocating native resources.
    /// </summary>
    public void TransitionToWaitingForWakeWord(string reason)
    {
        lock (_lock)
        {
            _recognizer?.Reset();
            ResetWakeWordState();

            _pendingCommandText = string.Empty;
            _sessionAccumulatedText = string.Empty;
            _currentPartialText = string.Empty;
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;

            ChangeState(VoiceListenerState.WaitingForWakeWord, reason);
        }
    }

    private void ProcessCommandListening(byte[] buffer, int bytesRecorded, bool isAudioSpeech)
    {
        var now = DateTime.UtcNow;

        // Небольшой защитный интервал (300 мс) после перехода, чтобы остаточный звук/бип от вейк-ворда не вызывал ложный таймаут тишины
        bool isGracePeriod = (now - _stateEnteredTime) < TimeSpan.FromMilliseconds(300);

        if (isAudioSpeech && !isGracePeriod)
        {
            _lastSpeechTime = now;
            _voiceDetectedInCommand = true;
        }

        // Таймаут ожидания начала речи: 10 секунд при активном диалоге подтверждения, иначе стандартный таймаут
        var waitTimeout = Gem.Services.JarvisOrchestrator.Instance.HasPendingAction
            ? TimeSpan.FromSeconds(10.0)
            : _commandWaitTimeout;

        if (!_voiceDetectedInCommand && (now - _stateEnteredTime) > waitTimeout)
        {
            TransitionToWaitingForWakeWord("Command timeout: No speech detected.");
            if (Gem.Services.JarvisOrchestrator.Instance.HasPendingAction)
            {
                Gem.Services.JarvisOrchestrator.Instance.ResetConfirmationState();
            }
            return;
        }

        bool isFinal = _recognizer!.AcceptWaveform(buffer, bytesRecorded);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string rawText = ExtractTextFromJson(json, isFinal).Trim();
        string cleanedText = CleanUpCommandText(rawText);

        if (!isFinal)
        {
            // Отслеживание изменений в VoskRecognizer.PartialResult()
            if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                _currentPartialText = cleanedText;
                _voiceDetectedInCommand = true;
                _lastSpeechTime = now;

                string currentFull = GetCurrentSessionText();
                _pendingCommandText = currentFull;

                if (!currentFull.Equals(_lastLoggedPartial, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLoggedPartial = currentFull;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Partial] \"{currentFull}\"");
                    Console.ResetColor();
                }
            }
        }
        else
        {
            // isFinal == true: Vosk зафиксировал завершение промежуточного речевого блока (например, пауза между словами).
            // ВАЖНО: Не вызываем recognizer.Reset() и не сбрасываем контекст сессии!
            // Накапливаем распознанные слова в рамках сессии и продолжаем слушать до таймаута тишины.
            if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                AppendSessionText(cleanedText);
                _currentPartialText = string.Empty;
                _voiceDetectedInCommand = true;
                _lastSpeechTime = now;
                _pendingCommandText = _sessionAccumulatedText;

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Chunk] '{cleanedText}' -> Накоплено: '{_sessionAccumulatedText}'");
                Console.ResetColor();
            }
        }

        // FAST PATH: Если во фразе уже распознана быстрая команда или короткий ответ на вопрос подтверждения
        string candidateCheck = GetCurrentSessionText();
        bool isFast = IsFastCommand(candidateCheck);
        bool isConfirmation = Gem.Services.JarvisOrchestrator.Instance.HasPendingAction &&
            (Gem.Services.JarvisOrchestrator.IsAffirmativeReply(candidateCheck) || Gem.Services.JarvisOrchestrator.IsNegativeReply(candidateCheck));

        if (!string.IsNullOrWhiteSpace(candidateCheck) && (isFast || isConfirmation))
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] FAST PATH {(isConfirmation ? "ответ подтверждения" : "команда")} зафиксирована (мгновенно): '{candidateCheck}'");
            Console.ResetColor();

            _recognizer.Reset();
            _pendingCommandText = string.Empty;
            _sessionAccumulatedText = string.Empty;
            _currentPartialText = string.Empty;
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;

            OnCommandSpoken?.Invoke(candidateCheck);
            TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
            return;
        }

        // Сессия распознавания завершается строго по таймауту естественной тишины (не менее 650–750 мс)
        TimeSpan silenceTimeout = Gem.Services.JarvisOrchestrator.Instance.HasPendingAction
            ? TimeSpan.FromMilliseconds(500)
            : _silenceTimeout;

        bool silenceTimeoutElapsed = _voiceDetectedInCommand && (now - _lastSpeechTime) >= silenceTimeout;
        bool maxDurationExceeded = _voiceDetectedInCommand && (now - _stateEnteredTime) >= TimeSpan.FromSeconds(15);

        if (silenceTimeoutElapsed || maxDurationExceeded)
        {
            // Фиксируем остаточный текст через FinalResult()
            string finalJson = _recognizer.FinalResult();
            string finalText = ExtractTextFromJson(finalJson, isFinal: true).Trim();
            string cleanedFinal = CleanUpCommandText(finalText);

            if (!string.IsNullOrWhiteSpace(cleanedFinal))
            {
                AppendSessionText(cleanedFinal);
            }
            else if (!string.IsNullOrWhiteSpace(_currentPartialText))
            {
                AppendSessionText(_currentPartialText);
            }

            string finalCandidate = CleanUpCommandText(_sessionAccumulatedText);
            if (string.IsNullOrWhiteSpace(finalCandidate))
            {
                finalCandidate = CleanUpCommandText(_pendingCommandText);
            }

            // Игнорируем случайные паразитные шумы и обрывки длиной менее 2 символов
            if (!string.IsNullOrWhiteSpace(finalCandidate) && finalCandidate.Length >= 2)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Final] '{finalCandidate}' (полная фраза, тишина {silenceTimeout.TotalMilliseconds:0} мс)");
                Console.ResetColor();

                OnCommandSpoken?.Invoke(finalCandidate);
            }
            else if (!string.IsNullOrWhiteSpace(finalCandidate))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Игнорирован паразитный шум / обрывок: '{finalCandidate}' (< 2 символов)");
                Console.ResetColor();
            }

            TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
        }
    }

    public string CleanUpCommandText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var pattern = @"^(" + string.Join("|", _wakeWords.Select(Regex.Escape)) + @")\s*";
        var cleaned = Regex.Replace(text.Trim(), pattern, "", RegexOptions.IgnoreCase).Trim();

        foreach (var wakeWord in _wakeWords)
        {
            int idx = cleaned.IndexOf(wakeWord, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[(idx + wakeWord.Length)..].Trim();
            }
        }
        return cleaned.Trim();
    }

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

    private static double CalculateRms(byte[] buffer, int bytesRecorded)
    {
        int sampleCount = bytesRecorded / 2;
        if (sampleCount == 0) return 0;

        double sumSquares = 0;
        for (int i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    private static string ExtractTextFromJson(string json, bool isFinal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string propName = isFinal ? "text" : "partial";

            if (root.TryGetProperty(propName, out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Ignore json parse hiccups on streaming chunks
        }
        return string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _wakeWordDetector?.Dispose();
        if (Instance == this)
        {
            Instance = null;
        }
        OnWakeWordDetected = null;
        OnCommandSpoken = null;
        OnStatusChanged = null;
    }
}
