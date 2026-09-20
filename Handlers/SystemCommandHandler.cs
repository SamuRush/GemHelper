using System.Text.Json;
using Gem.Core;
using Gem.Services;

namespace Gem.Handlers;

/// <summary>
/// Command handler for system-level commands (e.g. exit/shutdown JARVIS).
/// </summary>
public sealed class SystemCommandHandler : ICommandHandler
{
    public string CommandName => "system";

    private readonly IVoiceFeedbackService? _voiceFeedback;

    public SystemCommandHandler(IVoiceFeedbackService? voiceFeedback = null)
    {
        _voiceFeedback = voiceFeedback;
    }

    public Task<CommandResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken = default)
    {
        string action = string.Empty;
        string name = string.Empty;
        string? reply = null;

        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("action", out var actionProp))
            {
                action = actionProp.GetString() ?? string.Empty;
            }

            if (args.TryGetProperty("name", out var nameProp))
            {
                name = nameProp.GetString() ?? string.Empty;
            }

            if (args.TryGetProperty("reply", out var replyProp) && replyProp.ValueKind == JsonValueKind.String)
            {
                reply = replyProp.GetString();
            }
        }

        if ((action.Equals("close", StringComparison.OrdinalIgnoreCase) ||
             action.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
             action.Equals("kill", StringComparison.OrdinalIgnoreCase) ||
             action.Equals("exit", StringComparison.OrdinalIgnoreCase)) &&
            (AppHandler.IsExitTarget(name) || string.IsNullOrWhiteSpace(name)))
        {
            AppHandler.ShutdownJarvis(reply, _voiceFeedback);
            return Task.FromResult(CommandResult.Ok("Завершение работы JARVIS."));
        }

        return Task.FromResult(CommandResult.Fail($"Unsupported system action '{action}' on target '{name}'."));
    }
}
