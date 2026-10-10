using System.Threading;
using System.Threading.Tasks;

namespace Gem.Services;

/// <summary>
/// Status of global multimedia playback reported by Windows GSMTC or playback probes.
/// </summary>
public enum MediaPlaybackStatus
{
    Unknown = 0,
    Closed = 1,
    Opened = 2,
    Changing = 3,
    Stopped = 4,
    Playing = 5,
    Paused = 6
}

/// <summary>
/// Service abstraction for interacting with multimedia sessions, Windows GSMTC,
/// and targeting video and music applications.
/// </summary>
public interface IMediaSessionService
{
    /// <summary>
    /// Gets current playback status from Windows GSMTC.
    /// </summary>
    Task<MediaPlaybackStatus> GetPlaybackStatusAsync();

    /// <summary>
    /// Starts/resumes playback. If already playing, avoids redundant toggling.
    /// </summary>
    Task<bool> PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses playback. If already paused/stopped, avoids accidental play toggling.
    /// </summary>
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops playback.
    /// </summary>
    Task<bool> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    Task<bool> NextTrackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Goes back to previous track.
    /// </summary>
    Task<bool> PreviousTrackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Music scenario: checks if target music process (Yandex Music) is running.
    /// If not: launches it (via protocol or browser fallback), waits for initialization, and starts playback.
    /// If running: focuses window and sends playback signal.
    /// </summary>
    Task<bool> PlayMusicAsync(string appOrTarget = "yandexmusic", CancellationToken cancellationToken = default);

    /// <summary>
    /// Video scenario: checks foreground window (GetForegroundWindow / GetWindowText / process name).
    /// If browser or media player: sends VK_SPACE / pause to target window.
    /// </summary>
    Task<bool> ControlVideoAsync(bool pause, CancellationToken cancellationToken = default);
}
