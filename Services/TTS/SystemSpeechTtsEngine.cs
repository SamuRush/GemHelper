using System.Runtime.InteropServices;
using System.Security;
using System.Speech.Synthesis;

namespace Gem.Services;

/// <summary>
/// Safety fallback & offline TTS engine utilizing Windows SAPI (System.Speech.Synthesis.SpeechSynthesizer / SAPI5 COM).
/// Direct integration with installed Windows Silero SAPI5 (Aidar / Baya / Kseniya) and system voices (Microsoft Pavel / Microsoft Irina Desktop).
/// Full support for both male and female voices without forced pitch modification.
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

    public SystemSpeechTtsEngine(string? preferredVoice = null)
    {
        _synthesizer = new SpeechSynthesizer();
        var (voiceName, needPitchShift) = ConfigureVoice(_synthesizer, preferredVoice);
        SelectedVoiceName = voiceName;
        _forceLowPitch = needPitchShift;

        // Если выбран системный голос Silero (Aidar / Baya / Kseniya / Xenia и др.), привязать прямой зарегистрированный токен через SAPI COM
        if (voiceName != null && (voiceName.Contains("Aidar", StringComparison.OrdinalIgnoreCase) ||
                                  voiceName.Contains("Baya", StringComparison.OrdinalIgnoreCase) ||
                                  voiceName.Contains("Kseniya", StringComparison.OrdinalIgnoreCase) ||
                                  voiceName.Contains("Xenia", StringComparison.OrdinalIgnoreCase)))
        {
            _spVoice = TryBindSapiComVoiceToken(voiceName.Contains("Aidar", StringComparison.OrdinalIgnoreCase) ? "Aidar" :
                                                voiceName.Contains("Baya", StringComparison.OrdinalIgnoreCase) ? "Baya" :
                                                voiceName.Contains("Kseniya", StringComparison.OrdinalIgnoreCase) ? "Kseniya" :
                                                voiceName.Contains("Xenia", StringComparison.OrdinalIgnoreCase) ? "Xenia" : voiceName);
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
    /// Backward-compatible alias for ConfigureVoice.
    /// </summary>
    public static (string? voiceName, bool needPitchShift) ConfigureMaleRussianVoice(SpeechSynthesizer synthesizer, string? preferredVoice = null)
        => ConfigureVoice(synthesizer, preferredVoice);

    /// <summary>
    /// Configures SAPI5 voice with full support for both male and female Russian voices (Aidar, Baya, Pavel, Irina).
    /// If a female voice (e.g. Microsoft Irina Desktop) is selected, pitch shift is disabled.
    /// Priority order when voice is not specified: Aidar > Baya > Pavel > Irina / any Russian > any system voice.
    /// Logs all discovered SAPI5 voices at startup.
    /// </summary>
    public static (string? voiceName, bool needPitchShift) ConfigureVoice(SpeechSynthesizer synthesizer, string? preferredVoice = null)
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
                return (null, false);
            }

            // If a specific preferred voice was requested, try it first (male or female)
            if (!string.IsNullOrWhiteSpace(preferredVoice))
            {
                var preferred = installedVoices.FirstOrDefault(v =>
                    v.Enabled &&
                    v.VoiceInfo.Name.Contains(preferredVoice, StringComparison.OrdinalIgnoreCase));

                if (preferred != null)
                {
                    try
                    {
                        synthesizer.SelectVoice(preferred.VoiceInfo.Name);
                        synthesizer.Rate = 1;
                        synthesizer.Volume = 100;

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[TTS: SAPI5] Успешно активирован выбранный голос: '{preferred.VoiceInfo.Name}'.");
                        Console.ResetColor();

                        return (preferred.VoiceInfo.Name, false);
                    }
                    catch
                    {
                    }
                }

                // Check direct SAPI COM binding for preferred voice tokens (e.g. Silero tokens)
                var comVoice = TryBindSapiComVoiceToken(preferredVoice);
                if (comVoice != null)
                {
                    try { Marshal.ReleaseComObject(comVoice); } catch { }
                    return (preferredVoice, false);
                }
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
                (v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                 v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase)) &&
                (v.VoiceInfo.Gender == VoiceGender.Male || v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase)));

            // Priority 2: Any Russian voice with male hints or Pavel
            if (ruMaleVoice == null)
            {
                ruMaleVoice = installedVoices.FirstOrDefault(v =>
                    v.Enabled &&
                    (v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase) ||
                     v.VoiceInfo.Name.Contains("Male", StringComparison.OrdinalIgnoreCase)));
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
                    Console.WriteLine($"[TTS: SAPI5] Не удалось активировать голос '{ruMaleVoice.VoiceInfo.Name}': {ex.Message}.");
                    Console.ResetColor();
                }
            }

            // Priority 3: Russian female voice (e.g. Microsoft Irina Desktop) without pitch-shift
            var ruFemaleVoice = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                (v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                 v.VoiceInfo.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase)));

            if (ruFemaleVoice != null)
            {
                try
                {
                    synthesizer.SelectVoice(ruFemaleVoice.VoiceInfo.Name);
                    synthesizer.Rate = 1;
                    synthesizer.Volume = 100;

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[TTS: SAPI5] Успешно активирован женский голос: '{ruFemaleVoice.VoiceInfo.Name}'.");
                    Console.ResetColor();

                    return (ruFemaleVoice.VoiceInfo.Name, false);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TTS: SAPI5] Не удалось активировать голос '{ruFemaleVoice.VoiceInfo.Name}': {ex.Message}.");
                    Console.ResetColor();
                }
            }

            // Priority 4: Any installed voice in the system
            var anyVoice = installedVoices.FirstOrDefault(v => v.Enabled) ?? installedVoices.First();
            try
            {
                synthesizer.SelectVoice(anyVoice.VoiceInfo.Name);
                synthesizer.Rate = 1;
                synthesizer.Volume = 100;
                return (anyVoice.VoiceInfo.Name, false);
            }
            catch
            {
                return (synthesizer.Voice.Name, false);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS Warning] Ошибка инициализации голоса System.Speech: {ex.Message}");
            Console.ResetColor();
            return (null, false);
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
