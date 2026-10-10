using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Services.Routing;

/// <summary>
/// Fast-path recognition for weather and system hotkeys (task manager, desktop, screenshot, clipboard).
/// </summary>
public static class SystemFastMatcher
{
    public static JarvisResponse? MatchWeather(string lower)
    {
        if (lower.Contains("погода") || lower.Contains("погоду") || lower.Contains("погоде") ||
            lower.Contains("температур") || lower.Contains("на улице") || lower.Contains("дождь") || lower.Contains("снег"))
        {
            // If specific city is requested (e.g. "погода в москве", "погода в париже"), pass to LLM for precise normalization
            bool hasSpecificCity = Regex.IsMatch(lower, @"\b(?:в|во|для|по)\s+[а-яa-zё\-]+", RegexOptions.IgnoreCase);
            if (!hasSpecificCity)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "city": null }""");
                return new JarvisResponse(new CommandRequest("weather", args), null);
            }
        }

        return null;
    }

    public static JarvisResponse? MatchOfflineWeather(string lower)
    {
        if (lower.Contains("погода") || lower.Contains("погоду") || lower.Contains("погоде") || lower.Contains("температур"))
        {
            var match = Regex.Match(lower, @"\b(?:погода|погоду|погоде|температура|температуру|температуре)\s+(?:в|во)\s+([а-яa-zё\-]+)", RegexOptions.IgnoreCase);
            string? city = match.Success ? match.Groups[1].Value.Trim() : null;
            string cityJson = city != null ? $"\"{city}\"" : "null";
            var args = RoutingHelper.CreateFastMatchArgs($$"""{ "city": {{cityJson}} }""");
            return new JarvisResponse(new CommandRequest("weather", args), null);
        }

        return null;
    }

    public static JarvisResponse? MatchHotkeys(string lower)
    {
        if (lower.Contains("диспетчер задач"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "keys": ["ctrl", "shift", "esc"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Открываю диспетчер задач, сэр.");
        }

        if (lower.Contains("сверни все") || lower.Contains("свернуть все") || lower.Contains("рабочий стол"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "keys": ["win", "d"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Сворачиваю окна, сэр.");
        }

        if (lower.Contains("скриншот") || lower.Contains("снимок экрана"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "keys": ["win", "shift", "s"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Делаю снимок экрана, сэр.");
        }

        if (lower.Contains("пробел") || lower == "space")
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "key": "space" }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Клавиша пробел, сэр.");
        }

        if (lower.Contains("копируй") || lower.Contains("скопируй"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "keys": ["ctrl", "c"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Копирую в буфер, сэр.");
        }

        if (lower.Contains("вставь") || lower.Contains("вставить"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "keys": ["ctrl", "v"], "delayMs": 50 }""");
            return new JarvisResponse(new CommandRequest("hotkey", args), "Вставляю из буфера, сэр.");
        }

        return null;
    }
}
