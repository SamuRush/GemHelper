using System.Speech.Synthesis;

namespace Gem.Services;

/// <summary>
/// Safety fallback TTS engine utilizing Windows SAPI (System.Speech.Synthesis.SpeechSynthesizer).
/// Always available on Windows systems with Russian voice auto-selection.
/// </summary>
public sealed class SystemSpeechTtsEngine : ITtsEngine, IDisposable
{
    private readonly SpeechSynthesizer _synthesizer;
    private readonly object _sync = new();
    private bool _disposed = false;

    public string Name => "System.Speech";

    public bool IsAvailable => true;

    public string? SelectedVoiceName { get; private set; }

    public SystemSpeechTtsEngine()
    {
        _synthesizer = new SpeechSynthesizer();
        SelectedVoiceName = ConfigureRussianVoice(_synthesizer);
    }

    public static string? ConfigureRussianVoice(SpeechSynthesizer synthesizer)
    {
        try
        {
            var installedVoices = synthesizer.GetInstalledVoices();
            if (installedVoices.Count == 0)
            {
                return null;
            }

            // Priority 1: Culture matching "ru-RU"
            var ruVoice = installedVoices.FirstOrDefault(v =>
                v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase));

            // Priority 2: Voice name containing Irina, Pavel, Elena, or Russian
            if (ruVoice == null)
            {
                ruVoice = installedVoices.FirstOrDefault(v =>
                    v.Enabled && (
                        v.VoiceInfo.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Pavel", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Elena", StringComparison.OrdinalIgnoreCase) ||
                        v.VoiceInfo.Name.Contains("Russian", StringComparison.OrdinalIgnoreCase)));
            }

            if (ruVoice != null)
            {
                synthesizer.SelectVoice(ruVoice.VoiceInfo.Name);
                synthesizer.Rate = 1;
                synthesizer.Volume = 100;
                return ruVoice.VoiceInfo.Name;
            }
            else
            {
                return synthesizer.Voice.Name;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TTS] Ошибка инициализации голоса System.Speech: {ex.Message}");
            Console.ResetColor();
            return null;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        await Task.Run(() =>
        {
            lock (_sync)
            {
                _synthesizer.Speak(text);
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
