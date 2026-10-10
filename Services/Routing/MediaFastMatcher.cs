using System.Text.RegularExpressions;
using Gem.Core;

namespace Gem.Services.Routing;

/// <summary>
/// Fast-path recognition for media playback and track navigation commands.
/// Emulates VK_MEDIA_* virtual keys (0ms latency, zero hallucinations, no SteamService/LLM calls).
/// </summary>
public static class MediaFastMatcher
{
    public static JarvisResponse? MatchMediaCommand(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
        {
            return null;
        }

        string text = RoutingHelper.CleanPrefixes(lower).Trim();
        // Strip common polite and filler words (e.g. "пожалуйста", "мне", "быстро", "сейчас", "давай")
        text = Regex.Replace(text, @"\b(пожалуйста|мне|быстро|сейчас|давай)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // 0. Volume exclusion: commands adjusting volume of video or player must defer to Volume handler
        bool isVolumeKeyword = text.Contains("громк") || text.Contains("звук") || text.Contains("тише") ||
                               text.Contains("громче") || text.Contains("убавь") || text.Contains("прибавь") ||
                               text.StartsWith("volume") || text.StartsWith("vol") ||
                               Regex.IsMatch(text, @"^\s*\d{1,3}\s*%\s*$");
        if (isVolumeKeyword)
        {
            return null;
        }

        // 1. Next Track / Media Category:
        // Patterns: "следующий трек", "следующая песня", "следующее видео", "переключи на следующий", "включи следующий", "скипни", etc.
        bool isNext = text == "следующий" || text == "следующая" || text == "следующее" || text == "следующую" ||
                      text == "следующий трек" || text == "следующая песня" || text == "следующее видео" ||
                      text == "следующий ролик" || text == "следующая серия" || text == "следующая композиция" ||
                      text == "дальше" || text == "вперед" || text == "трек вперед" || text == "песня вперед" || text == "видео вперед" || text == "вперед трек" ||
                      text == "переключи" || text == "переключить" || text == "переключай" ||
                      text == "переключи трек" || text == "переключи песню" || text == "переключи видео" || text == "переключи ролик" ||
                      text == "переключи следующий" || text == "переключи следующую" || text == "переключи следующее" ||
                      text == "переключи на следующий" || text == "переключи на следующую" || text == "переключи на следующее" ||
                      text == "включи следующий" || text == "включи следующую" || text == "включи следующее" ||
                      text == "включи следующий трек" || text == "включи следующую песню" || text == "включи следующее видео" ||
                      text == "поставь следующий" || text == "поставь следующую" || text == "поставь следующее" ||
                      text == "поставь следующий трек" || text == "поставь следующую песню" || text == "поставь следующее видео" ||
                      text == "пропусти" || text == "пропусти трек" || text == "пропусти песню" || text == "пропусти видео" ||
                      text == "скип" || text == "скипни" || text == "скипнуть" || text == "скипни трек" || text == "скипни песню" || text == "скипни видео" ||
                      text.StartsWith("следующий ") || text.StartsWith("следующая ") || text.StartsWith("следующее ") ||
                      text.StartsWith("переключи трек") || text.StartsWith("переключи песню") || text.StartsWith("переключи видео") ||
                      text.StartsWith("переключи на следующий") || text.StartsWith("переключи на следующую") || text.StartsWith("переключи на следующее") ||
                      text.StartsWith("переключи следующий") || text.StartsWith("переключи следующую") || text.StartsWith("переключи следующее") ||
                      text.StartsWith("включи следующий") || text.StartsWith("включи следующую") || text.StartsWith("включи следующее") ||
                      text.StartsWith("поставь следующий") || text.StartsWith("поставь следующую") || text.StartsWith("поставь следующее") ||
                      text.StartsWith("скипни") || text.StartsWith("пропусти ") ||
                      Regex.IsMatch(text, @"^(?:переключи|включи|поставь)\s+(?:на\s+)?следующ(?:ий|ая|ее|ую)(?:\s+(?:трек|песню|песня|видео|ролик))?$", RegexOptions.IgnoreCase);

        // Guard: do not confuse "переключи раскладку / язык / окно / вкладку" with next track
        if (isNext && !text.Contains("раскладк") && !text.Contains("язык") && !text.Contains("окно") && !text.Contains("вкладк"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "next", "key": "media_next" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Следующий трек, сэр.");
        }

        // 2. Prev Track / Media Category:
        // Patterns: "предыдущий трек", "предыдущая песня", "предыдущее видео", "переключи на предыдущий", "включи предыдущий", etc.
        bool isPrev = text == "предыдущий" || text == "предыдущая" || text == "предыдущее" || text == "предыдущую" ||
                      text == "предыдущий трек" || text == "предыдущая песня" || text == "предыдущее видео" ||
                      text == "предыдущий ролик" || text == "предыдущая серия" || text == "предыдущая композиция" ||
                      text == "прошлый трек" || text == "прошлая песня" || text == "прошлое видео" || text == "прошлый ролик" ||
                      text == "назад" || text == "верни трек" || text == "верни песню" || text == "верни видео" ||
                      text == "трек назад" || text == "песня назад" || text == "видео назад" || text == "назад трек" ||
                      text == "переключи назад" ||
                      text == "переключи предыдущий" || text == "переключи предыдущую" || text == "переключи предыдущее" ||
                      text == "переключи на предыдущий" || text == "переключи на предыдущую" || text == "переключи на предыдущее" ||
                      text == "включи предыдущий" || text == "включи предыдущую" || text == "включи предыдущее" ||
                      text == "включи предыдущий трек" || text == "включи предыдущую песню" || text == "включи прошлое видео" ||
                      text == "поставь предыдущий" || text == "поставь предыдущую" || text == "поставь предыдущее" ||
                      text == "поставь предыдущий трек" || text == "поставь предыдущую песню" ||
                      text.StartsWith("предыдущий ") || text.StartsWith("предыдущая ") || text.StartsWith("предыдущее ") ||
                      text.StartsWith("верни трек") || text.StartsWith("верни песню") || text.StartsWith("верни видео") ||
                      text.StartsWith("переключи на предыдущ") || text.StartsWith("переключи предыдущ") ||
                      text.StartsWith("включи предыдущ") || text.StartsWith("поставь предыдущ") ||
                      Regex.IsMatch(text, @"^(?:переключи|включи|поставь)\s+(?:на\s+)?предыдущ(?:ий|ая|ее|ую)(?:\s+(?:трек|песню|песня|видео|ролик))?$", RegexOptions.IgnoreCase);

        if (isPrev && !text.Contains("раскладк") && !text.Contains("язык") && !text.Contains("окно") && !text.Contains("вкладк"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "prev", "key": "media_prev" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Предыдущий трек, сэр.");
        }

        // 3. Video / Film / Serial Category:
        // Any phrase containing "видео", "ролик", "фильм", "сериал" is strictly routed to MediaCommandHandler
        bool hasVideoEntity = text.Contains("видео") || text.Contains("ролик") || text.Contains("фильм") || text.Contains("сериал");
        if (hasVideoEntity)
        {
            bool isVideoPause = text.Contains("пауз") || text.Contains("стоп") || text.Contains("останов") ||
                                text.Contains("выключ") || text.Contains("заглуш");
            if (isVideoPause)
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "pause_video" }""");
                return new JarvisResponse(new CommandRequest("media", args), "Остановил видео, сэр.");
            }
            else
            {
                var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "resume_video" }""");
                return new JarvisResponse(new CommandRequest("media", args), "Включаю видео, сэр.");
            }
        }

        // 4. Stop Category:
        // Patterns: "заглуши музыку", "выключи музыку", "стоп музыка", "стоп музыку", "стоп трек", "стоп"
        if (text == "заглуши музыку" || text == "выключи музыку" ||
            text == "заглушить музыку" || text == "выключить музыку" ||
            text == "стоп музыка" || text == "стоп музыку" ||
            text == "выключи трек" || text == "заглуши трек" ||
            text == "стоп трек" || text == "стоп песню" ||
            text.StartsWith("заглуши музыку") || text.StartsWith("выключи музыку"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "stop", "key": "media_stop" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Остановил, сэр.");
        }

        // 6. Yandex Music & Music Launch Category:
        // Patterns: "включи яндекс музыку", "запусти яндекс музыку", "яндекс музыка", "включи музыку", "поставь музыку", "запусти музыку"
        bool isYandexMusic = text == "включи яндекс музыку" || text == "включить яндекс музыку" ||
                             text == "запусти яндекс музыку" || text == "запустить яндекс музыку" ||
                             text == "поставь яндекс музыку" || text == "поставить яндекс музыку" ||
                             text == "яндекс музыка" || text == "яндекс музыку" ||
                             text == "яндекс.музыка" || text == "яндекс-музыка" ||
                             text.StartsWith("включи яндекс") || text.StartsWith("запусти яндекс") ||
                             text.StartsWith("поставь яндекс");

        if (isYandexMusic)
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "play_music", "app": "yandexmusic" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Включаю Яндекс Музыку, сэр.");
        }

        bool isPlayMusic = text == "включи музыку" || text == "включить музыку" ||
                           text == "музыка" || text == "музыку" ||
                           text == "играй музыку" || text == "играть музыку" ||
                           text == "поставь музыку" || text == "поставить музыку" ||
                           text == "запусти музыку" || text == "запустить музыку" ||
                           text == "поставь трек" || text == "поставь песню" ||
                           text == "запусти трек" || text == "запусти песню" ||
                           text.StartsWith("включи музыку") || text.StartsWith("включить музыку") ||
                           text.StartsWith("поставь музыку") || text.StartsWith("запусти музыку") ||
                           Regex.IsMatch(text, @"^(запусти|поставь|включи|сыграй)\s+(музыку|музыка|трек|треки|песню|песня|плейлист)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        if (isPlayMusic && !text.StartsWith("играй в ") && !text.StartsWith("играть в "))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "play_music", "app": "yandexmusic" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Включаю музыку, сэр.");
        }

        // 7. Pause Category («поставь на паузу»):
        // Patterns: "поставь на паузу", "на паузу", "пауза", "паузу", "останови воспроизведение", "останови музыку", "останови"
        bool isPause = text == "пауза" || text == "стоп" || text == "останови" || text == "останови музыку" ||
                       text == "останови воспроизведение" || text == "останови трек" || text == "останови песню" ||
                       text == "поставь на паузу" || text == "на паузу" || text == "паузу" ||
                       text.StartsWith("останови музыку") || text.StartsWith("останови трек") ||
                       text.StartsWith("останови песню") || text.StartsWith("поставь на паузу");

        // Do not intercept "стоп приложение" or "стоп джарвис"
        if (isPause && !text.Contains("приложение") && !text.Contains("джарвис") && !text.Contains("себя") && !text.Contains("программ"))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "pause" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Остановил, сэр.");
        }

        // 8. General Resume / Unpause Category:
        // Patterns: "продолжи", "возобнови", "сними с паузы", "плей", "play"
        bool isResume = text == "продолжи" || text == "продолжить" || text == "продолжай" ||
                        text == "продолжи музыку" || text == "продолжить музыку" ||
                        text == "продолжи воспроизведение" || text == "продолжить воспроизведение" ||
                        text == "возобнови" || text == "возобновить" ||
                        text == "возобнови музыку" || text == "возобновить музыку" ||
                        text == "возобнови воспроизведение" || text == "возобновить воспроизведение" ||
                        text == "сними с паузы" || text == "плей" || text == "play" || text == "играй" ||
                        text.StartsWith("продолжи музыку") || text.StartsWith("возобнови музыку");

        if (isResume && !text.StartsWith("играй в ") && !text.StartsWith("играть в "))
        {
            var args = RoutingHelper.CreateFastMatchArgs("""{ "action": "resume" }""");
            return new JarvisResponse(new CommandRequest("media", args), "Возобновляю воспроизведение, сэр.");
        }

        return null;
    }
}
