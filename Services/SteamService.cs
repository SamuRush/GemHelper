using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Services.Steam;
using Gem.Voice;

namespace Gem.Services;

/// <summary>
/// Universal Steam service providing library indexing, phonetic search,
/// game installation, launch, and uninstallation.
/// </summary>
public sealed partial class SteamService
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

            LoadConfigAliases();

            _steamPath = SteamLibraryIndexer.LocateSteamPath();
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

            SteamLibraryIndexer.DiscoverLibraryFolders(_steamPath, _libraryFolders);
            SteamLibraryIndexer.ScanAppManifests(_libraryFolders, AddOrUpdateGame);
            SteamLibraryIndexer.ScanAppInfoCache(_steamPath, AddOrUpdateGame);
            SteamLibraryIndexer.ScanUserdataLibraryCache(_steamPath, appId => _gamesByAppId.ContainsKey(appId), AddOrUpdateGame);
            RegisterFallbackAliases();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamService] Библиотека Steam проиндексирована: {_games.Count} игр ({_games.Count(g => g.IsInstalled)} установлено).");
            Console.ResetColor();
        }
    }

    public static bool IsServiceEntry(string? title) => SteamLibraryIndexer.IsServiceEntry(title);

    public static string NormalizeGameTitle(string raw) => SteamPhonetics.NormalizeGameTitle(raw);
    public static string TransliterateToCyrillic(string english) => SteamPhonetics.TransliterateToCyrillic(english);
    public static string Transliterate(string text) => SteamPhonetics.Transliterate(text);
    public static int LevenshteinDistance(string s1, string s2) => SteamPhonetics.LevenshteinDistance(s1, s2);
    public static double CalculateSimilarity(string s1, string s2) => SteamPhonetics.CalculateSimilarity(s1, s2);

    public bool IsGameInstalled(SteamGameInfo? game) => SteamLibraryIndexer.IsGameInstalled(game, _libraryFolders);

    public Task InstallGameAsync(SteamGameInfo game)
    {
        if (IsGameInstalled(game))
        {
            JarvisLog.Info("SteamService", $"Игра '{game.Title}' уже установлена (StateFlags == 4 / файлы присутствуют на диске). Отмена установки.");
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Start, game);
            VoiceFeedbackService.Instance?.SpeakAsync("Эта игра уже установлена, сэр. Запустить её?");
            return Task.CompletedTask;
        }

        LaunchInstallation(game);
        return Task.CompletedTask;
    }

    public void LaunchInstallation(SteamGameInfo game) => SteamInstaller.LaunchInstallation(game);

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

        if (IsGameInstalled(search.Game))
        {
            JarvisLog.Info("SteamService", $"Игра '{search.Game.Title}' уже установлена (StateFlags == 4 / файлы присутствуют на диске). Отмена установки.");
            JarvisOrchestrator.Instance.SetPendingConfirmation(PendingGameAction.Start, search.Game);
            VoiceFeedbackService.Instance?.SpeakAsync("Эта игра уже установлена, сэр. Запустить её?");
            return false;
        }

        onFound(search.Game.Title);
        LaunchInstallation(search.Game);
        return true;
    }

    public static IntPtr FindMainSteamWindow() => SteamUiAutomation.FindMainSteamWindow();
    public static void ForceForegroundWindow(IntPtr hWnd) => SteamUiAutomation.ForceForegroundWindow(hWnd);
    public static Task ClickMouseAtAsync(Point pt) => SteamUiAutomation.ClickMouseAtAsync(pt);

    public static bool IsEulaDialogTitle(string? windowTitle) => SteamUiAutomation.IsEulaDialogTitle(windowTitle);
    public static bool IsEulaButtonName(string? buttonText) => SteamUiAutomation.IsEulaButtonName(buttonText);
    public static Point? FindEulaButtonViaUiAutomation(IntPtr hWnd) => SteamUiAutomation.FindEulaButtonViaUiAutomation(hWnd);
    public static Task<Point?> TryConfirmEulaViaUiAutomationAsync(IntPtr hWnd) => SteamUiAutomation.TryConfirmEulaViaUiAutomationAsync(hWnd);

    public static bool IsMatchingButtonName(string? name, string[]? targetNames = null) =>
        SteamUiAutomation.IsMatchingButtonName(name, targetNames);

    public static bool IsMatchingDialogTitle(string? windowTitle, string? gameTitle = null) =>
        SteamUiAutomation.IsMatchingDialogTitle(windowTitle, gameTitle);

    public static Point? FindSteamButtonViaUiAutomation(IntPtr hWnd, string[]? targetButtonNames = null) =>
        SteamUiAutomation.FindSteamButtonViaUiAutomation(hWnd, targetButtonNames);

    public static Point? FindSteamBlueButtonInWindow(IntPtr hWnd) => SteamUiAutomation.FindSteamBlueButtonInWindow(hWnd);
    public static Point? FindSteamUninstallButton(IntPtr hWnd) => SteamUiAutomation.FindSteamUninstallButton(hWnd);

    public static Task<bool> PollAndConfirmInstallDialogAsync() => SteamInstaller.PollAndConfirmInstallDialogAsync();

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

    public static async Task<bool> UninstallGameAsync(uint appId, string title)
    {
        return await SteamInstaller.UninstallGameAsync(appId, title, id =>
        {
            lock (Instance._lock)
            {
                if (Instance._gamesByAppId.TryGetValue(id.ToString(), out var g))
                {
                    var updated = g with { IsInstalled = false, InstallPath = null };
                    Instance._gamesByAppId[id.ToString()] = updated;
                    int idx = Instance._games.FindIndex(x => x.AppId == id.ToString());
                    if (idx >= 0) Instance._games[idx] = updated;
                }
            }
        });
    }

    public static Task<bool> UninstallGameAsync(string appId, string title)
    {
        return uint.TryParse(appId, out uint numericId)
            ? UninstallGameAsync(numericId, title)
            : Task.FromResult(false);
    }

    public static IntPtr FindSteamUninstallDialog(string title) => SteamInstaller.FindSteamUninstallDialog(title);
    public static string GetWindowTitle(IntPtr hWnd) => SteamInstaller.GetWindowTitle(hWnd);
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
