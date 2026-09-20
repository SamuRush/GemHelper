using System.Text.Json.Serialization;
using Gem.Core;

namespace Gem.Services;

/// <summary>
/// DTO representing the response from the LLM intent interpreter.
/// </summary>
public sealed record JarvisResponse(
    [property: JsonPropertyName("commandRequest")] CommandRequest? CommandRequest,
    [property: JsonPropertyName("reply")] string? Reply
);
