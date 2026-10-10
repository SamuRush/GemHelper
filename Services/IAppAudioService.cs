using System.Threading.Tasks;

namespace Gem.Services;

/// <summary>
/// Result of an application audio volume operation.
/// </summary>
public record AppVolumeResult(
    bool Success,
    string Message,
    int Level = 0,
    float Scalar = 0.0f,
    bool IsMuted = false,
    string? AppName = null
);

/// <summary>
/// Service abstraction for controlling audio session volume for individual applications
/// (e.g. Yandex Music, media players, browsers) without affecting the Windows master volume.
/// </summary>
public interface IAppAudioService
{
    /// <summary>
    /// Sets the volume scalar (0.0 to 1.0) for the target player/application.
    /// </summary>
    Task<AppVolumeResult> SetAppVolumeAsync(string target, float scalar);

    /// <summary>
    /// Adjusts the volume scalar by delta for the target player/application.
    /// </summary>
    Task<AppVolumeResult> ChangeAppVolumeAsync(string target, float deltaScalar);

    /// <summary>
    /// Mutes or unmutes the target player/application.
    /// </summary>
    Task<AppVolumeResult> SetAppMuteAsync(string target, bool? isMuted);

    /// <summary>
    /// Gets current volume level and mute state for the target player/application.
    /// </summary>
    Task<AppVolumeResult> GetAppVolumeAsync(string target);
}
