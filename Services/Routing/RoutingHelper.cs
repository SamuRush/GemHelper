using System.Text.Json;

namespace Gem.Services.Routing;

/// <summary>
/// Helper utilities for fast-match routing, argument creation, and prefix stripping.
/// </summary>
public static class RoutingHelper
{
    private static readonly string[] WakeWordPrefixes = ["джарвис", "джар", "jarvis"];

    /// <summary>
    /// Cleans incoming text from wake-word prefixes ("джарвис", "джар", "jarvis").
    /// </summary>
    public static string CleanPrefixes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = text.Trim();
        bool matched;
        do
        {
            matched = false;
            foreach (var prefix in WakeWordPrefixes)
            {
                if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure prefix is a whole word or followed by non-letter
                    if (cleaned.Length == prefix.Length || !char.IsLetter(cleaned[prefix.Length]))
                    {
                        cleaned = cleaned[prefix.Length..].Trim().TrimStart(',', ':', '-', '!', '?').Trim();
                        matched = true;
                    }
                }
            }
        } while (matched);

        return cleaned;
    }

    /// <summary>
    /// Safely parses JSON and clones RootElement while immediately disposing the JsonDocument.
    /// Prevents internal ArrayPool rented buffer leaks from un-disposed JsonDocument instances.
    /// </summary>
    public static JsonElement CreateFastMatchArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
