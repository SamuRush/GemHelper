using System.Security;
using System.Speech.Synthesis;

namespace Gem.Services;

/// <summary>
/// Safety fallback TTS engine utilizing Windows SAPI (System.Speech.Synthesis.SpeechSynthesizer).
/// Guarantees strictly male voice (e.g. Microsoft Pavel) or forces extra-low pitch modification
/// so that female voices (Microsoft Irina Desktop) never sound under any circumstances.
/// </summary>
public sealed class SystemSpeechTtsEngine : ITtsEngine, IDisposable
{
    private readonly SpeechSynthesizer _synthesizer;
    private readonly object _sync = new();
    private bool _disposed = false;
    private readonly bool _forceLowPitch = false;

    public string Name => "System.Speech";

    public bool IsAvailable => true;

    public string? SelectedVoiceName { get; private set; }

    public bool IsPitchShiftedToMale => _forceLowPitch;

    public SystemSpeechTtsEngine()
    {
        _synthesizer = new SpeechSynthesizer();
        var (voiceName, needPitchShift) = ConfigureMaleRussianVoice(_synthesizer);
        SelectedVoiceName = voiceName;
        _forceLowPitch = needPitchShift;
    }

    /// <summary>
    /// Configures strictly male voice, excluding Irina and female voices completely.
    /// Priority order: Aidar (SAPI5) > Baya (SAPI5) > Microsoft Pavel > Any Russian male > Any male > pitch-shift fallback.
    /// Logs all discovered SAPI5 voices at startup. Each SelectVoice call is individually guarded by try/catch.
    /// </summary>
    public static (string? voiceName, bool needPitchShift) ConfigureMaleRussianVoice(SpeechSynthesizer synthesizer)
    {
        try
        {
            var installedVoices = synthesizer.GetInstalledVoices();

            // Вывести в лог все обнаруженные в системе голоса SAPI5
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            foreach (var voice in installedVoices)
            {
                Console.WriteLine($"[TTS: SAPI5] Обнаружен голос: {voice.VoiceInfo.Name} ({voice.VoiceInfo.Culture})");
            }
            Console.ResetColor();

            if (installedVoices.Count == 0)
            {
                return (null, true);
            }

            // Strictly filter out female voices (Irina, Elena, etc.)
            bool IsFemale(InstalledVoice v)
            {
                string name = v.VoiceInfo.Name;
                return v.VoiceInfo.Gender == VoiceGender.Female ||
                       name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Elena", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Zira", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Hazel", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Susan", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Hana", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Female", StringComparison.OrdinalIgnoreCase);
            }

            // Priority 0: Aidar — любое SAPI5-имя, содержащее "Aidar" (напр. "Aidar (Russian)")
            var sileroAidar = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                v.VoiceInfo.Name.Contains("Aidar", StringComparison.OrdinalIgnoreCase));

            if (sileroAidar != null)
            {
                try
                {
                    synthesizer.SelectVoice(sileroAidar.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[TTS: SAPI5] Успешно активирован голос: '{sileroAidar.VoiceInfo.Name}'.");
                    Console.ResetColor();

                    return (sileroAidar.VoiceInfo.Name, false);
                }
                catch (Exception)
                {
                    // 32-bit SAPI voice token in 64-bit process context (e.g. Silero Aidar installed in WOW6432Node)
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"[TTS: SAPI5 Info] Голос '{sileroAidar.VoiceInfo.Name}' (32-bit SAPI) недоступен для x64 контекста. Переключение на системный мужской голос Microsoft Pavel.");
                    Console.ResetColor();
                }
            }
            else if (Has32BitVoiceToken("Aidar"))
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[TTS: SAPI5 Info] Голос 'Aidar (Russian)' (32-bit SAPI) недоступен для x64 контекста. Переключение на системный мужской голос Microsoft Pavel.");
                Console.ResetColor();
            }

            // Priority 0.5: Baya — любое SAPI5-имя, содержащее "Baya" (мужской баритон)
            var sileroBaya = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                !IsFemale(v) &&
                v.VoiceInfo.Name.Contains("Baya", StringComparison.OrdinalIgnoreCase));

            if (sileroBaya != null)
            {
                try
                {
                    synthesizer.SelectVoice(sileroBaya.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[TTS: SAPI5] Успешно активирован голос: '{sileroBaya.VoiceInfo.Name}'.");
                    Console.ResetColor();

                    return (sileroBaya.VoiceInfo.Name, false);
                }
                catch (Exception)
                {
                    // 32-bit SAPI voice token in 64-bit process context (e.g. Silero Baya installed in WOW6432Node)
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"[TTS: SAPI5 Info] Голос '{sileroBaya.VoiceInfo.Name}' (32-bit SAPI) недоступен для x64 контекста. Переключение на системный мужской голос Microsoft Pavel.");
                    Console.ResetColor();
                }
            }
            else if (Has32BitVoiceToken("Baya"))
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[TTS: SAPI5 Info] Голос 'Baya (Russian)' (32-bit SAPI) недоступен для x64 контекста. Переключение на системный мужской голос Microsoft Pavel.");
                Console.ResetColor();
            }

            // Priority 1: Russian male voice (e.g. Microsoft Pavel)
            var ruMaleVoice = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                !IsFemale(v) &&
                (v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                 v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase)) &&
                (v.VoiceInfo.Gender == VoiceGender.Male || v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase)));

            // Priority 2: Any Russian voice with male hints or Pavel
            if (ruMaleVoice == null)
            {
                ruMaleVoice = installedVoices.FirstOrDefault(v =>
                    v.Enabled &&
                    !IsFemale(v) &&
                    (v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase) ||
                     v.VoiceInfo.Name.Contains("Male", StringComparison.OrdinalIgnoreCase)));
            }

            // Priority 3: Any installed male voice in the system (e.g. Microsoft David)
            if (ruMaleVoice == null)
            {
                ruMaleVoice = installedVoices.FirstOrDefault(v =>
                    v.Enabled &&
                    !IsFemale(v) &&
                    (v.VoiceInfo.Gender == VoiceGender.Male ||
                     v.VoiceInfo.Name.Contains("David", StringComparison.OrdinalIgnoreCase) ||
                     v.VoiceInfo.Name.Contains("Mark", StringComparison.OrdinalIgnoreCase) ||
                     v.VoiceInfo.Name.Contains("George", StringComparison.OrdinalIgnoreCase)));
            }

            if (ruMaleVoice != null)
            {
                try
                {
                    synthesizer.SelectVoice(ruMaleVoice.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[TTS: SAPI5] Успешно активирован голос: '{ruMaleVoice.VoiceInfo.Name}'.");
                    Console.ResetColor();

                    return (ruMaleVoice.VoiceInfo.Name, false);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: SAPI5] Не удалось активировать голос '{ruMaleVoice.VoiceInfo.Name}': {ex.Message}. Переход к pitch-shift fallback.");
                    Console.ResetColor();
                }
            }

            // No male voice found or all SelectVoice calls failed — force pitch shift
            synthesizer.Rate = 0;
            synthesizer.Volume = 100;

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS: System.Speech Warning] В системе не найден установленный мужской голос SAPI5 (Aidar, Baya, Pavel). " +
                              $"Включена модуляция питча (ExtraLow Pitch): женский голос Ирины заблокирован, тембр занижен до мужского.");
            Console.ResetColor();

            return (synthesizer.Voice.Name, true);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS Warning] Ошибка инициализации голоса System.Speech: {ex.Message}");
            Console.ResetColor();
            return (null, true);
        }
    }

    /// <summary>
    /// Checks whether a voice token is registered in the 32-bit Windows SAPI registry (WOW6432Node).
    /// </summary>
    public static bool Has32BitVoiceToken(string voiceSubstring)
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var tokensKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Speech\Voices\Tokens");
            if (tokensKey != null)
            {
                foreach (var subKeyName in tokensKey.GetSubKeyNames())
                {
                    if (subKeyName.Contains(voiceSubstring, StringComparison.OrdinalIgnoreCase))
                        return true;
                    using var tokenKey = tokensKey.OpenSubKey(subKeyName);
                    string? val = tokenKey?.GetValue(null)?.ToString();
                    if (val != null && val.Contains(voiceSubstring, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // Suppress any registry access restrictions
        }
        return false;
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (_forceLowPitch)
                {
                    // Render through SSML with deep pitch reduction (-40%) or PromptBuilder to guarantee male pitch
                    try
                    {
                        string escaped = SecurityElement.Escape(text);
                        string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'><prosody pitch='-40%' rate='0%'>{escaped}</prosody></speak>";
                        _synthesizer.SpeakSsml(ssml);
                    }
                    catch
                    {
                        var prompt = new PromptBuilder();
                        var style = new PromptStyle
                        {
                            Rate = PromptRate.Slow,
                            Emphasis = PromptEmphasis.Strong
                        };
                        prompt.StartStyle(style);
                        prompt.AppendText(text);
                        prompt.EndStyle();
                        _synthesizer.Speak(prompt);
                    }
                }
                else
                {
                    _synthesizer.Speak(text);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _synthesizer.Dispose(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[TTS: Warning] Ошибка освобождения SpeechSynthesizer: {ex.Message}");
        }
    }
}
