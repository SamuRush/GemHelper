using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gem.Core;

/// <summary>
/// DTO representing an incoming command payload: { "command": "...", "args": { ... } }
/// </summary>
public sealed record CommandRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] JsonElement Args
);
