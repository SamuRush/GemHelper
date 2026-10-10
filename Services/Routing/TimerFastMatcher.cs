using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Services.Routing;

/// <summary>
/// Fast-path recognition for timer and reminder commands (0ms latency, zero hallucinations).
/// </summary>
public static class TimerFastMatcher
{
    private static readonly Regex PoliteWordsRegex = new(@"\b(пожалуйста|мне|быстро|сейчас|давай)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex CancelRegex = new(@"^(?:отмени|отменить|сбрось|сбросить|выключи|выключить|останови|остановить|удали|удалить)\s+(?:все\s+)?таймер[ыа]?$", RegexOptions.Compiled);
    private static readonly Regex StatusRegex = new(@"^(?:сколько осталось(?:\s+времени)?(?:\s+до конца(?:\s+таймера)?)?|статус таймера|что с таймером|что там с таймером|сколько на таймере)$", RegexOptions.Compiled);
    private static readonly Regex RemindPrefixRegex = new(@"^(?:напомни(?: мне)?|напомнить(?: мне)?|напоминание)\s+(?:через\s+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimerPrefix1Regex = new(@"^(?:(?:поставь|заведи|включи|установи|запусти)\s+)?(?:таймер\s+)?на\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimerPrefix2Regex = new(@"^(?:поставь|заведи|включи|установи|запусти)\s+таймер\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeIndicatorRegex = new(@"^(?:полчаса|полтора|пол\s+часа|\d+|один|одну|одна|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|минут|секунд|час)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemindRemainderRegex = new(@"^(?:,|чтобы|что|надо|нужно|–|-)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimerRemainderRegex = new(@"^(?:на|для|про|–|-)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HalfHoursRegex = new(@"^полтора\s+час(?:а|ов|ика)?(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HalfHourRegex = new(@"^(?:полчаса|пол\s+часа|полчасика|пол\s+часика)(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FracHourRegex = new(@"^(\d+[.,]\d+)\s*час(?:а|ов)?(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FracMinRegex = new(@"^(\d+[.,]\d+)\s*мин(?:ут|уты|уту|ута)?(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HoursRegex = new(@"^(?:(?<val>\d+|один|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять)\s+)?(?:ч|час|часа|часов|часика|часик)(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinutesRegex = new(@"^(?:(?<val>\d+|одну|одна|один|две|два|три|четыре|пять|шесть|семь|восемь|девять|десять|пятнадцать|двадцать|тридцать|сорок|пятьдесят)\s+)?(?:мин|минут|минуты|минуту|минута|минутки|минутку)(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecondsRegex = new(@"^(?:(?<val>\d+|одну|одна|один|две|два|три|четыре|пять|шесть|семь|восемь|девять|десять|пятнадцать|двадцать|тридцать|сорок|пятьдесят)\s+)?(?:сек|секунд|секунды|секунду|секунда|секундочку|секундка)(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static JarvisResponse? MatchTimerCommand(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
        {
            return null;
        }

        string text = RoutingHelper.CleanPrefixes(lower).Trim();
        // Strip common polite and filler words (e.g. "пожалуйста", "мне", "быстро", "сейчас", "давай")
        text = PoliteWordsRegex.Replace(text, "").Trim();
        text = SpacesRegex.Replace(text, " ").Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // 1. Cancel Timer Category:
        // Patterns: "отмени таймер", "сбрось таймер", "выключи таймер", "отмени все таймеры", etc.
        if (IsTimerCancelCommand(text))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "cancel" }""");
            return new JarvisResponse(new CommandRequest("timer", args), "Таймер отменен, сэр.");
        }

        // 2. Status Timer Category:
        // Patterns: "сколько осталось", "статус таймера", "что с таймером", etc.
        if (IsTimerStatusCommand(text))
        {
            var activeTimers = TimerService.Instance?.GetActiveTimers();
            string reply;
            if (activeTimers != null && activeTimers.Count > 0)
            {
                var remaining = activeTimers[0].Remaining;
                int totalSec = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
                int m = totalSec / 60;
                int s = totalSec % 60;
                reply = $"Осталось {m} минут {s} секунд, сэр.";
            }
            else
            {
                reply = "Нет активных таймеров, сэр.";
            }

            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "status" }""");
            return new JarvisResponse(new CommandRequest("timer", args), reply);
        }

        // 3. Set Timer / Reminder Category:
        if (TryParseTimerSetCommand(text, out TimeSpan duration, out string label, out string timeDisplay))
        {
            int totalSeconds = (int)Math.Ceiling(duration.TotalSeconds);
            if (totalSeconds > 0)
            {
                string jsonLabel = JsonSerializer.Serialize(label);
                var args = RoutingHelper.CreateFastMatchArgs($$"""{ "action": "set", "seconds": {{totalSeconds}}, "label": {{jsonLabel}} }""");
                string reply = $"Таймер на {timeDisplay} установлен, сэр.";
                return new JarvisResponse(new CommandRequest("timer", args), reply);
            }
        }

        return null;
    }

    public static bool IsTimerCancelCommand(string text)
    {
        return text == "отмени таймер" || text == "отменить таймер" ||
               text == "сбрось таймер" || text == "сбросить таймер" ||
               text == "выключи таймер" || text == "выключить таймер" ||
               text == "останови таймер" || text == "остановить таймер" ||
               text == "удали таймер" || text == "удалить таймер" ||
               text == "отмена таймера" ||
               text == "отмени все таймеры" || text == "сбрось все таймеры" ||
               text == "отмени таймеры" || text == "сбрось таймеры" ||
               text == "выключи все таймеры" || text == "выключи таймеры" ||
               CancelRegex.IsMatch(text);
    }

    public static bool IsTimerStatusCommand(string text)
    {
        return text == "сколько осталось" || text == "статус таймера" ||
               text == "что с таймером" || text == "сколько на таймере" ||
               text == "что там с таймером" || text == "сколько осталось времени" ||
               text == "сколько осталось до конца таймера" || text == "таймер статус" ||
               StatusRegex.IsMatch(text);
    }

    public static bool TryParseTimerSetCommand(string text, out TimeSpan duration, out string label, out string timeDisplay)
    {
        duration = TimeSpan.Zero;
        label = "таймер";
        timeDisplay = string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return false;

        string work = text.Trim();

        // Check if reminder format: "напомни через ...", "напомни мне через ..."
        bool isReminder = false;
        var mRemind = RemindPrefixRegex.Match(work);
        if (mRemind.Success)
        {
            isReminder = true;
            work = work[mRemind.Length..].Trim();
        }
        else
        {
            // Standard timer prefixes:
            var mPrefix = TimerPrefix1Regex.Match(work);
            if (mPrefix.Success)
            {
                work = work[mPrefix.Length..].Trim();
            }
            else
            {
                var mPrefix2 = TimerPrefix2Regex.Match(work);
                if (mPrefix2.Success)
                {
                    work = work[mPrefix2.Length..].Trim();
                }
                else if (work.StartsWith("таймер ", StringComparison.OrdinalIgnoreCase))
                {
                    work = work[7..].Trim();
                }
                else if (work.StartsWith("через ", StringComparison.OrdinalIgnoreCase))
                {
                    work = work[6..].Trim();
                }
                else
                {
                    // To avoid capturing arbitrary non-timer commands, require time indicator
                    if (!TimeIndicatorRegex.IsMatch(work))
                    {
                        return false;
                    }
                }
            }
        }

        // Now parse duration and remainder from 'work'
        if (!ExtractDurationAndRemainder(work, out duration, out timeDisplay, out string remainder))
        {
            return false;
        }

        // Extract label from remainder
        if (isReminder)
        {
            string cleanRemainder = RemindRemainderRegex.Replace(remainder, "").Trim();
            if (!string.IsNullOrWhiteSpace(cleanRemainder))
            {
                label = cleanRemainder;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(remainder))
            {
                string cleanRemainder = TimerRemainderRegex.Replace(remainder, "").Trim();
                if (!string.IsNullOrWhiteSpace(cleanRemainder))
                {
                    label = cleanRemainder;
                }
            }
        }

        return true;
    }

    public static bool ExtractDurationAndRemainder(string s, out TimeSpan duration, out string timeDisplay, out string remainder)
    {
        duration = TimeSpan.Zero;
        timeDisplay = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();

        // 1. "полтора часа"
        var mHalfHours = HalfHoursRegex.Match(s);
        if (mHalfHours.Success)
        {
            duration = TimeSpan.FromMinutes(90);
            timeDisplay = "полтора часа";
            remainder = s[mHalfHours.Length..].Trim();
            return true;
        }

        // 2. "полчаса" / "пол часа"
        var mHalfHour = HalfHourRegex.Match(s);
        if (mHalfHour.Success)
        {
            duration = TimeSpan.FromMinutes(30);
            timeDisplay = "полчаса";
            remainder = s[mHalfHour.Length..].Trim();
            return true;
        }

        // 3. Fractional hours (e.g. 1.5 часа, 0.5 часа, 2,5 часа)
        var mFracHour = FracHourRegex.Match(s);
        if (mFracHour.Success && double.TryParse(mFracHour.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fracHours))
        {
            duration = TimeSpan.FromHours(fracHours);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = s[mFracHour.Length..].Trim();
            return true;
        }

        // 4. Fractional minutes (e.g. 0.5 минут, 1.5 минуты)
        var mFracMin = FracMinRegex.Match(s);
        if (mFracMin.Success && double.TryParse(mFracMin.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fracMins))
        {
            duration = TimeSpan.FromMinutes(fracMins);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = s[mFracMin.Length..].Trim();
            return true;
        }

        // 5. Compound / sequential scan for hours, minutes, seconds:
        int totalSeconds = 0;
        bool foundAnyUnit = false;
        string current = s;

        // 5A. Check Hours
        var mH = HoursRegex.Match(current);
        if (mH.Success)
        {
            int h = mH.Groups["val"].Success ? (ParseTimerNumeral(mH.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += h * 3600;
            foundAnyUnit = true;
            current = current[mH.Length..].Trim();
        }

        // 5B. Check Minutes
        var mM = MinutesRegex.Match(current);
        if (mM.Success)
        {
            int m = mM.Groups["val"].Success ? (ParseTimerNumeral(mM.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += m * 60;
            foundAnyUnit = true;
            current = current[mM.Length..].Trim();
        }

        // 5C. Check Seconds
        var mS = SecondsRegex.Match(current);
        if (mS.Success)
        {
            int sec = mS.Groups["val"].Success ? (ParseTimerNumeral(mS.Groups["val"].Value) ?? 1) : 1;
            totalSeconds += sec;
            foundAnyUnit = true;
            current = current[mS.Length..].Trim();
        }

        if (foundAnyUnit && totalSeconds > 0)
        {
            duration = TimeSpan.FromSeconds(totalSeconds);
            timeDisplay = TimerService.FormatDuration(duration);
            remainder = current.Trim();
            return true;
        }

        return false;
    }

    public static int? ParseTimerNumeral(string val)
    {
        if (int.TryParse(val, out int n)) return n;
        return val.ToLowerInvariant() switch
        {
            "один" or "одну" or "одна" => 1,
            "два" or "две" => 2,
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
            _ => null
        };
    }
}
