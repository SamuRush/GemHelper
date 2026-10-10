using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Services.Routing;

/// <summary>
/// Fast-path recognition for system and app volume commands (mute, unmute, get, delta, set).
/// </summary>
public static class VolumeFastMatcher
{
    public static JarvisResponse? MatchVolumeCommand(string lower)
    {
        if (lower.Contains("громк") || lower.Contains("звук") || lower.Contains("тише") || lower.Contains("громче") ||
            lower.Contains("убавь") || lower.Contains("прибавь") || lower.StartsWith("volume") || lower.StartsWith("vol") || Regex.IsMatch(lower, @"^\s*\d{1,3}\s*%\s*$"))
        {
            bool isVideoVolume = lower.Contains("видео") || lower.Contains("ролик") || lower.Contains("фильм");
            bool isPlayerVolume = !isVideoVolume && (lower.Contains("музык") || lower.Contains("плеер") || lower.Contains("яндекс") || lower.Contains("трек"));
            string? target = isVideoVolume ? "video" : (isPlayerVolume ? "player" : null);
            string targetJson = target != null ? $""", "target": "{target}" """ : "";
            string targetName = isVideoVolume ? "видео " : (isPlayerVolume ? "плеера " : "");

            // Mute
            if (lower.Contains("выключи") || lower.Contains("без звука") || lower.Contains("мут") || lower.Contains("mute") || lower.Contains("заглуши"))
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "mute", "isMuted": true{{targetJson}} }""");
                string reply = target != null ? $"Звук {targetName}отключен, сэр." : "Звук отключен, сэр.";
                return new JarvisResponse(new CommandRequest("volume", args), reply);
            }

            // Unmute
            if (lower.Contains("включи") || lower.Contains("размут") || lower.Contains("unmute") || lower.Contains("верни звук"))
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "mute", "isMuted": false{{targetJson}} }""");
                string reply = target != null ? $"Звук {targetName}включен, сэр." : "Звук включен, сэр.";
                return new JarvisResponse(new CommandRequest("volume", args), reply);
            }

            // Status / Query
            if (lower.Contains("статус") || lower.Contains("какой") || lower.Contains("сколько") || lower == "звук" || lower == "громкость")
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "get"{{targetJson}} }""");
                string reply = target != null ? $"Проверяю громкость {targetName}, сэр." : "Проверяю громкость, сэр.";
                return new JarvisResponse(new CommandRequest("volume", args), reply);
            }

            // Relative increase
            if (lower.Contains("прибавь") || lower.Contains("громче") || lower.Contains("выше") || lower.Contains("добавь") || lower.Contains("увеличь") || lower.Contains("+"))
            {
                int delta = ParseVolumeNumber(lower) ?? 10;
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "change", "delta": {{delta}}{{targetJson}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость {targetName}увеличена на {delta}%, сэр.");
            }

            // Relative decrease
            if (lower.Contains("убавь") || lower.Contains("тише") || lower.Contains("меньше") || lower.Contains("снизь") || lower.Contains("уменьши") || lower.Contains("-"))
            {
                int delta = ParseVolumeNumber(lower) ?? 10;
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "change", "delta": {{-delta}}{{targetJson}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость {targetName}уменьшена на {delta}%, сэр.");
            }

            // Absolute set (e.g. "звук 60", "громкость 50", "звук на 70%", "звук шестьдесят", "звук максимум")
            int? level = ParseVolumeNumber(lower);
            if (level.HasValue)
            {
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "set", "level": {{level.Value}}{{targetJson}} }""");
                return new JarvisResponse(new CommandRequest("volume", args), $"Громкость {targetName}установлена на {level.Value}%, сэр.");
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a volume percentage number (0-100) from text, supporting digits,
    /// named words (максимум, минимум, половина), and Russian spoken numerals.
    /// </summary>
    public static int? ParseVolumeNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 1. Direct digits (e.g. "60", "60%")
        var match = Regex.Match(text, @"\b(\d{1,3})\b");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int num))
        {
            return Math.Clamp(num, 0, 100);
        }

        // 2. Semantic named levels
        if (text.Contains("максимум") || text.Contains("на всю") || text.Contains("полную") || text.Contains("сотку"))
        {
            return 100;
        }

        if (text.Contains("минимум") || text.Contains("ноль") || text.Contains("нуль"))
        {
            return 0;
        }

        if (text.Contains("половин"))
        {
            return 50;
        }

        // 3. Russian spoken number words (common for Vosk STT outputs)
        var words = text.Split(new[] { ' ', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
        int total = 0;
        bool foundAny = false;

        foreach (var rawWord in words)
        {
            string word = rawWord.Trim();
            int val = word switch
            {
                "один" or "единицу" => 1,
                "два" => 2,
                "три" => 3,
                "четыре" => 4,
                "пять" => 5,
                "шесть" => 6,
                "семь" => 7,
                "восемь" => 8,
                "девять" => 9,
                "десять" => 10,
                "одиннадцать" => 11,
                "двенадцать" => 12,
                "тринадцать" => 13,
                "четырнадцать" => 14,
                "пятнадцать" => 15,
                "шестнадцать" => 16,
                "семнадцать" => 17,
                "восемнадцать" => 18,
                "девятнадцать" => 19,
                "двадцать" => 20,
                "тридцать" => 30,
                "сорок" => 40,
                "пятьдесят" => 50,
                "шестьдесят" => 60,
                "семьдесят" => 70,
                "восемьдесят" => 80,
                "девяносто" => 90,
                "сто" => 100,
                _ => 0
            };

            if (val > 0)
            {
                total += val;
                foundAny = true;
            }
        }

        return foundAny ? Math.Clamp(total, 0, 100) : null;
    }
}
