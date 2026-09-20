using System.Text.Json.Serialization;

namespace Gem.Core;

/// <summary>
/// Result returned after executing a command.
/// </summary>
public sealed record CommandResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("data")] object? Data = null
)
{
    public static CommandResult Ok(string message = "Command executed successfully.", object? data = null)
        => new(true, message, data);

    public static CommandResult Fail(string message, object? data = null)
        => new(false, message, data);
}
