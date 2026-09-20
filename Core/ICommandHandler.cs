using System.Text.Json;

namespace Gem.Core;

/// <summary>
/// Defines a contract for handlers executing JSON-routed commands.
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Unique identifier for this command (e.g. "volume", "app", "hotkey").
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Executes the command with the provided JSON arguments.
    /// </summary>
    /// <param name="args">The "args" JSON object from the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command execution result.</returns>
    Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default);
}
