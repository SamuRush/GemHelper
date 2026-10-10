using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gem.Core;

namespace Gem.Handlers;

public sealed partial class AppControlCommandHandler
{
    private static async Task<CommandResult> CloseApplicationAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return CommandResult.Fail("Action 'close' requires 'name' property (e.g. 'notepad', 'discord', 'steam').");
        }

        string rawName = nameProp.GetString()!;
        string normalizedName = rawName.Trim().ToLowerInvariant();
        if (normalizedName.EndsWith(".exe"))
        {
            normalizedName = normalizedName[..^4];
        }

        // Self-shutdown: завершаем текущий процесс ассистента через AppHandler с блокирующим голосовым прощанием
        if (Gem.Services.AppHandler.IsExitTarget(normalizedName))
        {
            string? replyText = null;
            if (args.TryGetProperty("reply", out var replyProp) && replyProp.ValueKind == JsonValueKind.String)
            {
                replyText = replyProp.GetString();
            }

            var appHandler = new Gem.Services.AppHandler();
            appHandler.Handle("close", normalizedName, replyText);
            return CommandResult.Ok("Завершение работы.");
        }

        // 1. ИЗОЛЯЦИЯ: Сначала проверяем системные приложения
        var matchedApp = FindAppDefinition(normalizedName);
        string[] processCandidateNames;
        if (matchedApp != null)
        {
            processCandidateNames = matchedApp.ProcessNames;
        }
        // 2. Проверяем игры Steam через AppHandler
        else if (Gem.Services.AppHandler.SteamProcessNames.ContainsKey(normalizedName) ||
                 Gem.Services.AppHandler.SteamGames.ContainsKey(normalizedName) ||
                 normalizedName.Contains("monsterhunter", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("mhw", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("wilds", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.Contains("world", StringComparison.OrdinalIgnoreCase))
        {
            var appHandler = new Gem.Services.AppHandler();
            bool ok = appHandler.Handle("close", normalizedName);
            return ok
                ? CommandResult.Ok($"Игра '{rawName}' успешно закрыта.", new { Target = rawName, App = normalizedName })
                : CommandResult.Fail($"Процессы для '{rawName}' не найдены для закрытия.");
        }
        else
        {
            processCandidateNames = new[] { normalizedName };
        }

        bool force = false;
        if (args.TryGetProperty("force", out var forceProp) &&
            (forceProp.ValueKind == JsonValueKind.True || forceProp.ValueKind == JsonValueKind.False))
        {
            force = forceProp.GetBoolean();
        }

        var processes = new List<Process>();
        foreach (var procName in processCandidateNames)
        {
            processes.AddRange(Process.GetProcessesByName(procName));
        }

        if (processes.Count == 0)
        {
            string display = matchedApp?.CanonicalId ?? normalizedName;
            return CommandResult.Fail($"No running processes found matching '{display}'.");
        }

        var closedPids = new List<int>();
        var killedPids = new List<int>();

        foreach (var proc in processes)
        {
            try
            {
                int pid = proc.Id;
                if (!force && proc.MainWindowHandle != IntPtr.Zero)
                {
                    // Attempt graceful window close
                    proc.CloseMainWindow();
                    bool exited = false;

                    for (int i = 0; i < 15; i++)
                    {
                        if (proc.HasExited)
                        {
                            exited = true;
                            break;
                        }
                        await Task.Delay(100, cancellationToken);
                    }

                    if (exited)
                    {
                        closedPids.Add(pid);
                        continue;
                    }
                }

                // Force kill if graceful close wasn't requested or didn't complete
                proc.Kill(entireProcessTree: true);
                killedPids.Add(pid);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[AppHandler Warning] Ошибка закрытия процесса PID {proc.Id}: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                proc.Dispose();
            }
        }

        string appLabel = matchedApp?.CanonicalId ?? normalizedName;
        return CommandResult.Ok(
            $"Processed {processes.Count} process(es) for '{appLabel}'. Gracefully closed: {closedPids.Count}, Force-killed: {killedPids.Count}.",
            new
            {
                Application = appLabel,
                TotalFound = processes.Count,
                GracefullyClosed = closedPids,
                ForceKilled = killedPids
            }
        );
    }

    private static CommandResult CheckApplicationStatus(JsonElement args)
    {
        if (!args.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            return CommandResult.Fail("Action 'status' requires 'name' property.");
        }

        string rawName = nameProp.GetString()!;
        string normalizedName = rawName.Trim().ToLowerInvariant();
        if (normalizedName.EndsWith(".exe"))
        {
            normalizedName = normalizedName[..^4];
        }

        var matchedApp = FindAppDefinition(normalizedName);
        string[] processCandidateNames = matchedApp != null
            ? matchedApp.ProcessNames
            : new[] { normalizedName };

        var processes = new List<Process>();
        foreach (var procName in processCandidateNames)
        {
            processes.AddRange(Process.GetProcessesByName(procName));
        }

        var pids = processes.Select(p => p.Id).ToList();
        foreach (var p in processes)
        {
            p.Dispose();
        }

        string appLabel = matchedApp?.CanonicalId ?? normalizedName;
        return CommandResult.Ok(
            $"Application '{appLabel}' running status: {(processes.Count > 0 ? "Running" : "Not running")}.",
            new
            {
                Application = appLabel,
                IsRunning = processes.Count > 0,
                InstanceCount = processes.Count,
                ProcessIds = pids
            }
        );
    }
}
