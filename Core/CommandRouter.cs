using System.Text.Json;

namespace Gem.Core;

/// <summary>
/// Dispatches JSON requests to the corresponding ICommandHandler.
/// </summary>
public sealed class CommandRouter
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a command handler.
    /// </summary>
    public CommandRouter Register(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.CommandName] = handler;
        return this;
    }

    /// <summary>
    /// Gets all registered command names.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredCommands => _handlers.Keys;

    /// <summary>
    /// Executes a command from a raw JSON string.
    /// Expected format: { "command": "...", "args": { ... } }
    /// </summary>
    public async Task<CommandResult> ExecuteJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CommandResult.Fail("Command JSON payload cannot be empty.");
        }

        json = json.Trim().Trim('\uFEFF');
        if (string.IsNullOrWhiteSpace(json))
        {
            return CommandResult.Fail("Command JSON payload cannot be empty.");
        }

        CommandRequest? request;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return CommandResult.Fail("JSON payload must be a JSON object.");
            }

            if (!root.TryGetProperty("command", out var commandProp) || commandProp.ValueKind != JsonValueKind.String)
            {
                return CommandResult.Fail("Invalid command format. 'command' string property is required.");
            }

            var commandName = commandProp.GetString();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return CommandResult.Fail("'command' property cannot be empty.");
            }

            // Args can be an object or missing/null
            JsonElement argsElement;
            if (root.TryGetProperty("args", out var argsProp))
            {
                argsElement = argsProp.Clone();
            }
            else
            {
                using var emptyDoc = JsonDocument.Parse("{}");
                argsElement = emptyDoc.RootElement.Clone();
            }

            request = new CommandRequest(commandName, argsElement);
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"JSON parse error: {ex.Message}");
        }

        return await RouteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Routes and executes a strongly-typed CommandRequest directly.
    /// </summary>
    public async Task<CommandResult> RouteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return CommandResult.Fail("'command' property cannot be empty.");
        }

        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            return CommandResult.Fail(
                $"Unknown command '{request.Command}'. Registered commands: [{string.Join(", ", _handlers.Keys)}]"
            );
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[Router: Dispatch] Маршрутизация команды '{request.Command}' -> {handler.GetType().Name}...");
        Console.ResetColor();

        try
        {
            var result = await handler.ExecuteAsync(request.Args, cancellationToken);
            Console.ForegroundColor = result.Success ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"[Router: Result] Исполнение '{request.Command}': {(result.Success ? "УСПЕШНО" : "ОШИБКА")} (\"{result.Message}\")");
            Console.ResetColor();
            return result;
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[Router: Result] Команда '{request.Command}' отменена.");
            Console.ResetColor();
            return CommandResult.Fail($"Command '{request.Command}' was canceled.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Router: Error] Исключение при выполнении команды '{request.Command}': {ex.Message}");
            Console.ResetColor();
            return CommandResult.Fail(
                $"Execution error in command '{request.Command}': {ex.Message}",
                new { ExceptionType = ex.GetType().FullName, ex.StackTrace }
            );
        }
    }

    /// <summary>
    /// Alias for ExecuteJsonAsync.
    /// </summary>
    public Task<CommandResult> RouteAsync(string json, CancellationToken cancellationToken = default)
        => ExecuteJsonAsync(json, cancellationToken);
}
