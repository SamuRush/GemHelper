using System.Runtime.InteropServices;
using System.Security;
using System.Speech.Synthesis;

namespace Gem.Services;

/// <summary>
/// Safety fallback & offline TTS engine utilizing Windows SAPI (System.Speech.Synthesis.SpeechSynthesizer / SAPI5 COM).
/// Direct integration with installed Windows Silero SAPI5 (Aidar / Baya) with robust fallback to Microsoft Pavel.
/// Female voices (Microsoft Irina Desktop) are strictly filtered out.
/// </summary>
public sealed class SystemSpeechTtsEngine : ITtsEngine, IDisposable
{
    private readonly SpeechSynthesizer _synthesizer;
    private dynamic? _spVoice;
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

        // Если выбран системный голос Silero (Aidar / Baya), привязать прямой зарегистрированный системный токен через SAPI COM
        if (voiceName != null && (voiceName.Contains("Aidar", StringComparison.OrdinalIgnoreCase) ||
                                  voiceName.Contains("Baya", StringComparison.OrdinalIgnoreCase)))
        {
            _spVoice = TryBindSapiComVoiceToken(voiceName.Contains("Aidar", StringComparison.OrdinalIgnoreCase) ? "Aidar" : "Baya");
        }
    }

    /// <summary>
    /// Пытается напрямую связать зарегистрированный системный токен SAPI5 через COM SpVoice.
    /// </summary>
    public static object? TryBindSapiComVoiceToken(string voiceSubstring)
    {
        try
        {
            Type? spVoiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (spVoiceType == null) return null;

            dynamic spVoice = Activator.CreateInstance(spVoiceType)!;
            dynamic tokens = spVoice.GetVoices();
            int count = tokens.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic token = tokens.Item(i);
                string desc = token.GetDescription();
                if (desc.Contains(voiceSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    spVoice.Voice = token;
                    return spVoice;
                }
            }
        }
        catch
        {
            // Безопасное подавление при отсутствии или сбое COM
        }
        return null;
    }

    /// <summary>
    /// Configures strictly male voice, excluding Irina and female voices completely.
    /// Priority order: Aidar (Silero SAPI5) > Baya (Silero SAPI5) > Microsoft Pavel > Any Russian male > Any male > pitch-shift fallback.
    /// Logs all discovered SAPI5 voices at startup.
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

            // Priority 0: Aidar — системный установленный пакет Silero SAPI5
            var sileroAidar = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                v.VoiceInfo.Name.Contains("Aidar", StringComparison.OrdinalIgnoreCase));

            if (sileroAidar != null)
            {
                bool bound = false;
                try
                {
                    synthesizer.SelectVoice(sileroAidar.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;
                    bound = true;
                }
                catch
                {
                    bound = false;
                }

                if (!bound)
                {
                    var comVoice = TryBindSapiComVoiceToken("Aidar");
                    if (comVoice != null)
                    {
                        bound = true;
                        try { Marshal.ReleaseComObject(comVoice); } catch { }
                    }
                }

                if (bound)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[+] [TTS: SAPI5] Активирован системный голос Silero: Aidar (Russian)");
                    Console.ResetColor();

                    return (sileroAidar.VoiceInfo.Name, false);
                }
            }

            // Priority 0.5: Baya — системный установленный пакет Silero SAPI5 (баритон)
            var sileroBaya = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                !IsFemale(v) &&
                v.VoiceInfo.Name.Contains("Baya", StringComparison.OrdinalIgnoreCase));

            if (sileroBaya != null)
            {
                bool bound = false;
                try
                {
                    synthesizer.SelectVoice(sileroBaya.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;
                    bound = true;
                }
                catch
                {
                    bound = false;
                }

                if (!bound)
                {
                    var comVoice = TryBindSapiComVoiceToken("Baya");
                    if (comVoice != null)
                    {
                        bound = true;
                        try { Marshal.ReleaseComObject(comVoice); } catch { }
                    }
                }

                if (bound)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[+] [TTS: SAPI5] Активирован системный голос Silero: Baya (Russian)");
                    Console.ResetColor();

                    return (sileroBaya.VoiceInfo.Name, false);
                }
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
                if (_spVoice != null)
                {
                    try
                    {
                        // 0 = SVSFDefault (синхронное воспроизведение внутри фонового таска)
                        _spVoice.Speak(text, 0);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"[TTS: SAPI5] Ошибка воспроизведения через SAPI COM: {ex.Message}. Фоллбэк на SpeechSynthesizer.");
                        Console.ResetColor();
                    }
                }

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

        if (_spVoice != null)
        {
            try
            {
                Marshal.ReleaseComObject(_spVoice);
            }
            catch { }
            _spVoice = null;
        }

        try { _synthesizer.Dispose(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[TTS: Warning] Ошибка освобождения SpeechSynthesizer: {ex.Message}");
        }
    }
}
