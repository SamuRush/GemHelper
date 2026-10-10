using System;
using System.Collections.Generic;

namespace Gem.Services.App;

/// <summary>
/// Catalogs and alias dictionaries for Steam games, processes, and applications.
/// </summary>
public static class AppCatalog
{
    // Словарь соответствия игр Steam (StringComparer.OrdinalIgnoreCase)
    public static readonly Dictionary<string, string> SteamGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Monster Hunter Wilds: ключи "mhw_wilds", "wilds", "вайлдс" -> AppID "2246340"
        ["mhw_wilds"]             = "2246340",
        ["wilds"]                 = "2246340",
        ["вайлдс"]                = "2246340",
        ["уайлд"]                 = "2246340",
        ["уайлдс"]                = "2246340",
        ["монстер хантер вайлдс"] = "2246340",
        ["монстер хантер уайлд"]  = "2246340",

        // Monster Hunter World: ключи "mhw_world", "world", "ворлд", "монстер хантер", "монстер хантер ворлд" -> AppID "582010"
        ["mhw_world"]             = "582010",
        ["world"]                 = "582010",
        ["ворлд"]                 = "582010",
        ["монстер хантер"]        = "582010",
        ["монстер хантер ворлд"]  = "582010",

        // Dota 2
        ["дота"]                  = "570",
        ["dota"]                  = "570",

        // CS
        ["кс"]                    = "730",
        ["cs"]                    = "730",
        ["контра"]                = "730",

        // Garry's Mod
        ["гарри мод"]             = "4000",
        ["гаррис мод"]            = "4000",
        ["garrys mod"]            = "4000",
        ["garry's mod"]           = "4000",
        ["gmod"]                  = "4000",
    };

    // Словарь соответствия процессов игр для завершения через Process.Kill()
    public static readonly Dictionary<string, string> SteamProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mhw_wilds"]             = "MonsterHunterWilds",
        ["mhw_world"]             = "MonsterHunterWorld",
        ["wilds"]                 = "MonsterHunterWilds",
        ["вайлдс"]                = "MonsterHunterWilds",
        ["уайлд"]                 = "MonsterHunterWilds",
        ["уайлдс"]                = "MonsterHunterWilds",
        ["world"]                 = "MonsterHunterWorld",
        ["ворлд"]                 = "MonsterHunterWorld",
        ["монстер хантер"]        = "MonsterHunterWorld",
        ["монстер хантер вайлдс"] = "MonsterHunterWilds",
        ["монстер хантер уайлд"]  = "MonsterHunterWilds",
        ["монстер хантер ворлд"]  = "MonsterHunterWorld",
        ["MonsterHunterWilds"]    = "MonsterHunterWilds",
        ["MonsterHunterWorld"]    = "MonsterHunterWorld",
        ["гарри мод"]             = "hl2",
        ["гаррис мод"]            = "hl2",
        ["gmod"]                  = "hl2",
    };

    // Обычные приложения
    public static readonly Dictionary<string, string> AppTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Steam
        ["steam"]    = "steam://open/main",
        ["стим"]     = "steam://open/main",
        ["тим"]      = "steam://open/main",
        ["тем"]      = "steam://open/main",

        // Brave / Chrome
        ["brave"]    = "brave.exe",
        ["брейв"]    = "brave.exe",
        ["браузер"]  = "brave.exe",
        ["хром"]     = "brave.exe",
        ["chrome"]   = "brave.exe",

        // Discord
        ["discord"]  = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",
        ["дискорд"]  = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",
        ["диск"]     = @"%LocalAppData%\Discord\Update.exe --processStart Discord.exe",

        // Notepad
        ["notepad"]  = "notepad.exe",
        ["блокнот"]  = "notepad.exe",

        // Calc
        ["calc"]     = "calc.exe",
        ["калькулятор"] = "calc.exe",
    };

    public static readonly HashSet<string> ExitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "jarvis", "джарвис", "себя", "ассистент", "gem"
    };

    /// <summary>
    /// Проверяет, является ли имя указанием на завершение работы самого ассистента JARVIS.
    /// </summary>
    public static bool IsExitTarget(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim().ToLowerInvariant();
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }
        return ExitNames.Contains(key);
    }

    /// <summary>
    /// Обновляет словарь игр Steam из настроек.
    /// </summary>
    public static void SetSteamGames(IDictionary<string, string> games)
    {
        foreach (var kv in games)
        {
            SteamGames[kv.Key] = kv.Value;
        }
    }
}
