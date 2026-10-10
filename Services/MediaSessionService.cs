using System.Diagnostics;
using Gem.Core;
using Gem.Handlers;
using Gem.Services.Media;
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

    public static readonly string[] MusicProcessNames = VideoWindowFinder.MusicProcessNames;

    public static bool IsBrowserProcessName(string processName) => VideoWindowFinder.IsBrowserProcessName(processName);
    public static bool IsMediaPlayerProcessName(string processName) => VideoWindowFinder.IsMediaPlayerProcessName(processName);
    public static bool IsVideoProcessName(string processName) => VideoWindowFinder.IsVideoProcessName(processName);
    public static bool IsVideoTitle(string title) => VideoWindowFinder.IsVideoTitle(title);
    public static bool IsVideoWindow(IntPtr hWnd) => VideoWindowFinder.IsVideoWindow(hWnd);
    public static bool IsBrowserWindow(IntPtr hWnd) => VideoWindowFinder.IsBrowserWindow(hWnd);
    public static IntPtr FindActiveVideoWindow() => VideoWindowFinder.FindActiveVideoWindow();
    public static IntPtr FindWindowForProcessName(string procNameOrAppId) => VideoWindowFinder.FindWindowForProcessName(procNameOrAppId);
    public static IntPtr FindYouTubeWindow() => VideoWindowFinder.FindYouTubeWindow();
    public static Process? FindRunningMusicProcess() => VideoWindowFinder.FindRunningMusicProcess();
    public static bool TryClickFirstRecommendedVideoViaUiAutomation(IntPtr hWnd) => YouTubeLauncher.TryClickFirstRecommendedVideoViaUiAutomation(hWnd);

    public Task<MediaPlaybackStatus> GetPlaybackStatusAsync() => GsmtcMediaController.GetPlaybackStatusAsync();
    public Task<bool> PlayAsync(CancellationToken cancellationToken = default) => GsmtcMediaController.PlayAsync(cancellationToken);
    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => GsmtcMediaController.PauseAsync(cancellationToken);
    public Task<bool> StopAsync(CancellationToken cancellationToken = default) => GsmtcMediaController.StopAsync(cancellationToken);
    public Task<bool> NextTrackAsync(CancellationToken cancellationToken = default) => GsmtcMediaController.NextTrackAsync(cancellationToken);
    public Task<bool> PreviousTrackAsync(CancellationToken cancellationToken = default) => GsmtcMediaController.PreviousTrackAsync(cancellationToken);

    public Task<bool> LaunchYouTubeAndPlayFirstRecommendedAsync(CancellationToken cancellationToken = default) =>
        YouTubeLauncher.LaunchYouTubeAndPlayFirstRecommendedAsync(cancellationToken);

    /// <summary>
    /// Starts music playback with context isolation (pauses any active video first).
    /// </summary>
    public async Task<bool> PlayMusicAsync(string appOrTarget = "yandexmusic", CancellationToken cancellationToken = default)
    {
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

            await Task.Delay(250, cancellationToken);
        }

        var runningProcess = FindRunningMusicProcess();
        if (runningProcess != null)
        {
            IntPtr hWnd = IntPtr.Zero;
            try
            {
                hWnd = runningProcess.MainWindowHandle;
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = VideoWindowFinder.FindWindowForProcess(runningProcess.Id);
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
                if (hWnd == IntPtr.Zero) hWnd = VideoWindowFinder.FindWindowForProcess(newMusicProc.Id);
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
    /// Checks for an active video stream across windows, GSMTC, and WASAPI sessions.
    /// </summary>
    public async Task<(bool Found, IntPtr WindowHandle, GlobalSystemMediaTransportControlsSession? Session)> FindActiveVideoStreamAsync()
    {
        IntPtr win = FindActiveVideoWindow();
        if (win != IntPtr.Zero)
        {
            return (true, win, null);
        }

        try
        {
            var manager = await GsmtcMediaController.GetSessionManagerAsync();
            if (manager != null)
            {
                var sessions = manager.GetSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    foreach (var s in sessions)
                    {
                        string appId = s.SourceAppUserModelId ?? "";
                        if (AppAudioService.IsMusicProcessName(appId)) continue;

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
                        IntPtr targetWin = VideoWindowFinder.FindWindowForProcess((int)item.Pid);
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

    /// <summary>
    /// Controls video playback (pause or play), with YouTube fallback.
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

        if (pause)
        {
            return false;
        }

        return await LaunchYouTubeAndPlayFirstRecommendedAsync(cancellationToken);
    }
}
