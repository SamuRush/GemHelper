using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gem.Handlers;
using Gem.Win32;
using Windows.Media.Control;

namespace Gem.Services;

/// <summary>
/// Production implementation of IMediaSessionService integrating with Windows Media Session (GSMTC),
/// process management, and active window video/music controls.
/// </summary>
public class MediaSessionService : IMediaSessionService
{
    private static readonly Lazy<MediaSessionService> _instance = new(() => new MediaSessionService());
    public static MediaSessionService Instance => _instance.Value;

    public int InitializationDelayMs { get; set; } = 1500;

    public static readonly string[] MusicProcessNames = new[]
    {
        "YandexMusic",
        "yandexmusic",
        "Yandex.Music",
        "yandex-music"
    };

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "browser", // Yandex Browser executable is browser.exe
        "opera",
        "opera_gx",
        "brave",
        "vivaldi"
    };

    private static readonly HashSet<string> MediaPlayerProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc",
        "mpc-hc",
        "mpc-hc64",
        "mpc-be",
        "mpc-be64",
        "wmplayer",
        "potplayer",
        "potplayermini",
        "potplayermini64",
        "kmplayer"
    };

    /// <summary>
    /// Checks if a process name corresponds to a supported browser or media player.
    /// </summary>
    public static bool IsVideoProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }
        return BrowserProcessNames.Contains(norm) || MediaPlayerProcessNames.Contains(norm);
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> GetSessionManagerAsync()
    {
        try
        {
            return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] RequestAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Queries the current playback status via Windows GSMTC.
    /// </summary>
    public async Task<MediaPlaybackStatus> GetPlaybackStatusAsync()
    {
        try
        {
            var manager = await GetSessionManagerAsync();
            if (manager != null)
            {
                var currentSession = manager.GetCurrentSession();
                if (currentSession != null)
                {
                    var playbackInfo = currentSession.GetPlaybackInfo();
                    if (playbackInfo != null)
                    {
                        return MapPlaybackStatus(playbackInfo.PlaybackStatus);
                    }
                }

                // If GetCurrentSession() is null, inspect any active sessions
                var sessions = manager.GetSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    foreach (var session in sessions)
                    {
                        var info = session.GetPlaybackInfo();
                        if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            return MediaPlaybackStatus.Playing;
                        }
                    }

                    foreach (var session in sessions)
                    {
                        var info = session.GetPlaybackInfo();
                        if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                        {
                            return MediaPlaybackStatus.Paused;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] GetPlaybackStatusAsync failed: {ex.Message}");
        }

        return MediaPlaybackStatus.Unknown;
    }

    private static MediaPlaybackStatus MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Closed,
            _ => MediaPlaybackStatus.Unknown
        };
    }

    /// <summary>
    /// Starts playback idempotently. If already playing, does not toggle off.
    /// </summary>
    public async Task<bool> PlayAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetPlaybackStatusAsync();
        if (status == MediaPlaybackStatus.Playing)
        {
            Debug.WriteLine("[GSMTC] Already playing. Skipping duplicate play command.");
            return true;
        }

        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session != null)
            {
                bool success = await session.TryPlayAsync();
                if (success) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] TryPlayAsync failed: {ex.Message}");
        }

        return MediaKeyService.PlayPause();
    }

    /// <summary>
    /// Pauses playback idempotently. If already paused or stopped, does not toggle on.
    /// </summary>
    public async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetPlaybackStatusAsync();
        if (status == MediaPlaybackStatus.Paused || status == MediaPlaybackStatus.Stopped)
        {
            Debug.WriteLine("[GSMTC] Already paused or stopped. Skipping duplicate pause command to prevent accidental playback.");
            return true;
        }

        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session != null)
            {
                bool success = await session.TryPauseAsync();
                if (success) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] TryPauseAsync failed: {ex.Message}");
        }

        return MediaKeyService.PlayPause();
    }

    /// <summary>
    /// Stops playback.
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetPlaybackStatusAsync();
        if (status == MediaPlaybackStatus.Stopped || status == MediaPlaybackStatus.Closed)
        {
            return true;
        }

        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session != null)
            {
                bool success = await session.TryStopAsync();
                if (success) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] TryStopAsync failed: {ex.Message}");
        }

        return MediaKeyService.Stop();
    }

    public async Task<bool> NextTrackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session != null)
            {
                bool success = await session.TrySkipNextAsync();
                if (success) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] TrySkipNextAsync failed: {ex.Message}");
        }

        return MediaKeyService.NextTrack();
    }

    public async Task<bool> PreviousTrackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await GetSessionManagerAsync();
            var session = manager?.GetCurrentSession();
            if (session != null)
            {
                bool success = await session.TrySkipPreviousAsync();
                if (success) return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSMTC Warning] TrySkipPreviousAsync failed: {ex.Message}");
        }

        return MediaKeyService.PreviousTrack();
    }

    /// <summary>
    /// Scenario 1: «Включи / поставь музыку»:
    /// 1. Проверяет через Process.GetProcessesByName, запущено ли целевое музыкальное приложение (Яндекс Музыка / yandexmusic).
    /// 2. Если не запущено: запускает его через Process.Start (или протокол yandexmusic://, либо в браузере https://music.yandex.ru),
    ///    ждет инициализацию и начинает воспроизведение.
    /// 3. Если запущено: фокусирует окно и отправляет сигнал воспроизведения.
    /// </summary>
    public async Task<bool> PlayMusicAsync(string appOrTarget = "yandexmusic", CancellationToken cancellationToken = default)
    {
        var runningProcess = FindRunningMusicProcess();
        if (runningProcess != null)
        {
            try
            {
                IntPtr hWnd = runningProcess.MainWindowHandle;
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindowForProcess(runningProcess.Id);
                }

                if (hWnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaSessionService Warning] Error focusing music window: {ex.Message}");
            }
            finally
            {
                runningProcess.Dispose();
            }

            return await PlayAsync(cancellationToken);
        }

        // Process is not running: launch via protocol or browser fallback
        bool launched = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "yandexmusic://",
                UseShellExecute = true
            };
            var proc = Process.Start(startInfo);
            launched = true;
            proc?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Failed to launch yandexmusic://: {ex.Message}");
        }

        if (!launched)
        {
            try
            {
                var browserStartInfo = AppControlCommandHandler.GetDefaultBrowserStartInfo("https://music.yandex.ru");
                var proc = Process.Start(browserStartInfo);
                launched = true;
                proc?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaSessionService] Failed to open music in browser: {ex.Message}");
            }
        }

        if (InitializationDelayMs > 0)
        {
            try
            {
                await Task.Delay(InitializationDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        await PlayAsync(cancellationToken);
        return launched;
    }

    /// <summary>
    /// Scenario 2: «Останови видео / видео на паузу / продолжи»:
    /// 1. Определяет окно на переднем плане через GetForegroundWindow / GetWindowText / process name.
    /// 2. Если активен браузер (Chrome, Edge, Firefox, Yandex) или медиаплеер (VLC, MPC) —
    ///    отправляет пробел (VK_SPACE) или адресную команду в целевое окно.
    /// </summary>
    public async Task<bool> ControlVideoAsync(bool pause, CancellationToken cancellationToken = default)
    {
        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        if (fgWnd != IntPtr.Zero)
        {
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(fgWnd, sb, sb.Capacity);
            string title = sb.ToString();

            NativeMethods.GetWindowThreadProcessId(fgWnd, out uint pid);
            string processName = "";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaSessionService] Could not get foreground process: {ex.Message}");
            }

            bool isTargetProcess = IsVideoProcessName(processName);
            bool isTargetTitle = title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("VK Video", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("ВК Видео", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("Кинопоиск", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("RuTube", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("VLC", StringComparison.OrdinalIgnoreCase) ||
                                 title.Contains("Media Player", StringComparison.OrdinalIgnoreCase);

            if (isTargetProcess || isTargetTitle)
            {
                Debug.WriteLine($"[MediaSessionService] Foreground window is video target: Process='{processName}', Title='{title}'. Sending VK_SPACE.");
                return MediaKeyService.SendMediaKey(NativeConstants.VK_SPACE);
            }
        }

        // Fallback if foreground window is not a browser or media player
        return pause ? await PauseAsync(cancellationToken) : await PlayAsync(cancellationToken);
    }

    public static Process? FindRunningMusicProcess()
    {
        foreach (var name in MusicProcessNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaSessionService] Error getting processes for '{name}': {ex.Message}");
                continue;
            }

            if (procs.Length > 0)
            {
                var chosen = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs[0];
                foreach (var p in procs)
                {
                    if (p != chosen) p.Dispose();
                }
                return chosen;
            }
        }
        return null;
    }

    private static IntPtr FindWindowForProcess(int processId)
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == processId)
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] EnumWindows error: {ex.Message}");
        }

        return found;
    }
}
