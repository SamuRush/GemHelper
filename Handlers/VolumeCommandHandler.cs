using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gem.Core;
using Gem.Services;
using NAudio.CoreAudioApi;

namespace Gem.Handlers;

/// <summary>
/// Command handler for controlling Windows master audio volume via NAudio CoreAudioApi
/// and per-application player volume via IAppAudioService without master volume conflicts.
/// Supported actions: "set", "change", "mute", "get".
/// </summary>
public sealed class VolumeCommandHandler : ICommandHandler
{
    public string CommandName => "volume";

    private readonly IAppAudioService? _appAudioService;

    public VolumeCommandHandler(IAppAudioService? appAudioService = null)
    {
        _appAudioService = appAudioService;
    }

    private IAppAudioService AppAudio => _appAudioService ?? AppAudioService.Instance;

    public async Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "get";
        string? target = null;

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("action", out var actionProp))
            {
                action = actionProp.GetString()?.ToLowerInvariant() ?? "get";
            }

            if (args.TryGetProperty("target", out var targetProp))
            {
                target = targetProp.GetString()?.ToLowerInvariant();
            }
        }

        // 1. Per-Application / Player Volume Control (No conflict with system master volume)
        if (!string.IsNullOrWhiteSpace(target) && target != "master" && target != "system")
        {
            return await ExecuteAppVolumeAsync(action, target, args);
        }

        // 2. Windows Master Volume Control
        return ExecuteMasterVolume(action, args);
    }

    private async Task<CommandResult> ExecuteAppVolumeAsync(string action, string target, JsonElement args)
    {
        switch (action)
        {
            case "set":
            {
                float level = 0;
                bool hasLevel = false;
                if (args.TryGetProperty("level", out var levelProp))
                {
                    if (levelProp.ValueKind == JsonValueKind.Number && levelProp.TryGetSingle(out level))
                    {
                        hasLevel = true;
                    }
                    else if (levelProp.ValueKind == JsonValueKind.String && float.TryParse(levelProp.GetString(), out level))
                    {
                        hasLevel = true;
                    }
                }

                if (!hasLevel)
                {
                    return CommandResult.Fail("Action 'set' requires numeric 'level' (0-100 or 0.0-1.0).");
                }

                float scalar = level > 1.0f ? Math.Clamp(level / 100.0f, 0.0f, 1.0f) : Math.Clamp(level, 0.0f, 1.0f);
                var res = await AppAudio.SetAppVolumeAsync(target, scalar);

                return res.Success
                    ? CommandResult.Ok(res.Message, new { target, app = res.AppName, level = res.Level, scalar = res.Scalar, isMuted = res.IsMuted })
                    : CommandResult.Fail(res.Message);
            }

            case "change":
            {
                float delta = 0;
                bool hasDelta = false;
                if (args.TryGetProperty("delta", out var deltaProp))
                {
                    if (deltaProp.ValueKind == JsonValueKind.Number && deltaProp.TryGetSingle(out delta))
                    {
                        hasDelta = true;
                    }
                    else if (deltaProp.ValueKind == JsonValueKind.String && float.TryParse(deltaProp.GetString(), out delta))
                    {
                        hasDelta = true;
                    }
                }

                if (!hasDelta)
                {
                    return CommandResult.Fail("Action 'change' requires numeric 'delta' (e.g. +10, -5).");
                }

                float deltaScalar = Math.Abs(delta) > 1.0f ? delta / 100.0f : delta;
                var res = await AppAudio.ChangeAppVolumeAsync(target, deltaScalar);

                return res.Success
                    ? CommandResult.Ok(res.Message, new { target, app = res.AppName, level = res.Level, scalar = res.Scalar, isMuted = res.IsMuted })
                    : CommandResult.Fail(res.Message);
            }

            case "mute":
            {
                bool? newMuteState = null;
                if (args.TryGetProperty("isMuted", out var muteProp) &&
                    (muteProp.ValueKind == JsonValueKind.True || muteProp.ValueKind == JsonValueKind.False))
                {
                    newMuteState = muteProp.GetBoolean();
                }

                var res = await AppAudio.SetAppMuteAsync(target, newMuteState);
                return res.Success
                    ? CommandResult.Ok(res.Message, new { target, app = res.AppName, isMuted = res.IsMuted, level = res.Level })
                    : CommandResult.Fail(res.Message);
            }

            case "get":
            default:
            {
                var res = await AppAudio.GetAppVolumeAsync(target);
                return res.Success
                    ? CommandResult.Ok(res.Message, new { target, app = res.AppName, level = res.Level, scalar = res.Scalar, isMuted = res.IsMuted })
                    : CommandResult.Fail(res.Message);
            }
        }
    }

    private static CommandResult ExecuteMasterVolume(string action, JsonElement args)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null)
            {
                return CommandResult.Fail("No default audio output device found.");
            }

            var endpointVolume = defaultDevice.AudioEndpointVolume;

            switch (action)
            {
                case "set":
                {
                    float level = 0;
                    bool hasLevel = false;
                    if (args.TryGetProperty("level", out var levelProp))
                    {
                        if (levelProp.ValueKind == JsonValueKind.Number && levelProp.TryGetSingle(out level))
                        {
                            hasLevel = true;
                        }
                        else if (levelProp.ValueKind == JsonValueKind.String && float.TryParse(levelProp.GetString(), out level))
                        {
                            hasLevel = true;
                        }
                    }

                    if (!hasLevel)
                    {
                        return CommandResult.Fail("Action 'set' requires numeric 'level' (0-100 or 0.0-1.0).");
                    }

                    float scalar = level > 1.0f ? Math.Clamp(level / 100.0f, 0.0f, 1.0f) : Math.Clamp(level, 0.0f, 1.0f);
                    endpointVolume.MasterVolumeLevelScalar = scalar;

                    int percentage = (int)Math.Round(scalar * 100.0f);
                    return CommandResult.Ok(
                        $"Volume set to {percentage}%.",
                        new { level = percentage, scalar, isMuted = endpointVolume.Mute, device = defaultDevice.FriendlyName }
                    );
                }

                case "change":
                {
                    float delta = 0;
                    bool hasDelta = false;
                    if (args.TryGetProperty("delta", out var deltaProp))
                    {
                        if (deltaProp.ValueKind == JsonValueKind.Number && deltaProp.TryGetSingle(out delta))
                        {
                            hasDelta = true;
                        }
                        else if (deltaProp.ValueKind == JsonValueKind.String && float.TryParse(deltaProp.GetString(), out delta))
                        {
                            hasDelta = true;
                        }
                    }

                    if (!hasDelta)
                    {
                        return CommandResult.Fail("Action 'change' requires numeric 'delta' (e.g. +10, -5).");
                    }

                    float currentScalar = endpointVolume.MasterVolumeLevelScalar;
                    float deltaScalar = Math.Abs(delta) > 1.0f ? delta / 100.0f : delta;
                    float newScalar = Math.Clamp(currentScalar + deltaScalar, 0.0f, 1.0f);

                    endpointVolume.MasterVolumeLevelScalar = newScalar;
                    int percentage = (int)Math.Round(newScalar * 100.0f);

                    return CommandResult.Ok(
                        $"Volume changed by {delta} to {percentage}%.",
                        new { level = percentage, scalar = newScalar, isMuted = endpointVolume.Mute, device = defaultDevice.FriendlyName }
                    );
                }

                case "mute":
                {
                    bool newMuteState;
                    if (args.TryGetProperty("isMuted", out var muteProp) &&
                        (muteProp.ValueKind == JsonValueKind.True || muteProp.ValueKind == JsonValueKind.False))
                    {
                        newMuteState = muteProp.GetBoolean();
                    }
                    else
                    {
                        newMuteState = !endpointVolume.Mute;
                    }

                    endpointVolume.Mute = newMuteState;
                    return CommandResult.Ok(
                        $"Volume muted state set to: {newMuteState}.",
                        new { isMuted = newMuteState, level = (int)Math.Round(endpointVolume.MasterVolumeLevelScalar * 100.0f), device = defaultDevice.FriendlyName }
                    );
                }

                case "get":
                default:
                {
                    int percentage = (int)Math.Round(endpointVolume.MasterVolumeLevelScalar * 100.0f);
                    return CommandResult.Ok(
                        $"Current volume: {percentage}%, Muted: {endpointVolume.Mute}.",
                        new
                        {
                            level = percentage,
                            scalar = endpointVolume.MasterVolumeLevelScalar,
                            isMuted = endpointVolume.Mute,
                            device = defaultDevice.FriendlyName
                        }
                    );
                }
            }
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Failed to manage audio volume: {ex.Message}");
        }
    }
}
