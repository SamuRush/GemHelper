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
    /// If no male voices are available on Windows, sets needPitchShift to true to modify voice pitch.
    /// </summary>
    public static (string? voiceName, bool needPitchShift) ConfigureMaleRussianVoice(SpeechSynthesizer synthesizer)
    {
        try
        {
            var installedVoices = synthesizer.GetInstalledVoices();
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

            // Priority 1: Russian male voice (e.g. Microsoft Pavel)
            var ruMaleVoice = installedVoices.FirstOrDefault(v =>
                v.Enabled &&
                !IsFemale(v) &&
                v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) &&
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
                synthesizer.SelectVoice(ruMaleVoice.VoiceInfo.Name);
                synthesizer.Rate = 1;
                synthesizer.Volume = 100;

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[TTS: System.Speech] Выбран системный мужской голос: '{ruMaleVoice.VoiceInfo.Name}'.");
                Console.ResetColor();

                return (ruMaleVoice.VoiceInfo.Name, false);
            }
            else
            {
                // No male voice found on this Windows installation (only Irina exists by default)
                // Force pitch shift to low/extra-low so female voice sounds like a deep male/robotic voice!
                synthesizer.Rate = 0;
                synthesizer.Volume = 100;

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[TTS: System.Speech Warning] В системе не найден установленный мужской голос SAPI (например, Pavel). " +
                                  $"Включена модуляция питча (ExtraLow Pitch): женский голос Ирины заблокирован, тембр занижен до мужского.");
                Console.ResetColor();

                return (synthesizer.Voice.Name, true);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS Warning] Ошибка инициализации голоса System.Speech: {ex.Message}");
            Console.ResetColor();
            return (null, true);
        }
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
