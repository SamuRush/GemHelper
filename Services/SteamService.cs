using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Win32;
using Microsoft.Win32;

namespace Gem.Services;

/// <summary>
/// Model representing a Steam game indexed from local libraries, manifests, or cache.
/// </summary>
public sealed record SteamGameInfo(
    string AppId,
    string Title,
    string NormalizedTitle,
    string CyrillicTitle,
    bool IsInstalled,
    string? InstallPath = null,
    int StateFlags = 4,
    bool IsFromManifest = false
)
{
    public string Name => Title;
}

/// <summary>
/// Result of a Steam game search with confidence grading.
/// <para>Confidence &gt;= 0.82 — exact match (Found = true, RequiresConfirmation = false).</para>
/// <para>0.60 &lt;= Confidence &lt; 0.82 — probable candidate (Found = true, RequiresConfirmation = true).</para>
/// <para>Confidence &lt; 0.60 — not found (Found = false).</para>
/// </summary>
public sealed class GameSearchResult
{
    /// <summary>True if a matching game was found (confidence >= 0.60).</summary>
    public bool Found { get; set; }

    /// <summary>The matched game, or null when Found is false.</summary>
    public SteamGameInfo? Game { get; set; }

    /// <summary>Fuzzy similarity score from 0.0 to 1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>True when the match is probable but not certain (0.60 &lt;= Confidence &lt; 0.82) — requires user confirmation.</summary>
    public bool RequiresConfirmation => Confidence >= 0.60 && Confidence < 0.82;

    // Backward-compatibility forwarders for callers expecting SteamGameInfo
    public string AppId => Game?.AppId ?? string.Empty;
    public string Title => Game?.Title ?? string.Empty;
    public string Name => Game?.Name ?? string.Empty;
    public bool IsInstalled => Game?.IsInstalled ?? false;
    public string? InstallPath => Game?.InstallPath;

    public static implicit operator SteamGameInfo?(GameSearchResult? result) => result?.Game;
}

/// <summary>
/// Universal Steam service providing:
/// 1. Comprehensive library indexing (Registry, libraryfolders.vdf, appmanifest_*.acf, appinfo.vdf, userdata).
/// 2. Vosk Russian phonetic normalization (English &lt;-&gt; Cyrillic transliteration and fuzzy matching &gt;= 0.78).
/// 3. Game installation with automated Win32 dialog confirmation (auto-Enter).
/// 4. Game launch via steam://run/{appId}.
/// </summary>
public sealed class SteamService
{
    private static readonly Lazy<SteamService> _instance = new(() => new SteamService());
    public static SteamService Instance => _instance.Value;

    private readonly object _lock = new();
    private readonly Dictionary<string, SteamGameInfo> _gamesByAppId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SteamGameInfo> _games = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    private string? _steamPath;
    private readonly List<string> _libraryFolders = new();

    public IReadOnlyList<SteamGameInfo> IndexedGames
    {
        get
        {
            lock (_lock)
            {
                return _games.ToList();
            }
        }
    }

    public SteamService()
    {
        Initialize();
    }

    /// <summary>
    /// Initializes Steam paths, loads config aliases, and scans all game libraries and manifests.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            _gamesByAppId.Clear();
            _games.Clear();
            _aliases.Clear();
            _libraryFolders.Clear();

            // 1. Load aliases from appsettings.json
            LoadConfigAliases();

            // 2. Locate Steam directory from Registry or standard paths
            _steamPath = LocateSteamPath();
            if (string.IsNullOrWhiteSpace(_steamPath) || !Directory.Exists(_steamPath))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Steam directory not found in Registry or Program Files.");
                Console.ResetColor();
                RegisterFallbackAliases();
                return;
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Steam path detected: '{_steamPath}'");
            Console.ResetColor();

            // 3. Scan library folders from libraryfolders.vdf
            DiscoverLibraryFolders(_steamPath);

            // 4. Scan installed app manifests (appmanifest_*.acf)
            ScanAppManifests();

            // 5. Scan appcache/appinfo.vdf for full game titles
            ScanAppInfoCache(_steamPath);

            // 6. Scan userdata librarycache for owned app entries
            ScanUserdataLibraryCache(_steamPath);

            // 7. Register any configured aliases that may not have been detected
            RegisterFallbackAliases();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Библиотека Steam проиндексирована: {_games.Count} игр ({_games.Count(g => g.IsInstalled)} установлено).");
            Console.ResetColor();
        }
    }

    private void LoadConfigAliases()
    {
        try
        {
            var settings = AppSettingsService.Load();
            foreach (var kv in settings.GameAliases)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    _aliases[kv.Key.Trim()] = kv.Value.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Ошибка загрузки алиасов из appsettings.json: {ex.Message}");
            Console.ResetColor();
        }
    }

    private void RegisterFallbackAliases()
    {
        // Built-in hardcoded popular games & fallback definitions
        var fallbacks = new Dictionary<string, (string AppId, string Title)>
        {
            ["292030"] = ("292030", "The Witcher 3: Wild Hunt"),
            ["1091500"] = ("1091500", "Cyberpunk 2077"),
            ["275850"] = ("275850", "No Man's Sky"),
            ["570"] = ("570", "Dota 2"),
            ["730"] = ("730", "Counter-Strike 2"),
            ["2246340"] = ("2246340", "Monster Hunter Wilds"),
            ["582010"] = ("582010", "Monster Hunter: World"),
            ["361420"] = ("361420", "ASTRONEER")
        };

        foreach (var (appId, title) in fallbacks.Values)
        {
            if (!_gamesByAppId.TryGetValue(appId, out var existing) || existing.Title.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
            {
                AddOrUpdateGame(appId, title, isInstalled: false);
            }
        }

        // Popular aliases
        var defaultAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ведьмак"] = "292030",
            ["дота"] = "570",
            ["киберпанк"] = "1091500",
            ["ноу менс скай"] = "275850",
            ["кс"] = "730",
            ["контра"] = "730",
            ["вайлдс"] = "2246340",
            ["уайлдс"] = "2246340",
            ["ворлд"] = "582010",
            ["астронир"] = "361420",
            ["астрони р"] = "361420",
            ["астрониро"] = "361420",
            ["о стране"] = "361420",
            ["остране"] = "361420"
        };

        foreach (var (k, v) in defaultAliases)
        {
            if (!_aliases.ContainsKey(k))
            {
                _aliases[k] = v;
            }
        }
    }

    #region Indexing: Registry, Libraries, Manifests, VDF Cache

    private static string? LocateSteamPath()
    {
        try
        {
            // 1. CurrentUser\Software\Valve\Steam -> SteamPath
            using var cuKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (cuKey?.GetValue("SteamPath") is string cuPath && Directory.Exists(cuPath))
            {
                return NormalizePath(cuPath);
            }

            // 2. LocalMachine\SOFTWARE\Valve\Steam -> InstallPath
            using var lmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (lmKey?.GetValue("InstallPath") is string lmPath && Directory.Exists(lmPath))
            {
                return NormalizePath(lmPath);
            }

            // 3. WOW6432Node
            using var wowKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (wowKey?.GetValue("InstallPath") is string wowPath && Directory.Exists(wowPath))
            {
                return NormalizePath(wowPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamService] Ошибка чтения реестра Windows для поиска Steam: {ex.Message}");
        }

        // Standard fallback locations
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Steam",
            @"D:\Steam"
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string NormalizePath(string rawPath)
    {
        return rawPath.Replace('/', Path.DirectorySeparatorChar).Trim();
    }

    private void DiscoverLibraryFolders(string steamPath)
    {
        _libraryFolders.Add(steamPath);

        string[] vdfCandidates =
        {
            Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamPath, "config", "libraryfolders.vdf")
        };

        foreach (var vdfPath in vdfCandidates)
        {
            if (!File.Exists(vdfPath)) continue;

            try
            {
                string text = File.ReadAllText(vdfPath);
                // Matches "path"		"C:\\Program Files (x86)\\Steam" or "1" "D:\\SteamLibrary"
                var matches = Regex.Matches(text, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    string folder = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(folder) && !_libraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        _libraryFolders.Add(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Ошибка парсинга {vdfPath}: {ex.Message}");
            }
        }
    }

    private void ScanAppManifests()
    {
        foreach (var library in _libraryFolders)
        {
            string steamAppsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamAppsDir)) continue;

            try
            {
                var manifestFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf");
                foreach (var acf in manifestFiles)
                {
                    ParseAppManifest(acf, steamAppsDir);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Ошибка сканирования манифестов в '{steamAppsDir}': {ex.Message}");
            }
        }
    }

    private void ParseAppManifest(string acfPath, string steamAppsDir)
    {
        try
        {
            string content = File.ReadAllText(acfPath);
            var appIdMatch = Regex.Match(content, @"""appid""\s+""(\d+)""", RegexOptions.IgnoreCase);
            var nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var stateFlagsMatch = Regex.Match(content, @"""StateFlags""\s+""(\d+)""", RegexOptions.IgnoreCase);

            if (appIdMatch.Success && nameMatch.Success)
            {
                string appId = appIdMatch.Groups[1].Value.Trim();
                string title = nameMatch.Groups[1].Value.Trim();
                string? installDir = installDirMatch.Success ? Path.Combine(steamAppsDir, "common", installDirMatch.Groups[1].Value.Trim()) : null;

                int stateFlags = 4;
                if (stateFlagsMatch.Success && int.TryParse(stateFlagsMatch.Groups[1].Value, out int sf))
                {
                    stateFlags = sf;
                }

                // Steam StateFlags: 1 = StateUninstalled; 4 = StateFullyInstalled; 2 = StateUpdateRequired, 512 = StateUpdatePaused, 1024 = StateUpdateStarted, etc.
                // Any manifest file with StateFlags != 1 (and StateFlags > 0) is installed or in progress of downloading/installing
                bool isInstalledOrDownloading = (stateFlags != 1) && (stateFlags > 0);

                AddOrUpdateGame(appId, title, isInstalled: isInstalledOrDownloading, installDir, stateFlags: stateFlags, isFromManifest: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamService] Ошибка парсинга манифеста Steam: {ex.Message}");
        }
    }

    private void ScanAppInfoCache(string steamPath)
    {
        string appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");
        if (!File.Exists(appInfoPath)) return;

        try
        {
            using var fs = new FileStream(appInfoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            uint magic = reader.ReadUInt32();
            uint universe = reader.ReadUInt32();

            // Steam AppInfo versions:
            // 0x07564429 (Steam 2022+ v29): has 8-byte string table offset
            if (magic == 0x07564429)
            {
                long stringTableOffset = (long)reader.ReadUInt64();
                if (stringTableOffset > 16 && stringTableOffset < fs.Length)
                {
                    fs.Seek(stringTableOffset, SeekOrigin.Begin);
                    uint numStrings = reader.ReadUInt32();
                    var stringTable = new List<string>((int)Math.Min(numStrings, 50000));
                    int nameIndex = -1;

                    for (int i = 0; i < numStrings; i++)
                    {
                        var sb = new StringBuilder();
                        byte b;
                        while ((b = reader.ReadByte()) != 0)
                        {
                            sb.Append((char)b);
                        }
                        string s = sb.ToString();
                        stringTable.Add(s);
                        if (s == "name" && nameIndex == -1)
                        {
                            nameIndex = i;
                        }
                    }

                    if (nameIndex >= 0)
                    {
                        fs.Seek(16, SeekOrigin.Begin);
                        while (fs.Position < stringTableOffset - 10)
                        {
                            uint appId = reader.ReadUInt32();
                            if (appId == 0) break;

                            uint size = reader.ReadUInt32();
                            uint infoState = reader.ReadUInt32();
                            uint lastUpdated = reader.ReadUInt32();
                            ulong accessToken = reader.ReadUInt64();
                            byte[] hash = reader.ReadBytes(20);
                            uint changeNumber = reader.ReadUInt32();
                            byte[] dataHash = reader.ReadBytes(20); // V29 binary data hash

                            int payloadSize = (int)(size - 60);
                            if (payloadSize <= 0 || fs.Position + payloadSize > stringTableOffset)
                            {
                                break;
                            }

                            byte[] data = reader.ReadBytes(payloadSize);

                            // Find string property with keyIdx == nameIndex
                            for (int i = 0; i < data.Length - 5; i++)
                            {
                                if (data[i] == 1) // Type String
                                {
                                    int keyIdx = BitConverter.ToInt32(data, i + 1);
                                    if (keyIdx == nameIndex)
                                    {
                                        int valStart = i + 5;
                                        int valEnd = valStart;
                                        while (valEnd < data.Length && data[valEnd] != 0) valEnd++;
                                        if (valEnd > valStart)
                                        {
                                            string title = Encoding.UTF8.GetString(data, valStart, valEnd - valStart);
                                            if (!string.IsNullOrWhiteSpace(title))
                                            {
                                                AddOrUpdateGame(appId.ToString(), title, isInstalled: false);
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Кеш appinfo.vdf прочитан с исключением: {ex.Message}");
        }
    }

    private void ScanUserdataLibraryCache(string steamPath)
    {
        string userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir)) return;

        try
        {
            var jsonFiles = Directory.GetFiles(userdataDir, "*.json", SearchOption.AllDirectories);
            foreach (var jsonFile in jsonFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(jsonFile);
                if (uint.TryParse(fileName, out uint appId) && !_gamesByAppId.ContainsKey(appId.ToString()))
                {
                    // Known owned AppID from userdata/librarycache
                    // If we have an alias or title, record it
                    AddOrUpdateGame(appId.ToString(), $"Steam App {appId}", isInstalled: false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamService] Ошибка сканирования кэша библиотеки Steam: {ex.Message}");
        }
    }

    private void AddOrUpdateGame(string appId, string title, bool isInstalled, string? installDir = null, int stateFlags = 4, bool isFromManifest = false)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(title)) return;

        string normalized = NormalizeGameTitle(title);
        string cyrillic = TransliterateToCyrillic(normalized);

        if (_gamesByAppId.TryGetValue(appId, out var existing))
        {
            if (existing.Title.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) && !title.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
            {
                var updated = existing with
                {
                    Title = title,
                    NormalizedTitle = normalized,
                    CyrillicTitle = cyrillic,
                    IsInstalled = isInstalled || existing.IsInstalled,
                    InstallPath = installDir ?? existing.InstallPath,
                    StateFlags = stateFlags != 4 ? stateFlags : existing.StateFlags,
                    IsFromManifest = isFromManifest || existing.IsFromManifest
                };
                _gamesByAppId[appId] = updated;
                int idx = _games.FindIndex(g => g.AppId == appId);
                if (idx >= 0) _games[idx] = updated;
            }
            else if (isInstalled && !existing.IsInstalled)
            {
                var updated = existing with
                {
                    IsInstalled = true,
                    InstallPath = installDir ?? existing.InstallPath,
                    StateFlags = stateFlags,
                    IsFromManifest = isFromManifest || existing.IsFromManifest
                };
                _gamesByAppId[appId] = updated;
                int idx = _games.FindIndex(g => g.AppId == appId);
                if (idx >= 0) _games[idx] = updated;
            }
            else if (isFromManifest)
            {
                var updated = existing with
                {
                    StateFlags = stateFlags,
                    IsFromManifest = true,
                    InstallPath = installDir ?? existing.InstallPath,
                    IsInstalled = isInstalled || existing.IsInstalled
                };
                _gamesByAppId[appId] = updated;
                int idx = _games.FindIndex(g => g.AppId == appId);
                if (idx >= 0) _games[idx] = updated;
            }
            return;
        }

        var newGame = new SteamGameInfo(
            AppId: appId,
            Title: title,
            NormalizedTitle: normalized,
            CyrillicTitle: cyrillic,
            IsInstalled: isInstalled,
            InstallPath: installDir,
            StateFlags: stateFlags,
            IsFromManifest: isFromManifest
        );

        _gamesByAppId[appId] = newGame;
        _games.Add(newGame);
    }

    #endregion

    #region Phonetic Normalization & Transliteration

    /// <summary>
    /// Normalizes a game title by lowercasing, replacing punctuation/symbols with spaces,
    /// and removing redundant whitespace.
    /// </summary>
    public static string NormalizeGameTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string lower = raw.ToLowerInvariant();
        // Remove special trademarks, editions noise if needed, or keep clean alphanumeric
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
    /// E.g. "Cyberpunk" -> "киберпанк", "Space" -> "спейс", "No Man's Sky" -> "ноу менс скай", "Witcher" -> "витчер".
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
                    if (pair == "cy") { sb.Append("ки"); i += 2; continue; } // Cyberpunk Vosk preference
                }

                char c = word[i];
                // Handle context-dependent letters
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
    /// Translates Cyrillic characters into Latin equivalents according to the specified phonetic mapping:
    /// а->a, б->b, в->v, г->g, д->d, е->e, ё->e, ж->zh, з->z, и->i, й->y, к->k, л->l, м->m, н->n,
    /// о->o, п->p, р->r, с->s, т->t, у->u, ф->f, х->h, ц->ts, ч->ch, ш->sh, щ->sch, ъ->"", ы->y, ь->"", э->e, ю->yu, я->ya.
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
        // e.g. "kiberpank" in "cyberpunk2077", "vitcher" in "thewitcher3wildhunt"
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

    #endregion

    #region Search Engine

    /// <summary>
    /// Universal search for Steam game matching user query:
    /// 1. Step 1: Normalization & Cyrillic-to-Latin Transliteration.
    /// 2. Step 2: Exact alias and AppID direct matching.
    /// 3. Step 3: Fuzzy matching against local indexed games (Confidence &gt;= 0.82 or 0.60..0.81 confirmation).
    /// </summary>
    public GameSearchResult? FindGame(string query, bool autoInstall = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        bool hasInstallAction = Regex.IsMatch(query, @"\b(установи|скачай|поставь|install)\b", RegexOptions.IgnoreCase);

        var result = SearchGame(query);
        if (result == null || result.Game == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра не найдена для запроса '{query}'.");
            Console.ResetColor();

            if (autoInstall || hasInstallAction)
            {
                VoiceFeedbackService.Instance?.SpeakAsync("Игра не найдена в вашей библиотеке, сэр.");
            }
            return null;
        }

        if (result.RequiresConfirmation)
        {
            if (autoInstall || hasInstallAction)
            {
                JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Install, result.Game);
                VoiceFeedbackService.Instance?.SpeakAsync($"Вы имели в виду {result.Game.Name}, сэр?");
            }
            return result;
        }

        if (autoInstall || hasInstallAction)
        {
            LaunchInstallation(result.Game);
        }

        return result;
    }

    /// <summary>
    /// Searches for a game in Steam library and aliases with confidence grading:
    /// <list type="bullet">
    ///   <item>Confidence &gt;= 0.82 — high confidence match (RequiresConfirmation = false).</item>
    ///   <item>0.60 &lt;= Confidence &lt; 0.82 — probable candidate in zone of doubt (RequiresConfirmation = true).</item>
    ///   <item>Confidence &lt; 0.60 — rejected, returns null.</item>
    /// </list>
    /// </summary>
    public GameSearchResult? SearchGame(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        // Step 1: Normalization & Cyrillic-to-Latin Transliteration
        string text = Regex.Replace(query, @"\b(установи|скачай|поставь|install|игра|игру|игры|запусти|включи|открой)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = query.Trim();

        string rawQuery      = text.ToLowerInvariant().Replace(" ", "");
        string translitQuery = Transliterate(text).ToLowerInvariant().Replace(" ", "");

        string cleanRaw      = Regex.Replace(rawQuery,      @"[^a-z0-9а-яё]", "");
        string cleanTranslit = Regex.Replace(translitQuery, @"[^a-z0-9а-яё]", "");

        // AppID direct match -> confidence 1.0
        if (uint.TryParse(cleanRaw, out uint numericAppId))
        {
            lock (_lock)
            {
                if (_gamesByAppId.TryGetValue(numericAppId.ToString(), out var directAppIdGame))
                {
                    return new GameSearchResult { Found = true, Game = directAppIdGame, Confidence = 1.0 };
                }
            }
        }

        // Check explicit aliases
        lock (_lock)
        {
            if (_aliases.TryGetValue(cleanRaw, out string? aliasAppId) ||
                _aliases.TryGetValue(cleanTranslit, out aliasAppId)     ||
                _aliases.TryGetValue(text, out aliasAppId)              ||
                _aliases.TryGetValue(query.Trim(), out aliasAppId))
            {
                if (_gamesByAppId.TryGetValue(aliasAppId!, out var aliasGame))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена по алиасу: '{query}' -> '{aliasGame.Name}' (AppID: {aliasAppId})");
                    Console.ResetColor();

                    return new GameSearchResult { Found = true, Game = aliasGame, Confidence = 1.0 };
                }
                var fallbackGame = new SteamGameInfo(aliasAppId, query.Trim(), query.Trim(), query.Trim(), false);
                return new GameSearchResult { Found = true, Game = fallbackGame, Confidence = 1.0 };
            }
        }

        // Step 2: Fuzzy search across indexed library
        SteamGameInfo? bestGame = null;
        double bestScore = 0.0;

        lock (_lock)
        {
            foreach (var game in _games)
            {
                var cleanGameName = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (string.IsNullOrEmpty(cleanGameName)) continue;

                double scoreRaw      = CalculateSimilarity(cleanGameName, cleanRaw);
                double scoreTranslit = CalculateSimilarity(cleanGameName, cleanTranslit);
                double scoreCyrillic = !string.IsNullOrEmpty(game.CyrillicTitle)
                    ? CalculateSimilarity(Regex.Replace(game.CyrillicTitle.ToLowerInvariant(), @"[^a-z0-9а-яё]", ""), cleanRaw)
                    : 0.0;

                double score = Math.Max(scoreRaw, Math.Max(scoreTranslit, scoreCyrillic));

                bool isDlcOrBonus = game.Title.Contains("Bonus Content",    StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Soundtrack",        StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Expansion Pass",    StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Artbook",           StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Trailer",           StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Dedicated Server",  StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus) score *= 0.80;

                if (score > bestScore || (Math.Abs(score - bestScore) < 0.001 && game.IsInstalled && bestGame != null && !bestGame.IsInstalled))
                {
                    bestScore = score;
                    bestGame  = game;
                }
            }
        }

        // Apply two-threshold logic:
        // Score >= 0.82: High confidence (RequiresConfirmation = false)
        if (bestScore >= 0.82 && bestGame != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра найдена: '{query}' -> '{bestGame.Name}' (балл: {bestScore:F2}, уверенность >= 0.82)");
            Console.ResetColor();

            return new GameSearchResult { Found = true, Game = bestGame, Confidence = bestScore };
        }

        // 0.60 <= Score < 0.82: Zone of doubt (RequiresConfirmation = true)
        if (bestScore >= 0.60 && bestGame != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Вероятный кандидат в зоне сомнения: '{query}' -> '{bestGame.Name}' (балл: {bestScore:F2}, 0.60 <= score < 0.82)");
            Console.ResetColor();

            return new GameSearchResult { Found = true, Game = bestGame, Confidence = bestScore };
        }

        // Score < 0.60: Rejection
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра не найдена для запроса '{query}' (лучший балл: {bestScore:F2} < 0.60).");
        Console.ResetColor();

        return null;
    }

    /// <summary>
    /// Searches for a game asynchronously with confidence grading.
    /// </summary>
    public Task<GameSearchResult?> SearchGameAsync(string query)
    {
        return Task.Run(() => SearchGame(query));
    }

    /// <summary>
    /// Static shortcut to search strictly among installed Steam games (appmanifest_*.acf).
    /// </summary>
    public static SteamGameInfo? FindInstalledGame(string query, bool initiateConfirmation = false) =>
        Instance.FindInstalledGameInternal(query, initiateConfirmation);

    /// <summary>
    /// Searches strictly among installed Steam games (appmanifest_*.acf).
    /// Accounts for games both installed (StateFlags & 4 != 0) and in progress of downloading/installing (StateFlags != 1 && StateFlags > 0).
    /// Returns the matched SteamGameInfo if confidence >= 0.60, or null if not found or not installed.
    /// When initiateConfirmation is true, triggers uninstallation confirmation FSM and bypasses wake-word.
    /// </summary>
    public SteamGameInfo? FindInstalledGameInternal(string query, bool initiateConfirmation = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        string text = Regex.Replace(query, @"\b(удали|деинсталлируй|сноси|удалить|деинсталлировать|снеси|игра|игру|игры)\b", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = query.Trim();

        string rawQuery      = text.ToLowerInvariant().Replace(" ", "");
        string translitQuery = Transliterate(text).ToLowerInvariant().Replace(" ", "");

        string cleanRaw      = Regex.Replace(rawQuery,      @"[^a-z0-9а-яё]", "");
        string cleanTranslit = Regex.Replace(translitQuery, @"[^a-z0-9а-яё]", "");

        lock (_lock)
        {
            // 1. Direct numeric AppId match
            if (uint.TryParse(cleanRaw, out uint numericAppId))
            {
                if (_gamesByAppId.TryGetValue(numericAppId.ToString(), out var directGame) &&
                    (directGame.IsInstalled || directGame.IsFromManifest || (directGame.StateFlags != 1 && directGame.StateFlags > 0)))
                {
                    if (initiateConfirmation)
                    {
                        TriggerUninstallConfirmation(directGame);
                    }
                    return directGame;
                }
            }

            // 2. Exact alias match
            if ((_aliases.TryGetValue(cleanRaw, out string? aliasAppId) ||
                 _aliases.TryGetValue(cleanTranslit, out aliasAppId)     ||
                 _aliases.TryGetValue(text, out aliasAppId)              ||
                 _aliases.TryGetValue(query.Trim(), out aliasAppId)) &&
                aliasAppId != null)
            {
                if (_gamesByAppId.TryGetValue(aliasAppId, out var aliasGame) &&
                    (aliasGame.IsInstalled || aliasGame.IsFromManifest || (aliasGame.StateFlags != 1 && aliasGame.StateFlags > 0)))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра найдена по алиасу: '{query}' -> '{aliasGame.Name}' (AppID: {aliasGame.AppId})");
                    Console.ResetColor();

                    if (initiateConfirmation)
                    {
                        TriggerUninstallConfirmation(aliasGame);
                    }
                    return aliasGame;
                }
            }

            // 3. Search strictly among installed/manifest games (StateFlags != 1)
            SteamGameInfo? bestGame = null;
            double bestScore = 0.0;

            foreach (var game in _games)
            {
                bool isCandidate = game.IsInstalled || game.IsFromManifest || (game.StateFlags != 1 && game.StateFlags > 0);
                if (!isCandidate) continue;

                var cleanGameName = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (string.IsNullOrEmpty(cleanGameName)) continue;

                double scoreRaw      = CalculateSimilarity(cleanGameName, cleanRaw);
                double scoreTranslit = CalculateSimilarity(cleanGameName, cleanTranslit);
                double scoreCyrillic = !string.IsNullOrEmpty(game.CyrillicTitle)
                    ? CalculateSimilarity(Regex.Replace(game.CyrillicTitle.ToLowerInvariant(), @"[^a-z0-9а-яё]", ""), cleanRaw)
                    : 0.0;

                double score = Math.Max(scoreRaw, Math.Max(scoreTranslit, scoreCyrillic));

                bool isDlcOrBonus = game.Title.Contains("Bonus Content", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Expansion Pass", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Artbook", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Trailer", StringComparison.OrdinalIgnoreCase)
                                 || game.Title.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus) score *= 0.80;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestGame = game;
                }
            }

            if (bestScore >= 0.60 && bestGame != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра найдена: '{query}' -> '{bestGame.Name}' (AppID: {bestGame.AppId}, Score: {bestScore:F2})");
                Console.ResetColor();

                if (initiateConfirmation)
                {
                    TriggerUninstallConfirmation(bestGame);
                }
                return bestGame;
            }

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Установленная игра не найдена для запроса '{query}' (лучший балл: {bestScore:F2} < 0.60).");
            Console.ResetColor();

            return null;
        }
    }

    private static void TriggerUninstallConfirmation(SteamGameInfo game)
    {
        try
        {
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Uninstall, game);
            VoiceListener.Instance?.EnterConfirmationListening("Awaiting uninstallation confirmation reply (bypassing wake-word)...");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamService] Ошибка активации FSM подтверждения деинсталляции: {ex.Message}");
        }
    }

    /// <summary>
    /// Installs a game by its <see cref="SteamGameInfo"/> record.
    /// Launches steam://install/{appId} and starts the AutoClicker confirmation routine.
    /// </summary>
    public Task InstallGameAsync(SteamGameInfo game)
    {
        LaunchInstallation(game);
        return Task.CompletedTask;
    }


    public SteamGameInfo? MatchGameByTitle(string title, string? originalQuery = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string cleanTitle = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
        if (string.IsNullOrEmpty(cleanTitle)) return null;

        lock (_lock)
        {
            // 1. Exact match
            foreach (var game in _games)
            {
                var cleanGame = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                if (cleanGame == cleanTitle) return game;
            }

            // 2. Fuzzy match
            SteamGameInfo? best = null;
            double bestScore = 0.0;
            foreach (var game in _games)
            {
                var cleanGame = Regex.Replace(game.Name.ToLowerInvariant(), @"[^a-z0-9а-яё]", "");
                double score = CalculateSimilarity(cleanGame, cleanTitle);

                bool isDlcOrBonus = game.Title.Contains("Bonus Content", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Expansion Pass", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Artbook", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Trailer", StringComparison.OrdinalIgnoreCase) ||
                                    game.Title.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

                if (isDlcOrBonus)
                {
                    score *= 0.80;
                }

                if (!string.IsNullOrEmpty(originalQuery) && _aliases.TryGetValue(originalQuery, out string? origAliasAppId) && game.AppId == origAliasAppId)
                {
                    score += 0.15;
                }

                if (score > bestScore || (Math.Abs(score - bestScore) < 0.001 && game.IsInstalled && best != null && !best.IsInstalled))
                {
                    bestScore = score;
                    best = game;
                }
            }

            return bestScore >= 0.82 ? best : null;
        }
    }

    /// <summary>
    /// Makes a fast query to local LLM to translate Russian game query to original English Steam title:
    /// Prompt: $"Какое точное оригинальное название у игры '{query}' в Steam на английском языке? Ответь ТОЛЬКО названием игры, без кавычек и знаков препинания."
    /// </summary>
    public string? QueryLlmForEnglishGameTitle(string query)
    {
        try
        {
            var settings = AppSettingsService.Load();
            string baseUrl = settings.LlmBaseUrl;
            string model = settings.LlmModel;
            string apiKey = settings.LlmApiKey;

            string prompt = $"Какое точное оригинальное название у игры '{query}' в Steam на английском языке? Ответь ТОЛЬКО названием игры, без кавычек и знаков препинания.";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string endpoint = LlmIntentService.BuildEndpointUrl(baseUrl);

            var requestBody = new
            {
                model = model,
                temperature = 0.1,
                max_tokens = 50,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            string json = JsonSerializer.Serialize(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey) && !apiKey.Equals("YOUR_OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = Task.Run(async () => await client.SendAsync(request)).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                string body = Task.Run(async () => await response.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    string? text = null;
                    if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentProp))
                    {
                        text = contentProp.GetString();
                    }
                    else if (first.TryGetProperty("text", out var textProp))
                    {
                        text = textProp.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string cleaned = text.Trim().Trim('"', '\'', '`', '.', '!', '?', '\n', '\r');
                        cleaned = Regex.Replace(cleaned, @"^```[a-z]*\s*", "", RegexOptions.IgnoreCase);
                        cleaned = Regex.Replace(cleaned, @"\s*```$", "", RegexOptions.IgnoreCase).Trim();
                        return cleaned;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] LLM fallback для '{query}' не ответил: {ex.Message}");
            Console.ResetColor();
        }

        // Offline alias fallback
        lock (_lock)
        {
            if (_aliases.TryGetValue(query, out string? aliasAppId) && _gamesByAppId.TryGetValue(aliasAppId, out var aliasGame))
            {
                return aliasGame.Title;
            }
        }

        return null;
    }

    /// <summary>
    /// Launches game installation wizard via steam://install/{appId} and starts AutoClicker confirmation.
    /// </summary>
    public void LaunchInstallation(SteamGameInfo game)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Запуск установки: '{game.Title}' (AppID: {game.AppId})...");
        Console.ResetColor();

        try
        {
            Process.Start(new ProcessStartInfo($"steam://install/{game.AppId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Error] Ошибка запуска протокола steam://install/: {ex.Message}");
            Console.ResetColor();
            return;
        }

        _ = Task.Run(async () =>
        {
            await PollAndConfirmInstallDialogAsync();
        });
    }

    /// <summary>
    /// Launches the game installation wizard via steam://install/{appId},
    /// dumps all Steam windows before launch, and runs AutoClicker with periodic window dumps and auto-Enter confirmation.
    /// </summary>
    /// <param name="gameQuery">Game name, query, or alias.</param>
    /// <param name="onFound">Callback invoked when game is found, passing the game title.</param>
    /// <returns>True if the game was resolved and installation wizard was initiated; otherwise false.</returns>
    public async Task<bool> InstallGameAsync(string gameQuery, Action<string> onFound)
    {
        if (string.IsNullOrWhiteSpace(gameQuery))
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Пустой запрос для установки игры.");
            return false;
        }

        var search = SearchGame(gameQuery);
        if (search == null || search.Game == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Игра по запросу '{gameQuery}' не найдена в каталоге Steam.");
            Console.ResetColor();
            VoiceFeedbackService.Instance?.SpeakAsync("Игра не найдена в вашей библиотеке, сэр.");
            return false;
        }

        if (search.RequiresConfirmation)
        {
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Install, search.Game);
            VoiceFeedbackService.Instance?.SpeakAsync($"Вы имели в виду {search.Game.Name}, сэр?");
            return false;
        }

        // Notify caller that game is found
        onFound(search.Game.Title);

        LaunchInstallation(search.Game);
        return true;
    }

    /// <summary>
    /// Finds the main visible window of process "steam" (IsWindowVisible == true, Rect.Width > 600, Rect.Height > 400).
    /// </summary>
    public static IntPtr FindMainSteamWindow()
    {
        IntPtr mainHwnd = IntPtr.Zero;

        var steamProcesses = Process.GetProcessesByName("steam").ToArray();
        var steamPids = steamProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (steamPids.Count > 0)
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (steamPids.Contains(pid) && NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowRect(hWnd, out RECT rect);
                    if (rect.Width > 600 && rect.Height > 400)
                    {
                        mainHwnd = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }

        foreach (var p in steamProcesses) p.Dispose();

        if (mainHwnd != IntPtr.Zero) return mainHwnd;

        // Fallback for modern CEF UI where main container may belong to steamwebhelper
        var helperProcesses = Process.GetProcessesByName("steamwebhelper").ToArray();
        var helperPids = helperProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (helperPids.Count > 0)
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (helperPids.Contains(pid) && NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowRect(hWnd, out RECT rect);
                    if (rect.Width > 600 && rect.Height > 400)
                    {
                        mainHwnd = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }

        foreach (var p in helperProcesses) p.Dispose();

        return mainHwnd;
    }

    #region Win32 Hard Focus P/Invoke

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>
    /// Forces a window to the foreground by attaching thread input queues to bypass Windows restrictions.
    /// </summary>
    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        try
        {
            uint foreThread = GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint appThread = GetCurrentThreadId();

            if (foreThread != 0 && foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, true);
            }

            ShowWindow(hWnd, 9); // SW_RESTORE
            NativeMethods.SetForegroundWindow(hWnd);

            if (foreThread != 0 && foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] ForceForegroundWindow: {ex.Message}");
            NativeMethods.SetForegroundWindow(hWnd);
        }
    }

    #endregion

    /// <summary>
    /// Делает быстрый скриншот строго области окна <paramref name="hWnd"/> в память и сканирует его снизу вверх
    /// в поисках фирменного синего цвета кнопок Steam (B &gt; 170, B &gt; R + 60, G &gt; 90).
    /// Возвращает абсолютные экранные координаты первого совпадающего пикселя, или <c>null</c> — если кнопка не найдена.
    /// </summary>
    private static Point? FindSteamBlueButton(IntPtr hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out RECT rect)) return null;

        int width  = rect.Right  - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        // Копируем строго область окна Steam
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        // Сканируем снизу вверх с шагом 4 пикселя — кнопки «Установить»/«Принять» всегда внизу диалога
        for (int y = height - 15; y > height / 3; y -= 4)
        {
            for (int x = 30; x < width - 30; x += 4)
            {
                Color c = bmp.GetPixel(x, y);
                // Фирменный синий цвет кнопок Steam: доминирует Blue, заметно выше Red и Green
                if (c.B > 170 && c.B > c.R + 60 && c.G > 90)
                {
                    // Переводим локальные координаты bitmap в абсолютные экранные
                    return new Point(rect.Left + x, rect.Top + y);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Ищет синюю кнопку Steam в окне <paramref name="hWnd"/> и выполняет клик мышью.
    /// Возвращает экранные координаты клика <see cref="Point"/>, если кнопка найдена и клик выполнен, иначе <c>null</c>.
    /// </summary>
    private static async Task<Point?> TryClickBlueButtonAsync(IntPtr hWnd, string? actionLabel = null)
    {
        Point? buttonPos = FindSteamBlueButton(hWnd);
        if (buttonPos.HasValue)
        {
            SetCursorPos(buttonPos.Value.X, buttonPos.Value.Y);
            await Task.Delay(100);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            await Task.Delay(80);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            Console.ForegroundColor = ConsoleColor.Green;
            string label = !string.IsNullOrWhiteSpace(actionLabel)
                ? actionLabel
                : "Клик по синей кнопке выполнен";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] {label} (X={buttonPos.Value.X}, Y={buttonPos.Value.Y}).");
            Console.ResetColor();
            return buttonPos;
        }
        return null;
    }

    /// <summary>
    /// Searches for the Steam "Uninstall" confirmation blue button strictly within the bounds
    /// of the modal dialog window (bottom-left region), and calculates the exact geometric center.
    /// </summary>
    public static Point? FindSteamUninstallButton(IntPtr hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        // Helper to check Steam accent blue color
        static bool IsSteamAccentBlue(Color c)
        {
            return (c.B >= 150 && c.B > c.R + 50 && c.G >= 75 && c.R < 120) ||
                   (c.B > 170 && c.B > c.R + 60 && c.G > 90);
        }

        // Primary scan: lower-left portion of the dialog (y in [height * 0.55 .. height - 10], x in [15 .. width * 0.50])
        var bluePixels = new List<Point>();
        int startY = Math.Max((int)(height * 0.55), 0);
        int endY = Math.Max(height - 10, startY);
        int startX = 15;
        int endX = Math.Min(Math.Max((int)(width * 0.50), 100), width - 15);

        for (int y = endY; y >= startY; y -= 2)
        {
            for (int x = startX; x <= endX; x += 2)
            {
                Color c = bmp.GetPixel(x, y);
                if (IsSteamAccentBlue(c))
                {
                    bluePixels.Add(new Point(x, y));
                }
            }
        }

        // Fallback scan: if not found in lower-left, scan across the entire bottom region
        if (bluePixels.Count == 0)
        {
            for (int y = endY; y >= startY; y -= 2)
            {
                for (int x = startX; x < width - 15; x += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (IsSteamAccentBlue(c))
                    {
                        bluePixels.Add(new Point(x, y));
                    }
                }
            }
        }

        if (bluePixels.Count == 0)
        {
            return null;
        }

        // Calculate exact center of the blue button cluster
        int minX = bluePixels.Min(p => p.X);
        int maxX = bluePixels.Max(p => p.X);
        int minY = bluePixels.Min(p => p.Y);
        int maxY = bluePixels.Max(p => p.Y);

        int centerX = rect.Left + (minX + maxX) / 2;
        int centerY = rect.Top + (minY + maxY) / 2;

        return new Point(centerX, centerY);
    }

    /// <summary>
    /// Performs a mouse click at the specified screen point.
    /// </summary>
    public static async Task ClickMouseAtAsync(Point pt)
    {
        SetCursorPos(pt.X, pt.Y);
        await Task.Delay(100);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        await Task.Delay(80);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    /// <summary>
    /// AutoClicker: находит главное видимое окно Steam, выводит его на передний план,
    /// затем выполняет визуальное сканирование синей кнопки «Установить» и подтверждения параметров установки / EULA.
    /// </summary>
    public static async Task<bool> PollAndConfirmInstallDialogAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Поиск главного видимого окна процесса 'steam'...");
        Console.ResetColor();

        const int maxAttempts = 10;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            IntPtr hWnd = FindMainSteamWindow();
            if (hWnd != IntPtr.Zero)
            {
                // 1. Выводим окно на передний план и ждём его отрисовки
                ForceForegroundWindow(hWnd);
                await Task.Delay(1200);

                // 2. Шаг 1 — первый клик (X, Y) -> "Клик по кнопке 'Установить'"
                Point? installBtnPos = await TryClickBlueButtonAsync(hWnd, "Клик по кнопке 'Установить'");
                if (!installBtnPos.HasValue)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Синяя кнопка 'Установить' не найдена.");
                    Console.ResetColor();
                    return false;
                }

                // 3. Шаг 2 — подтверждение параметров установки / EULA (если синяя кнопка обнаружена повторно ниже)
                await Task.Delay(1200);
                Point? secondBtnPos = await TryClickBlueButtonAsync(hWnd, "Подтверждение параметров установки / EULA");
                if (secondBtnPos.HasValue)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Параметры установки / соглашение подтверждены (X={secondBtnPos.Value.X}, Y={secondBtnPos.Value.Y}). Установка успешно запущена.");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Дополнительное подтверждение не требуется. Установка успешно запущена.");
                    Console.ResetColor();
                }

                return true;
            }

            await Task.Delay(500);
        }

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Главное видимое окно процесса 'steam' не найдено.");
        Console.ResetColor();
        return false;
    }

    /// <summary>
    /// Launches an installed Steam game via steam://run/{appId}.
    /// </summary>
    /// <param name="gameQuery">Game query, title, or alias.</param>
    /// <param name="gameTitle">Out parameter with the canonical title of the launched game.</param>
    /// <returns>True if the game was found and launch initiated; otherwise false.</returns>
    public bool LaunchGame(string gameQuery, out string gameTitle)
    {
        gameTitle = string.Empty;
        if (string.IsNullOrWhiteSpace(gameQuery)) return false;

        var search = SearchGame(gameQuery);
        if (search == null || search.Game == null)
        {
            return false;
        }

        if (search.RequiresConfirmation)
        {
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Start, search.Game);
            return false;
        }

        gameTitle = search.Game.Title;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Запуск игры '{gameTitle}' (AppID: {search.Game.AppId}) через steam://run/...");
        Console.ResetColor();

        try
        {
            Process.Start(new ProcessStartInfo($"steam://run/{search.Game.AppId}")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Error] Ошибка запуска игры '{gameTitle}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    /// <summary>
    /// Registers or updates an installed game for testing purposes.
    /// </summary>
    public void RegisterInstalledGameForTesting(string appId, string title, string? installDir = null)
    {
        lock (_lock)
        {
            AddOrUpdateGame(appId, title, isInstalled: true, installDir, stateFlags: 4, isFromManifest: true);
        }
    }

    /// <summary>
    /// Automatically uninstalls a Steam game:
    /// 1. Initiates uninstallation via steam://uninstall/{appId}.
    /// 2. Polls for Steam confirmation dialog (up to 8 seconds).
    /// 3. Confirms uninstallation strictly via visual pixel scan of the blue button and mouse click.
    /// No Enter / VK_RETURN emulation is used.
    /// </summary>
    public static async Task<bool> UninstallGameAsync(uint appId, string title)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Инициирован запрос на удаление: {title} (AppId: {appId})");
        Console.ResetColor();

        try
        {
            Process.Start(new ProcessStartInfo($"steam://uninstall/{appId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Error] Ошибка запуска протокола steam://uninstall/: {ex.Message}");
            Console.ResetColor();
            return false;
        }

        // Poll for Steam confirmation dialog (timeout 8 sec, polling every 200 ms)
        IntPtr dialogHwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            dialogHwnd = FindSteamUninstallDialog(title);
            if (dialogHwnd != IntPtr.Zero)
            {
                break;
            }
            await Task.Delay(200);
        }

        if (dialogHwnd == IntPtr.Zero)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Диалоговое окно подтверждения удаления не обнаружено в течение таймаута.");
            Console.ResetColor();
            return false;
        }

        NativeMethods.GetWindowRect(dialogHwnd, out RECT dialogRect);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Найдено диалоговое окно удаления: HWND=0x{dialogHwnd.ToInt64():X}, Rect=[L:{dialogRect.Left}, T:{dialogRect.Top}, R:{dialogRect.Right}, B:{dialogRect.Bottom}]");
        Console.ResetColor();

        // Bring window to foreground
        ForceForegroundWindow(dialogHwnd);
        await Task.Delay(400); // Allow CEF dialog to finish rendering

        // Pixel Clicker: scan strictly within dialog rect for the blue confirmation button
        Point? buttonCenter = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            buttonCenter = FindSteamUninstallButton(dialogHwnd);
            if (buttonCenter.HasValue)
            {
                break;
            }
            await Task.Delay(250);
        }

        if (!buttonCenter.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Синяя кнопка подтверждения в диалоговом окне не найдена.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Кнопка 'Удалить' найдена в центре диалога: X={buttonCenter.Value.X}, Y={buttonCenter.Value.Y}. Выполняю клик мышью...");
        Console.ResetColor();

        await ClickMouseAtAsync(buttonCenter.Value);

        // Wait for dialog to close
        bool isClosed = false;
        var closeSw = Stopwatch.StartNew();
        while (closeSw.ElapsedMilliseconds < 3000)
        {
            if (!NativeMethods.IsWindowVisible(dialogHwnd))
            {
                isClosed = true;
                break;
            }
            await Task.Delay(150);
        }

        if (!isClosed)
        {
            // Retry mouse click on button if dialog remained open
            Point? retryCenter = FindSteamUninstallButton(dialogHwnd) ?? buttonCenter;
            if (retryCenter.HasValue)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Scanner] Повторный клик мышью по кнопке удаления (X={retryCenter.Value.X}, Y={retryCenter.Value.Y})...");
                Console.ResetColor();
                await ClickMouseAtAsync(retryCenter.Value);
                await Task.Delay(500);
                if (!NativeMethods.IsWindowVisible(dialogHwnd))
                {
                    isClosed = true;
                }
            }
        }

        if (isClosed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Диалоговое окно Steam подтверждено кликом мыши и закрыто.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService Warning] Диалоговое окно Steam не закрылось после клика.");
            Console.ResetColor();
        }

        // Update local cache: mark game as uninstalled
        lock (Instance._lock)
        {
            if (Instance._gamesByAppId.TryGetValue(appId.ToString(), out var g))
            {
                var updated = g with { IsInstalled = false, InstallPath = null };
                Instance._gamesByAppId[appId.ToString()] = updated;
                int idx = Instance._games.FindIndex(x => x.AppId == appId.ToString());
                if (idx >= 0) Instance._games[idx] = updated;
            }
        }

        return isClosed;
    }

    public static Task<bool> UninstallGameAsync(string appId, string title)
    {
        return uint.TryParse(appId, out uint numericId)
            ? UninstallGameAsync(numericId, title)
            : Task.FromResult(false);
    }

    /// <summary>
    /// Finds Steam modal uninstallation confirmation dialog.
    /// Strictly verifies that the window belongs to a Steam process (steam.exe or steamwebhelper.exe)
    /// and contains uninstallation keywords in its title.
    /// </summary>
    public static IntPtr FindSteamUninstallDialog(string title)
    {
        IntPtr foundHwnd = IntPtr.Zero;

        var steamProcesses = Process.GetProcessesByName("steam")
            .Concat(Process.GetProcessesByName("steamwebhelper"))
            .ToArray();
        var steamPids = steamProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (steamPids.Count == 0)
        {
            foreach (var p in steamProcesses) p.Dispose();
            return IntPtr.Zero;
        }

        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (!steamPids.Contains(pid)) return true; // STRICT: Window must belong to steam process!

                string wTitle = GetWindowTitle(hWnd);

                // Check title for "удаление", "удалить", "uninstall", "видален" or game title
                bool matchesUninstall = !string.IsNullOrEmpty(wTitle) &&
                    (wTitle.Contains("удаление", StringComparison.OrdinalIgnoreCase) ||
                     wTitle.Contains("удалить", StringComparison.OrdinalIgnoreCase) ||
                     wTitle.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                     wTitle.Contains("видален", StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(title) && wTitle.Contains(title, StringComparison.OrdinalIgnoreCase)));

                if (matchesUninstall && NativeMethods.GetWindowRect(hWnd, out RECT rect))
                {
                    int w = rect.Width;
                    int h = rect.Height;
                    // Modal dialog dimensions
                    if (w >= 250 && w <= 900 && h >= 120 && h <= 650)
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            foreach (var p in steamProcesses) p.Dispose();
        }

        return foundHwnd;
    }

    /// <summary>
    /// Gets window title using Win32 GetWindowText.
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    #endregion
}

public static class SteamServiceExtensions
{
    public static SteamGameInfo? FindInstalledGame(this SteamService service, string query, bool initiateConfirmation = false) =>
        SteamService.FindInstalledGame(query, initiateConfirmation);

    public static Task<bool> UninstallGameAsync(this SteamService service, uint appId, string title) =>
        SteamService.UninstallGameAsync(appId, title);

    public static Task<bool> UninstallGameAsync(this SteamService service, string appId, string title) =>
        SteamService.UninstallGameAsync(appId, title);
}
