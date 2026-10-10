using System.Text;
using System.Text.RegularExpressions;

namespace Gem.Services.Steam;

/// <summary>
/// Phonetic normalization, English &lt;-&gt; Cyrillic transliteration, and fuzzy similarity calculations for Steam game queries.
/// </summary>
public static class SteamPhonetics
{
    /// <summary>
    /// Normalizes a game title by lowercasing, replacing punctuation/symbols with spaces,
    /// and removing redundant whitespace.
    /// </summary>
    public static string NormalizeGameTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string lower = raw.ToLowerInvariant();
        lower = Regex.Replace(lower, @"['’`]", ""); // e.g. "no man's sky" -> "no mans sky"
        lower = Regex.Replace(lower, @"[^\w\s\dа-яё]", " ");
        lower = Regex.Replace(lower, @"\s+", " ").Trim();
        return lower;
    }

    // High-frequency gaming dictionary terms mapping English phonetic pronunciation to Russian Vosk representation
    private static readonly Dictionary<string, string> WordReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cyberpunk"] = "киберпанк",
        ["cyber"]     = "кибер",
        ["punk"]      = "панк",
        ["space"]     = "спейс",
        ["no"]        = "ноу",
        ["mans"]      = "менс",
        ["man"]       = "мен",
        ["sky"]       = "скай",
        ["witcher"]   = "витчер",
        ["wild"]      = "вайлд",
        ["wilds"]     = "вайлдс",
        ["hunt"]      = "хант",
        ["hunter"]    = "хантер",
        ["monster"]   = "монстер",
        ["world"]     = "ворлд",
        ["strike"]    = "страйк",
        ["counter"]   = "контра",
        ["game"]      = "гейм",
        ["games"]     = "геймс",
        ["half"]      = "халф",
        ["life"]      = "лайф",
        ["dead"]      = "дед",
        ["red"]       = "ред",
        ["redemption"]= "редэмпшн",
        ["fallout"]   = "фоллаут",
        ["skyrim"]    = "скайрим",
        ["scrolls"]   = "скроллс",
        ["elder"]     = "элдер",
        ["apex"]      = "апекс",
        ["legends"]   = "леджендс",
        ["duty"]      = "дьюти",
        ["call"]      = "колл",
        ["auto"]      = "авто",
        ["theft"]     = "тефт",
        ["grand"]     = "гранд",
        ["payday"]    = "пейдей",
        ["rust"]      = "раст",
        ["tarkov"]    = "тарков",
        ["escape"]    = "эскейп",
        ["from"]      = "фром",
        ["terraria"]  = "террария",
        ["starfield"] = "старфилд",
        ["star"]      = "стар",
        ["wars"]      = "варс",
        ["assassins"] = "ассасинс",
        ["creed"]     = "крид",
        ["resident"]  = "резидент",
        ["evil"]      = "ивил",
        ["silent"]    = "сайлент",
        ["hill"]      = "хилл",
        ["metal"]     = "метал",
        ["gear"]      = "гир",
        ["solid"]     = "солид",
        ["devil"]     = "девил",
        ["cry"]       = "край",
        ["dark"]      = "дарк",
        ["souls"]     = "соулс",
        ["elden"]     = "элден",
        ["ring"]      = "ринг",
        ["bloodborne"]= "бладборн",
        ["sekiro"]    = "секиро",
        ["baldur"]    = "балдур",
        ["baldurs"]   = "балдурс",
        ["gate"]      = "гейт",
        ["divinity"]  = "дивинити",
        ["original"]  = "ориджинал",
        ["sin"]       = "син",
        ["civilization"] = "цивилизация",
        ["hollow"]    = "холлоу",
        ["knight"]    = "найт",
        ["portal"]    = "портал",
        ["team"]      = "тим",
        ["fortress"]  = "фортресс",
        ["destiny"]   = "дестини",
        ["warframe"]  = "варфрейм",
        ["helldivers"]= "хеллдайверс",
        ["palworld"]  = "палворлд",
        ["astroneer"] = "астронир"
    };

    /// <summary>
    /// Converts English letters and syllables into Russian Cyrillic phonetics
    /// compensating for Vosk speech recognition tendencies.
    /// </summary>
    public static string TransliterateToCyrillic(string english)
    {
        if (string.IsNullOrWhiteSpace(english)) return string.Empty;

        // Handle possessive 's (e.g. "No Man's Sky" -> "No Mans Sky")
        string prepared = Regex.Replace(english, @"['’`]s\b", "s", RegexOptions.IgnoreCase);
        prepared = Regex.Replace(prepared, @"['’`]", "");

        var words = prepared.Split(new[] { ' ', '-', '_', ':', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var resultWords = new List<string>();

        foreach (var rawWord in words)
        {
            string word = rawWord.ToLowerInvariant().Trim();
            if (WordReplacements.TryGetValue(word, out var replaced))
            {
                resultWords.Add(replaced);
                continue;
            }

            // Rule-based phonetic transliteration
            var sb = new StringBuilder();
            int i = 0;
            while (i < word.Length)
            {
                // Multi-letter patterns
                if (i + 4 <= word.Length && word.Substring(i, 4) == "ight") { sb.Append("айт"); i += 4; continue; }
                if (i + 4 <= word.Length && word.Substring(i, 4) == "tion") { sb.Append("шн"); i += 4; continue; }
                if (i + 4 <= word.Length && word.Substring(i, 4) == "sion") { sb.Append("жн"); i += 4; continue; }
                if (i + 4 <= word.Length && word.Substring(i, 4) == "itch") { sb.Append("итч"); i += 4; continue; }

                if (i + 3 <= word.Length)
                {
                    string tri = word.Substring(i, 3);
                    if (tri == "sch") { sb.Append("ш"); i += 3; continue; }
                    if (tri == "tch") { sb.Append("ч"); i += 3; continue; }
                    if (tri == "ace") { sb.Append("ейс"); i += 3; continue; }
                    if (tri == "ake") { sb.Append("ейк"); i += 3; continue; }
                    if (tri == "ate") { sb.Append("ейт"); i += 3; continue; }
                    if (tri == "ame") { sb.Append("ейм"); i += 3; continue; }
                    if (tri == "ane") { sb.Append("ейн"); i += 3; continue; }
                    if (tri == "ave") { sb.Append("ейв"); i += 3; continue; }
                    if (tri == "ade") { sb.Append("ейд"); i += 3; continue; }
                    if (tri == "ice") { sb.Append("айс"); i += 3; continue; }
                    if (tri == "ike") { sb.Append("айк"); i += 3; continue; }
                    if (tri == "ite") { sb.Append("айт"); i += 3; continue; }
                    if (tri == "ime") { sb.Append("айм"); i += 3; continue; }
                    if (tri == "ine") { sb.Append("айн"); i += 3; continue; }
                    if (tri == "ive") { sb.Append("айв"); i += 3; continue; }
                    if (tri == "ide") { sb.Append("айд"); i += 3; continue; }
                }

                if (i + 2 <= word.Length)
                {
                    string pair = word.Substring(i, 2);
                    if (pair == "sh") { sb.Append("ш"); i += 2; continue; }
                    if (pair == "ch") { sb.Append("ч"); i += 2; continue; }
                    if (pair == "th") { sb.Append("т"); i += 2; continue; }
                    if (pair == "ph") { sb.Append("ф"); i += 2; continue; }
                    if (pair == "ck") { sb.Append("к"); i += 2; continue; }
                    if (pair == "wh") { sb.Append("в"); i += 2; continue; }
                    if (pair == "wr") { sb.Append("р"); i += 2; continue; }
                    if (pair == "kn") { sb.Append("н"); i += 2; continue; }
                    if (pair == "qu") { sb.Append("кв"); i += 2; continue; }
                    if (pair == "oo") { sb.Append("у"); i += 2; continue; }
                    if (pair == "ee") { sb.Append("и"); i += 2; continue; }
                    if (pair == "ea") { sb.Append("и"); i += 2; continue; }
                    if (pair == "ai" || pair == "ay") { sb.Append("ей"); i += 2; continue; }
                    if (pair == "oi" || pair == "oy") { sb.Append("ой"); i += 2; continue; }
                    if (pair == "ei" || pair == "ey") { sb.Append("ей"); i += 2; continue; }
                    if (pair == "ow") { sb.Append("оу"); i += 2; continue; }
                    if (pair == "ou") { sb.Append("ау"); i += 2; continue; }
                    if (pair == "cy") { sb.Append("ки"); i += 2; continue; }
                }

                char c = word[i];
                if (c == 'c')
                {
                    char next = (i + 1 < word.Length) ? word[i + 1] : '\0';
                    sb.Append((next == 'e' || next == 'i') ? "с" : "к");
                }
                else if (c == 'g')
                {
                    char next = (i + 1 < word.Length) ? word[i + 1] : '\0';
                    sb.Append((next == 'e' || next == 'i') ? "дж" : "г");
                }
                else
                {
                    string single = c switch
                    {
                        'a' => "а",
                        'b' => "б",
                        'd' => "д",
                        'e' => "е",
                        'f' => "ф",
                        'h' => "х",
                        'i' => "и",
                        'j' => "дж",
                        'k' => "к",
                        'l' => "л",
                        'm' => "м",
                        'n' => "н",
                        'o' => "о",
                        'p' => "п",
                        'q' => "к",
                        'r' => "р",
                        's' => "с",
                        't' => "т",
                        'u' => "у",
                        'v' => "в",
                        'w' => "в",
                        'x' => "кс",
                        'y' => "и",
                        'z' => "з",
                        _ => c.ToString()
                    };
                    sb.Append(single);
                }

                i++;
            }

            resultWords.Add(sb.ToString());
        }

        return string.Join(" ", resultWords);
    }

    /// <summary>
    /// Translates Cyrillic characters into Latin equivalents according to phonetic mapping.
    /// </summary>
    public static string Transliterate(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            char lower = char.ToLowerInvariant(c);
            string mapped = lower switch
            {
                'а' => "a",
                'б' => "b",
                'в' => "v",
                'г' => "g",
                'д' => "d",
                'е' => "e",
                'ё' => "e",
                'ж' => "zh",
                'з' => "z",
                'и' => "i",
                'й' => "y",
                'к' => "k",
                'л' => "l",
                'м' => "m",
                'н' => "n",
                'о' => "o",
                'п' => "p",
                'р' => "r",
                'с' => "s",
                'т' => "t",
                'у' => "u",
                'ф' => "f",
                'х' => "h",
                'ц' => "ts",
                'ч' => "ch",
                'ш' => "sh",
                'щ' => "sch",
                'ъ' => "",
                'ы' => "y",
                'ь' => "",
                'э' => "e",
                'ю' => "yu",
                'я' => "ya",
                _ => c.ToString()
            };
            sb.Append(mapped);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes the Levenshtein distance between two strings using two-row memory optimization.
    /// </summary>
    public static int LevenshteinDistance(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
        if (string.IsNullOrEmpty(s2)) return s1.Length;

        int[] v0 = new int[s2.Length + 1];
        int[] v1 = new int[s2.Length + 1];

        for (int i = 0; i <= s2.Length; i++) v0[i] = i;

        for (int i = 0; i < s1.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < s2.Length; j++)
            {
                int cost = (char.ToLowerInvariant(s1[i]) == char.ToLowerInvariant(s2[j])) ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }
            for (int j = 0; j <= s2.Length; j++) v0[j] = v1[j];
        }

        return v0[s2.Length];
    }

    /// <summary>
    /// Computes similarity score (0.0 to 1.0) based on Levenshtein distance,
    /// sliding window substring matching, and containment.
    /// </summary>
    public static double CalculateSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
        if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return 1.0;

        int maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0) return 1.0;

        int distance = LevenshteinDistance(s1, s2);
        double bestScore = 1.0 - ((double)distance / maxLen);

        string longer = s1.Length >= s2.Length ? s1 : s2;
        string shorter = s1.Length < s2.Length ? s1 : s2;

        // Substring containment check (e.g. "dota" in "dota2")
        if (shorter.Length >= 3 && longer.Contains(shorter, StringComparison.OrdinalIgnoreCase))
        {
            double ratio = (double)shorter.Length / longer.Length;
            double containScore = 0.65 + (0.35 * ratio);
            if (containScore > bestScore)
            {
                bestScore = containScore;
            }
        }

        // Sliding window: compare query (s2) against windows of a longer game title (s1)
        if (shorter.Length >= 4 && s1.Length > s2.Length)
        {
            for (int i = 0; i <= s1.Length - s2.Length; i++)
            {
                string sub = s1.Substring(i, s2.Length);
                int subDist = LevenshteinDistance(sub, s2);
                double subScore = 1.0 - ((double)subDist / s2.Length);
                double weighted = subScore * (0.85 + 0.15 * ((double)s2.Length / s1.Length));
                if (weighted > bestScore)
                {
                    bestScore = weighted;
                }
            }
        }

        return Math.Clamp(bestScore, 0.0, 1.0);
    }
}
