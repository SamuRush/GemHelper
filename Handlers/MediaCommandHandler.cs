using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Services;

namespace Gem.Handlers;

/// <summary>
/// Command handler for multimedia playback control with GSMTC state inspection,
/// scenario separation (Video vs Music), and targeted playback signals.
/// </summary>
public sealed class MediaCommandHandler : ICommandHandler
{
    public string CommandName => "media";

    private readonly IMediaSessionService? _mediaSessionService;

    public MediaCommandHandler(IMediaSessionService? mediaSessionService = null)
    {
        _mediaSessionService = mediaSessionService;
    }

    private IMediaSessionService MediaSession => _mediaSessionService ?? MediaSessionService.Instance;

    public async Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "play_pause";
        string targetApp = "yandexmusic";

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("action", out var actProp) && actProp.ValueKind == JsonValueKind.String)
            {
                action = actProp.GetString()?.ToLowerInvariant() ?? "play_pause";
            }
            else if (args.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
            {
                string key = keyProp.GetString()?.ToLowerInvariant() ?? "";
                action = key switch
                {
                    "media_next" => "next",
                    "media_prev" => "prev",
                    "media_stop" => "stop",
                    _ => "play_pause"
                };
            }

            if (args.TryGetProperty("app", out var appProp) && appProp.ValueKind == JsonValueKind.String)
            {
                targetApp = appProp.GetString()?.ToLowerInvariant() ?? "yandexmusic";
            }
        }
        else if (args.ValueKind == JsonValueKind.String)
        {
            action = args.GetString()?.ToLowerInvariant() ?? "play_pause";
        }

        bool success;
        string detail;

        JarvisLog.Info("MediaCommandHandler", $"Запуск действия '{action}' (цель: '{targetApp}')");

        switch (action)
        {
            case "pause_video":
                success = await MediaSession.ControlVideoAsync(pause: true, cancellationToken);
                detail = "Пауза активного видео";
                break;

            case "resume_video":
            case "play_video":
                success = await MediaSession.ControlVideoAsync(pause: false, cancellationToken);
                detail = "Возобновление активного видео";
                break;

            case "play_music":
                success = await MediaSession.PlayMusicAsync(targetApp, cancellationToken);
                detail = $"Воспроизведение музыки ({targetApp})";
                break;

            case "pause":
                success = await MediaSession.PauseAsync(cancellationToken);
                detail = "Приостановка воспроизведения (GSMTC Pause)";
                break;

            case "play":
            case "resume":
                success = await MediaSession.PlayAsync(cancellationToken);
                detail = "Возобновление воспроизведения (GSMTC Play)";
                break;

            case "play_pause":
            case "toggle":
            case "media_play_pause":
                var currentStatus = await MediaSession.GetPlaybackStatusAsync();
                if (currentStatus == MediaPlaybackStatus.Playing)
                {
                    success = await MediaSession.PauseAsync(cancellationToken);
                    detail = "Умный Play/Pause -> Приостановка";
                }
                else
                {
                    success = await MediaSession.PlayAsync(cancellationToken);
                    detail = "Умный Play/Pause -> Воспроизведение";
                }
                break;

            case "next":
            case "next_track":
            case "media_next":
                success = await MediaSession.NextTrackAsync(cancellationToken);
                detail = "Следующий трек";
                break;

            case "prev":
            case "previous":
            case "prev_track":
            case "media_prev":
                success = await MediaSession.PreviousTrackAsync(cancellationToken);
                detail = "Предыдущий трек";
                break;

            case "stop":
            case "media_stop":
                success = await MediaSession.StopAsync(cancellationToken);
                detail = "Остановка воспроизведения";
                break;

            default:
                return CommandResult.Fail($"Unknown media action: '{action}'.");
        }

        return success
            ? CommandResult.Ok($"Successfully executed media action: {detail}.")
            : CommandResult.Fail($"Failed executing media action: {detail}.");
    }
}
