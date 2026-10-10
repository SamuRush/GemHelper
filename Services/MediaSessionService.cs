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
using System.Windows.Automation;

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
    /// Checks if a process name corresponds to a supported browser.
    /// </summary>
    public static bool IsBrowserProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }
        return BrowserProcessNames.Contains(norm);
    }

    /// <summary>
    /// Checks if a process name corresponds to a supported media player.
    /// </summary>
    public static bool IsMediaPlayerProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }
        return MediaPlayerProcessNames.Contains(norm);
    }

    /// <summary>
    /// Checks if a process name corresponds to a supported browser or media player.
    /// </summary>
    public static bool IsVideoProcessName(string processName)
    {
        return IsBrowserProcessName(processName) || IsMediaPlayerProcessName(processName);
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
    /// 1. Изоляция контекста: если в данный момент играет видео (в браузере или медиаплеере), сначала ставит его на паузу (GSMTC / VK_SPACE).
    /// 2. Проверяет через Process.GetProcessesByName, запущено ли целевое музыкальное приложение (Яндекс Музыка / yandexmusic).
    /// 3. Если не запущено: запускает его через Process.Start (или протокол yandexmusic://, либо в браузере https://music.yandex.ru),
    ///    ждет инициализацию и начинает воспроизведение.
    /// 4. Если запущено: фокусирует окно и отправляет сигнал воспроизведения.
    /// </summary>
    public async Task<bool> PlayMusicAsync(string appOrTarget = "yandexmusic", CancellationToken cancellationToken = default)
    {
        // 1. Context isolation: If video or browser media is currently playing, pause it first
        var (videoFound, videoWnd, videoSession) = await FindActiveVideoStreamAsync();
        if (videoFound)
        {
            JarvisLog.Info("MediaSessionService", "Изоляция контекста медиа: Обнаружено активное видео. Ставим его на паузу перед запуском музыки...");
            if (videoWnd != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.SetForegroundWindow(videoWnd);
                    MediaKeyService.SendMediaKey(NativeConstants.VK_SPACE);
                }
                catch (Exception ex)
                {
                    JarvisLog.Debug("MediaSessionService", $"Ошибка отправки VK_SPACE в окно видео: {ex.Message}");
                }
            }
            else if (videoSession != null)
            {
                try
                {
                    await videoSession.TryPauseAsync();
                }
                catch (Exception ex)
                {
                    JarvisLog.Debug("MediaSessionService", $"Ошибка паузы видеосессии GSMTC: {ex.Message}");
                }
            }
            else
            {
                await PauseAsync(cancellationToken);
            }

            // Задержка для применения паузы системой
            await Task.Delay(250, cancellationToken);
        }

        // 2. Поиск и фокус существующего музыкального плеера
        var runningProcess = FindRunningMusicProcess();
        if (runningProcess != null)
        {
            IntPtr hWnd = IntPtr.Zero;
            try
            {
                hWnd = runningProcess.MainWindowHandle;
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = FindWindowForProcess(runningProcess.Id);
                }

                if (hWnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hWnd);
                    await Task.Delay(150, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("MediaSessionService", $"Error focusing music window: {ex.Message}");
            }
            finally
            {
                runningProcess.Dispose();
            }

            if (hWnd != IntPtr.Zero)
            {
                MediaKeyService.SendMediaKey(NativeConstants.VK_SPACE);
                return true;
            }

            return await PlayAsync(cancellationToken);
        }

        // 3. Process is not running: launch via protocol or browser fallback
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
            JarvisLog.Debug("MediaSessionService", $"Failed to launch yandexmusic://: {ex.Message}");
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
                JarvisLog.Debug("MediaSessionService", $"Failed to open music in browser: {ex.Message}");
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

        var newMusicProc = FindRunningMusicProcess();
        if (newMusicProc != null)
        {
            try
            {
                IntPtr hWnd = newMusicProc.MainWindowHandle;
                if (hWnd == IntPtr.Zero) hWnd = FindWindowForProcess(newMusicProc.Id);
                if (hWnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hWnd);
                    await Task.Delay(150, cancellationToken);
                    MediaKeyService.SendMediaKey(NativeConstants.VK_SPACE);
                    return true;
                }
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("MediaSessionService", $"Error focusing newly launched music process: {ex.Message}");
            }
            finally
            {
                newMusicProc.Dispose();
            }
        }

        await PlayAsync(cancellationToken);
        return launched;
    }

    /// <summary>
    /// Checks if a window title contains video platform keywords or media file extensions.
    /// </summary>
    public static bool IsVideoTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        return title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("ютуб", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("VK Video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("ВК Видео", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("vk.com/video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Кинопоиск", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("kinopoisk", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("твич", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("RuTube", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("рутуб", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("нетфликс", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Дзен", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Dzen", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Okko", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("окко", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Ivi", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("иви", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Premier", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("премьер", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Vimeo", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(" - Видео", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(" - Video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mkv", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".avi", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a window handle corresponds to an active dedicated media player or a browser tab playing video.
    /// Invariant: A browser window is ONLY considered a video window if its title explicitly matches a video platform
    /// or video media title. Blank or search tabs ("Новая вкладка", "Google Chrome", etc.) are strictly rejected.
    /// </summary>
    public static bool IsVideoWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        string processName = "";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Error getting process for window {hWnd}: {ex.Message}");
            return false;
        }

        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title)) return false;

        // Dedicated media players (VLC, MPC, PotPlayer, etc.)
        if (IsMediaPlayerProcessName(processName))
        {
            return true;
        }

        // Browsers: MUST have a video platform or video title. Blank tabs must never match!
        if (IsBrowserProcessName(processName))
        {
            return IsVideoTitle(title);
        }

        return IsVideoTitle(title);
    }

    /// <summary>
    /// Checks if a window belongs to any supported web browser process.
    /// </summary>
    public static bool IsBrowserWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hWnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return IsBrowserProcessName(proc.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the active or running window of a dedicated media player (VLC, MPC) or a browser with an active video tab.
    /// </summary>
    public static IntPtr FindActiveVideoWindow()
    {
        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        if (fgWnd != IntPtr.Zero && IsVideoWindow(fgWnd))
        {
            return fgWnd;
        }

        IntPtr foundWnd = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (IsVideoWindow(hWnd))
                {
                    foundWnd = hWnd;
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Error enumerating video windows: {ex.Message}");
        }

        return foundWnd;
    }

    /// <summary>
    /// Finds a visible window for the given process name or AppUserModelId.
    /// </summary>
    public static IntPtr FindWindowForProcessName(string procNameOrAppId)
    {
        if (string.IsNullOrWhiteSpace(procNameOrAppId)) return IntPtr.Zero;
        string norm = procNameOrAppId.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }

        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            if (proc.ProcessName.Equals(norm, StringComparison.OrdinalIgnoreCase))
                            {
                                found = hWnd;
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            JarvisLog.Debug("MediaSessionService", $"Process lookup error for PID {pid}: {ex.Message}");
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] FindWindowForProcessName error: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Finds a visible browser window that contains "YouTube" in its title.
    /// </summary>
    public static IntPtr FindYouTubeWindow()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0)
                    {
                        string procName = "";
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            procName = proc.ProcessName;
                        }
                        catch (Exception ex)
                        {
                            JarvisLog.Debug("MediaSessionService", $"Process lookup error for PID {pid}: {ex.Message}");
                        }

                        if (IsBrowserProcessName(procName))
                        {
                            var sb = new StringBuilder(512);
                            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                            string title = sb.ToString();
                            if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("ютуб", StringComparison.OrdinalIgnoreCase))
                            {
                                found = hWnd;
                                return false;
                            }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Error finding YouTube window: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Checks for an active video stream across:
    /// 1. Window titles of visible dedicated media players and video-streaming browser tabs.
    /// 2. Windows GSMTC media sessions with Playing or Paused state for video apps (excluding music).
    /// 3. WASAPI active audio playback streams for video/browser apps.
    /// </summary>
    public async Task<(bool Found, IntPtr WindowHandle, GlobalSystemMediaTransportControlsSession? Session)> FindActiveVideoStreamAsync()
    {
        // 1. Direct window check (foreground first, then enumerated visible windows)
        IntPtr win = FindActiveVideoWindow();
        if (win != IntPtr.Zero)
        {
            return (true, win, null);
        }

        // 2. Windows GSMTC check
        try
        {
            var manager = await GetSessionManagerAsync();
            if (manager != null)
            {
                var sessions = manager.GetSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    foreach (var s in sessions)
                    {
                        string appId = s.SourceAppUserModelId ?? "";
                        if (AppAudioService.IsMusicProcessName(appId))
                        {
                            continue; // Music player, not video
                        }

                        if (IsVideoProcessName(appId))
                        {
                            var info = s.GetPlaybackInfo();
                            if (info != null && (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                                                 info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused))
                            {
                                IntPtr targetWin = FindWindowForProcessName(appId);
                                return (true, targetWin, s);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] GSMTC video stream check error: {ex.Message}");
        }

        // 3. WASAPI audio session check
        try
        {
            var sessions = AppAudioService.FindSessions("video");
            try
            {
                foreach (var item in sessions)
                {
                    bool isPlayingAudio = false;
                    try
                    {
                        if (item.Session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive &&
                            item.Session.AudioMeterInformation.MasterPeakValue > 0.0001f)
                        {
                            isPlayingAudio = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        JarvisLog.Debug("MediaSessionService", $"Error querying AudioMeterInformation: {ex.Message}");
                    }

                    if (isPlayingAudio)
                    {
                        IntPtr targetWin = FindWindowForProcess((int)item.Pid);
                        if (targetWin != IntPtr.Zero)
                        {
                            return (true, targetWin, null);
                        }
                    }
                }
            }
            finally
            {
                foreach (var item in sessions)
                {
                    try { item.Session.Dispose(); }
                    catch (Exception ex)
                    {
                        JarvisLog.Debug("MediaSessionService", $"Error disposing session: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Audio session video stream check error: {ex.Message}");
        }

        return (false, IntPtr.Zero, null);
    }

    private static readonly HashSet<string> YouTubeNavigationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "YouTube", "Главная", "Home", "Shorts", "Подписки", "Subscriptions",
        "Вы", "You", "История", "History", "Мои видео", "Your videos",
        "Смотреть позже", "Watch later", "Понравившиеся", "Liked videos",
        "В тренде", "Trending", "Музыка", "Music", "Игры", "Gaming",
        "Новости", "News", "Спорт", "Sports", "Подкасты", "Podcasts",
        "Войти", "Sign in", "Настройки", "Settings", "Пожаловаться",
        "Справка", "Help", "Отправить отзыв", "Send feedback",
        "Все", "All", "Видеоигры", "В эфире", "Live", "Недавно опубликованные",
        "Просмотрено", "Новое для вас", "Поиск", "Search", "Поиск с помощью голоса"
    };

    /// <summary>
    /// Searches for and clicks the first recommended video link on YouTube using Win32 UI Automation.
    /// </summary>
    public static bool TryClickFirstRecommendedVideoViaUiAutomation(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            var windowElement = AutomationElement.FromHandle(hWnd);
            if (windowElement == null) return false;

            var linkCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
            var links = windowElement.FindAll(TreeScope.Descendants, linkCondition);

            foreach (AutomationElement link in links)
            {
                try
                {
                    string name = link.Current.Name?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (YouTubeNavigationNames.Contains(name) || name.Length < 3) continue;

                    var rect = link.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 50 || rect.Height < 20) continue;

                    // Exclude header bar and narrow left sidebar
                    if (rect.Top < 100 || rect.Left < 100) continue;

                    if (link.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj))
                    {
                        ((InvokePattern)invokeObj).Invoke();
                        return true;
                    }

                    int centerX = (int)(rect.Left + rect.Width / 2);
                    int centerY = (int)(rect.Top + rect.Height / 2);
                    _ = NativeMethods.ClickAtAsync(centerX, centerY);
                    return true;
                }
                catch (Exception ex)
                {
                    JarvisLog.Debug("MediaSessionService", $"Error processing video link element: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService UI Automation] Error: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Launches YouTube in the default browser and triggers the first recommended video.
    /// </summary>
    public async Task<bool> LaunchYouTubeAndPlayFirstRecommendedAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("[MediaSessionService] No active video stream. Launching YouTube and opening first recommended video.");

        // 1. Launch default browser with YouTube home page
        var startInfo = AppControlCommandHandler.GetDefaultBrowserStartInfo("https://www.youtube.com");
        try
        {
            using var proc = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaSessionService] Error launching browser with YouTube: {ex.Message}");
            return false;
        }

        // 2. Poll for the YouTube browser window (up to 4000ms)
        IntPtr ytHwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && !cancellationToken.IsCancellationRequested)
        {
            ytHwnd = FindYouTubeWindow();
            if (ytHwnd != IntPtr.Zero)
            {
                break;
            }
            await Task.Delay(250, cancellationToken);
        }

        if (ytHwnd == IntPtr.Zero)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (IsBrowserWindow(fg))
            {
                ytHwnd = fg;
            }
        }

        if (ytHwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(ytHwnd);
        }

        // Wait for YouTube SPA recommendations grid to load
        await Task.Delay(2000, cancellationToken);

        // 3. Attempt UI Automation click on the first recommended video
        if (ytHwnd != IntPtr.Zero)
        {
            bool clickedViaUia = TryClickFirstRecommendedVideoViaUiAutomation(ytHwnd);
            if (clickedViaUia)
            {
                Debug.WriteLine("[MediaSessionService] Successfully clicked first recommended video via UI Automation.");
                return true;
            }
        }

        // 4. Fallback: coordinate click on the first recommended video card in the client area
        if (ytHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(ytHwnd, out RECT rect))
        {
            int clickX = rect.Left + Math.Max(320, Math.Min(450, rect.Width / 3));
            int clickY = rect.Top + Math.Max(220, Math.Min(300, rect.Height / 3));
            await NativeMethods.ClickAtAsync(clickX, clickY);
            Debug.WriteLine($"[MediaSessionService] Clicked first recommended video at ({clickX}, {clickY}).");
            return true;
        }

        return true;
    }

    /// <summary>
    /// Scenario 2: «Включи видео / останови видео / видео на паузу / продолжи»:
    /// 1. Проверяет наличие активного видеопотока (локальный плеер VLC/MPC или вкладка браузера с видео).
    /// 2. Если найден активный поток — фокусирует окно и отправляет сигнал паузы/воспроизведения (VK_SPACE / GSMTC).
    /// 3. Если активного видеопотока нет:
    ///    - при команде паузы — не выполняет ложных действий;
    ///    - при команде включения — открывает YouTube по умолчанию и запускает первый ролик из рекомендованных.
    /// </summary>
    public async Task<bool> ControlVideoAsync(bool pause, CancellationToken cancellationToken = default)
    {
        var (found, videoWnd, gsmtcSession) = await FindActiveVideoStreamAsync();

        if (found)
        {
            if (videoWnd != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.SetForegroundWindow(videoWnd);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaSessionService] Error setting foreground video window: {ex.Message}");
                }

                Debug.WriteLine("[MediaSessionService] Active video window found. Sending VK_SPACE.");
                return MediaKeyService.SendMediaKey(NativeConstants.VK_SPACE);
            }

            if (gsmtcSession != null)
            {
                return pause ? await gsmtcSession.TryPauseAsync() : await gsmtcSession.TryPlayAsync();
            }

            return pause ? await PauseAsync(cancellationToken) : await PlayAsync(cancellationToken);
        }

        // If no active video stream exists:
        if (pause)
        {
            return false;
        }

        // Default fallback for "включи видео": launch YouTube and play first recommended video
        return await LaunchYouTubeAndPlayFirstRecommendedAsync(cancellationToken);
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
