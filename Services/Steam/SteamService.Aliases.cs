using System;
using System.Collections.Generic;
using Gem.Voice;

namespace Gem.Services;

public sealed partial class SteamService
{
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

    private void AddOrUpdateGame(string appId, string title, bool isInstalled, string? installDir = null, int stateFlags = 4, bool isFromManifest = false)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(title)) return;
        if (IsServiceEntry(title)) return;

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

    public void RegisterInstalledGameForTesting(string appId, string title, string? installDir = null)
    {
        lock (_lock)
        {
            AddOrUpdateGame(appId, title, isInstalled: true, installDir, stateFlags: 4, isFromManifest: true);
        }
    }
}
