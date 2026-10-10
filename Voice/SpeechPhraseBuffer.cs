using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gem.Voice;

/// <summary>
/// Buffers and normalizes speech recognition phrases, handling partial/final chunk deduplication and fast commands.
/// </summary>
public sealed class SpeechPhraseBuffer
{
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

    private string _sessionAccumulatedText = string.Empty;
    private string _currentPartialText = string.Empty;

    public string SessionAccumulatedText => _sessionAccumulatedText;
    public string CurrentPartialText
    {
        get => _currentPartialText;
        set => _currentPartialText = value;
    }

    public void Reset(string initialText = "")
    {
        _sessionAccumulatedText = initialText;
        _currentPartialText = string.Empty;
    }

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
    /// Cleans wake words from spoken command text.
    /// </summary>
    public static string CleanUpCommandText(string text, IEnumerable<string> wakeWords)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var wordsList = wakeWords as IList<string> ?? wakeWords.ToList();
        var pattern = @"^(" + string.Join("|", wordsList.Select(Regex.Escape)) + @")\s*";
        var cleaned = Regex.Replace(text.Trim(), pattern, "", RegexOptions.IgnoreCase).Trim();

        foreach (var wakeWord in wordsList)
        {
            int idx = cleaned.IndexOf(wakeWord, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[(idx + wakeWord.Length)..].Trim();
            }
        }
        return cleaned.Trim();
    }

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
    /// Searches for a wake-word token in the spoken text.
    /// </summary>
    public static bool TryFindWakeWord(string text, IEnumerable<string> wakeWords, out string matchedWakeWord)
    {
        matchedWakeWord = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var words = text.Split(new[] { ' ', '\t', ',', '.', '!', '?', ':', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            string cleanWord = word.Trim();
            foreach (var trigger in wakeWords)
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

    /// <summary>
    /// Extracts text from Vosk JSON response string.
    /// </summary>
    public static string ExtractTextFromJson(string json, bool isFinal)
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
}
