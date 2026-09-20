using System.Text.Json;
using Gem.Core;
using Gem.Services;
using Gem.Win32;

namespace Gem.Handlers;

/// <summary>
/// Command handler for global multimedia playback control (Play/Pause, Next, Prev, Stop).
/// Example args: { "action": "play_pause" } or { "key": "media_play_pause" }
/// </summary>
public sealed class MediaCommandHandler : ICommandHandler
{
    public string CommandName => "media";

    public Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "play_pause";
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
        }
        else if (args.ValueKind == JsonValueKind.String)
        {
            action = args.GetString()?.ToLowerInvariant() ?? "play_pause";
        }

        bool success;
        string detail;

        switch (action)
        {
            case "play_pause":
            case "toggle":
            case "play":
            case "pause":
            case "media_play_pause":
                success = MediaKeyService.PlayPause();
                detail = "VK_MEDIA_PLAY_PAUSE (0xB3)";
                break;

            case "next":
            case "next_track":
            case "media_next":
                success = MediaKeyService.NextTrack();
                detail = "VK_MEDIA_NEXT_TRACK (0xB0)";
                break;

            case "prev":
            case "previous":
            case "prev_track":
            case "media_prev":
                success = MediaKeyService.PreviousTrack();
                detail = "VK_MEDIA_PREV_TRACK (0xB1)";
                break;

            case "stop":
            case "media_stop":
                success = MediaKeyService.Stop();
                detail = "VK_MEDIA_STOP (0xB2)";
                break;

            default:
                return Task.FromResult(CommandResult.Fail($"Unknown media action: '{action}'."));
        }

        return Task.FromResult(success
            ? CommandResult.Ok($"Successfully sent media key {detail}.")
            : CommandResult.Fail($"Failed sending media key {detail}."));
    }
}
