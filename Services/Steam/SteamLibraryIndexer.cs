using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gem.Services.Steam;

/// <summary>
/// Scans local Steam libraries, registry paths, manifests, and caches.
/// </summary>
public static class SteamLibraryIndexer
{
    private static readonly string[] ServiceKeywords =
    [
        "Steam Cloud",
        "Videos",
        "Soundtrack",
        "Dedicated Server",
        "Beta",
        "Tool",
        "Test"
    ];

    /// <summary>
    /// Checks if a title corresponds to a Steam service entry.
    /// These service entries are strictly excluded from the game search index.
    /// </summary>
    public static bool IsServiceEntry(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return ServiceKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Locates Steam install path from Registry or standard filesystem locations.
    /// </summary>
    public static string? LocateSteamPath()
    {
        try
        {
            using var cuKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (cuKey?.GetValue("SteamPath") is string cuPath && Directory.Exists(cuPath))
            {
                return NormalizePath(cuPath);
            }

            using var lmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (lmKey?.GetValue("InstallPath") is string lmPath && Directory.Exists(lmPath))
            {
                return NormalizePath(lmPath);
            }

            using var wowKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (wowKey?.GetValue("InstallPath") is string wowPath && Directory.Exists(wowPath))
            {
                return NormalizePath(wowPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamLibraryIndexer] Ошибка чтения реестра Windows для поиска Steam: {ex.Message}");
        }

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

    public static string NormalizePath(string rawPath)
    {
        return rawPath.Replace('/', Path.DirectorySeparatorChar).Trim();
    }

    public static void DiscoverLibraryFolders(string steamPath, List<string> libraryFolders)
    {
        if (!libraryFolders.Contains(steamPath, StringComparer.OrdinalIgnoreCase))
        {
            libraryFolders.Add(steamPath);
        }

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
                var matches = Regex.Matches(text, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match m in matches)
                {
                    string folder = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(folder) && !libraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        libraryFolders.Add(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamLibraryIndexer] Ошибка парсинга {vdfPath}: {ex.Message}");
            }
        }
    }

    public static void ScanAppManifests(
        IEnumerable<string> libraryFolders,
        Action<string, string, bool, string?, int, bool> onGameFound)
    {
        foreach (var library in libraryFolders)
        {
            string steamAppsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamAppsDir)) continue;

            try
            {
                var manifestFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf");
                foreach (var acf in manifestFiles)
                {
                    ParseAppManifest(acf, steamAppsDir, onGameFound);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamLibraryIndexer] Ошибка сканирования манифестов в '{steamAppsDir}': {ex.Message}");
            }
        }
    }

    public static void ParseAppManifest(
        string acfPath,
        string steamAppsDir,
        Action<string, string, bool, string?, int, bool> onGameFound)
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

                bool isInstalledOrDownloading = (stateFlags != 1) && (stateFlags > 0);
                onGameFound(appId, title, isInstalledOrDownloading, installDir, stateFlags, true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamLibraryIndexer] Ошибка парсинга манифеста Steam: {ex.Message}");
        }
    }

    public static void ScanAppInfoCache(
        string steamPath,
        Action<string, string, bool, string?, int, bool> onGameFound)
    {
        string appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");
        if (!File.Exists(appInfoPath)) return;

        try
        {
            using var fs = new FileStream(appInfoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            uint magic = reader.ReadUInt32();
            uint universe = reader.ReadUInt32();

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
                            byte[] dataHash = reader.ReadBytes(20);

                            int payloadSize = (int)(size - 60);
                            if (payloadSize <= 0 || fs.Position + payloadSize > stringTableOffset)
                            {
                                break;
                            }

                            byte[] data = reader.ReadBytes(payloadSize);

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
                                                onGameFound(appId.ToString(), title, false, null, 4, false);
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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamLibraryIndexer] Кеш appinfo.vdf прочитан с исключением: {ex.Message}");
        }
    }

    public static void ScanUserdataLibraryCache(
        string steamPath,
        Func<string, bool> isKnownAppId,
        Action<string, string, bool, string?, int, bool> onGameFound)
    {
        string userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir)) return;

        try
        {
            var jsonFiles = Directory.GetFiles(userdataDir, "*.json", SearchOption.AllDirectories);
            foreach (var jsonFile in jsonFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(jsonFile);
                if (uint.TryParse(fileName, out uint appId) && !isKnownAppId(appId.ToString()))
                {
                    onGameFound(appId.ToString(), $"Steam App {appId}", false, null, 4, false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SteamLibraryIndexer] Ошибка сканирования кэша библиотеки Steam: {ex.Message}");
        }
    }

    public static bool IsGameInstalled(SteamGameInfo? game, IEnumerable<string> libraryFolders)
    {
        if (game == null) return false;

        if (game.IsInstalled && game.StateFlags == 4) return true;
        if (game.IsFromManifest && (game.StateFlags == 4 || (game.StateFlags & 4) != 0)) return true;

        string manifestName = $"appmanifest_{game.AppId}.acf";
        foreach (var library in libraryFolders)
        {
            string manifestPath = Path.Combine(library, "steamapps", manifestName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    string content = File.ReadAllText(manifestPath);
                    var match = Regex.Match(content, @"""StateFlags""\s+""(\d+)""", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int flags))
                    {
                        if (flags == 4 || (flags & 4) != 0)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Gem.Core.JarvisLog.Debug("SteamLibraryIndexer", $"Error reading manifest {manifestPath}: {ex.Message}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(game.InstallPath).Any())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Gem.Core.JarvisLog.Debug("SteamLibraryIndexer", $"Error inspecting install directory '{game.InstallPath}': {ex.Message}");
            }
        }

        return false;
    }
}
