using System.Diagnostics;
using Gem.Handlers;
using Windows.Media.Control;

namespace Gem.Services.Media;

/// <summary>
/// Controls media playback via Windows Global System Media Transport Controls (GSMTC).
/// </summary>
public static class GsmtcMediaController
{
    public static async Task<GlobalSystemMediaTransportControlsSessionManager?> GetSessionManagerAsync()
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

    public static async Task<MediaPlaybackStatus> GetPlaybackStatusAsync()
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

    public static MediaPlaybackStatus MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
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

    public static async Task<bool> PlayAsync(CancellationToken cancellationToken = default)
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

    public static async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetPlaybackStatusAsync();
        if (status == MediaPlaybackStatus.Paused || status == MediaPlaybackStatus.Stopped)
        {
            Debug.WriteLine("[GSMTC] Already paused or stopped. Skipping duplicate pause command.");
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

    public static async Task<bool> StopAsync(CancellationToken cancellationToken = default)
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

    public static async Task<bool> NextTrackAsync(CancellationToken cancellationToken = default)
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

    public static async Task<bool> PreviousTrackAsync(CancellationToken cancellationToken = default)
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
}
