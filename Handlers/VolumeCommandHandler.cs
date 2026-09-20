using System.Text.Json;
using Gem.Core;
using NAudio.CoreAudioApi;

namespace Gem.Handlers;

/// <summary>
/// Command handler for controlling Windows master audio volume via NAudio CoreAudioApi.
/// Supported actions: "set", "change", "mute", "get".
/// </summary>
public sealed class VolumeCommandHandler : ICommandHandler
{
    public string CommandName => "volume";

    public Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = "get";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var actionProp))
        {
            action = actionProp.GetString()?.ToLowerInvariant() ?? "get";
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null)
            {
                return Task.FromResult(CommandResult.Fail("No default audio output device found."));
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
                        return Task.FromResult(CommandResult.Fail("Action 'set' requires numeric 'level' (0-100 or 0.0-1.0)."));
                    }

                    // Normalize: if level > 1.0, treat as percentage (0-100)
                    float scalar = level > 1.0f ? Math.Clamp(level / 100.0f, 0.0f, 1.0f) : Math.Clamp(level, 0.0f, 1.0f);
                    endpointVolume.MasterVolumeLevelScalar = scalar;

                    int percentage = (int)Math.Round(scalar * 100.0f);
                    return Task.FromResult(CommandResult.Ok(
                        $"Volume set to {percentage}%.",
                        new { level = percentage, scalar, isMuted = endpointVolume.Mute, device = defaultDevice.FriendlyName }
                    ));
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
                        return Task.FromResult(CommandResult.Fail("Action 'change' requires numeric 'delta' (e.g. +10, -5)."));
                    }

                    float currentScalar = endpointVolume.MasterVolumeLevelScalar;
                    float deltaScalar = Math.Abs(delta) > 1.0f ? delta / 100.0f : delta;
                    float newScalar = Math.Clamp(currentScalar + deltaScalar, 0.0f, 1.0f);

                    endpointVolume.MasterVolumeLevelScalar = newScalar;
                    int percentage = (int)Math.Round(newScalar * 100.0f);

                    return Task.FromResult(CommandResult.Ok(
                        $"Volume changed by {delta} to {percentage}%.",
                        new { level = percentage, scalar = newScalar, isMuted = endpointVolume.Mute, device = defaultDevice.FriendlyName }
                    ));
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
                        // Toggle if not explicitly specified
                        newMuteState = !endpointVolume.Mute;
                    }

                    endpointVolume.Mute = newMuteState;
                    return Task.FromResult(CommandResult.Ok(
                        $"Volume muted state set to: {newMuteState}.",
                        new { isMuted = newMuteState, level = (int)Math.Round(endpointVolume.MasterVolumeLevelScalar * 100.0f), device = defaultDevice.FriendlyName }
                    ));
                }

                case "get":
                default:
                {
                    int percentage = (int)Math.Round(endpointVolume.MasterVolumeLevelScalar * 100.0f);
                    return Task.FromResult(CommandResult.Ok(
                        $"Current volume: {percentage}%, Muted: {endpointVolume.Mute}.",
                        new
                        {
                            level = percentage,
                            scalar = endpointVolume.MasterVolumeLevelScalar,
                            isMuted = endpointVolume.Mute,
                            device = defaultDevice.FriendlyName
                        }
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail($"Failed to manage audio volume: {ex.Message}"));
        }
    }
}
