using System.Diagnostics;
using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Voice;

public sealed partial class VoiceListener
{
    internal void SimulateAudioInput(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded == 0 || _isPaused || _isSpeaking || _isProcessing) return;

        lock (_lock)
        {
            if (_state == VoiceListenerState.Stopped || _isPaused || _isSpeaking || _isProcessing) return;

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

    internal void ProcessAudioChunkForTesting(byte[] buffer, int bytesRecorded, bool isSpeech = true)
    {
        lock (_lock)
        {
            if (_isPaused || _isSpeaking || _isProcessing) return;

            if (_state == VoiceListenerState.WaitingForWakeWord)
            {
                ProcessWakeWordListening(buffer, bytesRecorded, isSpeech);
            }
            else if (_state == VoiceListenerState.ListeningForCommand)
            {
                ProcessCommandListening(buffer, bytesRecorded, isSpeech);
            }
        }
    }

    private void ProcessWakeWordListening(byte[] buffer, int bytesRecorded, bool isAudioSpeech)
    {
        if (_wakeWordDetector.ProcessFrame(buffer.AsSpan(0, bytesRecorded)))
        {
            return;
        }

        if (_wakeWordDetector is OpenWakeWordDetector || _wakeWordDetector is not VoskGrammarWakeWordDetector)
        {
            return;
        }

        if (_recognizer == null) return;

        var voskSw = Stopwatch.StartNew();
        bool isFinal = _recognizer.AcceptWaveform(buffer, bytesRecorded);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string rawText = SpeechPhraseBuffer.ExtractTextFromJson(json, isFinal).Trim();
        voskSw.Stop();
        long voskLatencyMs = voskSw.ElapsedMilliseconds;

        if (isFinal)
        {
            ProcessWakeWordFinalResult(rawText, voskLatencyMs);
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText)) return;

        string text = rawText.ToLowerInvariant().Trim();
        if (!text.Equals(_lastPartialText, StringComparison.OrdinalIgnoreCase))
        {
            _lastPartialText = text;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Partial] \"{text}\"");
            Console.ResetColor();
        }

        if (!_wakeWordTriggered && TryFindWakeWord(text, out string matchedName))
        {
            _wakeWordTriggered = true;
            _triggeredWakeWord = matchedName;

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [WakeWord: VoskGrammar (\"{_triggeredWakeWord}\")] [Detection Latency: {voskLatencyMs} ms]");
            Console.ResetColor();

            OnWakeWordDetected?.Invoke(_triggeredWakeWord);

            string cleaned = Regex.Replace(text, _wakeWordsRegexPattern, "", RegexOptions.IgnoreCase).Trim();
            if (cleaned.Equals(text, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_triggeredWakeWord))
            {
                int idx = cleaned.IndexOf(_triggeredWakeWord, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    cleaned = cleaned[(idx + _triggeredWakeWord.Length)..].Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(cleaned) && IsFastCommand(cleaned))
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] FAST PATH команда зафиксирована (мгновенно): '{cleaned}'");
                Console.ResetColor();

                _recognizer.Reset();
                ResetWakeWordState();

                NotifyProcessingStarted();
                OnCommandSpoken?.Invoke(cleaned);
                TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
                return;
            }

            TransitionToListeningForCommand(_triggeredWakeWord, string.IsNullOrWhiteSpace(cleaned) ? null : cleaned);
        }
    }

    private void ProcessWakeWordFinalResult(string input, long latencyMs = 0)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            if (_wakeWordTriggered)
            {
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

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [WakeWord: VoskGrammar (\"{_triggeredWakeWord}\")] [Detection Latency: {latencyMs} ms]");
                Console.ResetColor();

                OnWakeWordDetected?.Invoke(_triggeredWakeWord);
            }
            else
            {
                return;
            }
        }

        var pattern = @"^(" + string.Join("|", _wakeWords.Select(Regex.Escape)) + @")\s*";
        var cleaned = Regex.Replace(input, pattern, "", RegexOptions.IgnoreCase).Trim();

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
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Команда зафиксирована (очищена от имени): '{cleaned}'");
            Console.ResetColor();

            _recognizer?.Reset();
            ResetWakeWordState();

            NotifyProcessingStarted();
            OnCommandSpoken?.Invoke(cleaned);
            TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VoiceListener] Произнесено только имя '{_triggeredWakeWord}'. Мгновенный переход (0 мс) в режим явного ожидания команды...");
            Console.ResetColor();

            TransitionToListeningForCommand(_triggeredWakeWord, null);
        }
    }

    private void ProcessCommandListening(byte[] buffer, int bytesRecorded, bool isAudioSpeech)
    {
        var now = DateTime.UtcNow;
        bool isGracePeriod = (now - _stateEnteredTime) < TimeSpan.FromMilliseconds(300);

        if (isAudioSpeech && !isGracePeriod)
        {
            _lastSpeechTime = now;
            _voiceDetectedInCommand = true;
        }

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
        string rawText = SpeechPhraseBuffer.ExtractTextFromJson(json, isFinal).Trim();
        string cleanedText = CleanUpCommandText(rawText);

        if (!isFinal)
        {
            if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                _phraseBuffer.CurrentPartialText = cleanedText;
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
                OnPartialResult?.Invoke(currentFull);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                AppendSessionText(cleanedText);
                _phraseBuffer.CurrentPartialText = string.Empty;
                _voiceDetectedInCommand = true;
                _lastSpeechTime = now;
                _pendingCommandText = _phraseBuffer.SessionAccumulatedText;

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Chunk] '{cleanedText}' -> Накоплено: '{_phraseBuffer.SessionAccumulatedText}'");
                Console.ResetColor();
            }
        }

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
            _phraseBuffer.Reset();
            _lastLoggedPartial = string.Empty;
            _lastPartialText = string.Empty;
            _voiceDetectedInCommand = false;

            NotifyProcessingStarted();
            OnCommandSpoken?.Invoke(candidateCheck);
            TransitionToWaitingForWakeWord("Ready. Listening for wake-word...");
            return;
        }

        TimeSpan silenceTimeout = Gem.Services.JarvisOrchestrator.Instance.HasPendingAction
            ? TimeSpan.FromMilliseconds(500)
            : _silenceTimeout;

        bool silenceTimeoutElapsed = _voiceDetectedInCommand && (now - _lastSpeechTime) >= silenceTimeout;
        bool maxDurationExceeded = _voiceDetectedInCommand && (now - _stateEnteredTime) >= TimeSpan.FromSeconds(15);

        if (silenceTimeoutElapsed || maxDurationExceeded)
        {
            string finalJson = _recognizer.FinalResult();
            string finalText = SpeechPhraseBuffer.ExtractTextFromJson(finalJson, isFinal: true).Trim();
            string cleanedFinal = CleanUpCommandText(finalText);

            if (!string.IsNullOrWhiteSpace(cleanedFinal))
            {
                AppendSessionText(cleanedFinal);
            }
            else if (!string.IsNullOrWhiteSpace(_phraseBuffer.CurrentPartialText))
            {
                AppendSessionText(_phraseBuffer.CurrentPartialText);
            }

            string finalCandidate = CleanUpCommandText(_phraseBuffer.SessionAccumulatedText);
            if (string.IsNullOrWhiteSpace(finalCandidate))
            {
                finalCandidate = CleanUpCommandText(_pendingCommandText);
            }

            if (!string.IsNullOrWhiteSpace(finalCandidate) && finalCandidate.Length >= 2)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [STT: Vosk Final] '{finalCandidate}' (полная фраза, тишина {silenceTimeout.TotalMilliseconds:0} мс)");
                Console.ResetColor();

                NotifyProcessingStarted();
                OnFinalResult?.Invoke(finalCandidate);
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
}
