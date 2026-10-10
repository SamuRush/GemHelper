using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Gem.Handlers.App;

public record AppDefinition(
    string CanonicalId,
    string[] Aliases,
    string[] ProcessNames,
    Func<string, ProcessStartInfo> CreateStartInfo
);

/// <summary>
/// Registry of well-known Windows applications and smart alias mappings.
/// </summary>
public static class KnownAppRegistry
{
    private static readonly List<AppDefinition> KnownApps = new()
    {
        // 1. Steam ("steam", "tim", "стим")
        new AppDefinition(
            CanonicalId: "steam",
            Aliases: new[] { "steam", "tim", "тим", "стим", "стима" },
            ProcessNames: new[] { "steam", "Steam" },
            CreateStartInfo: (args) =>
            {
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string steamExe = Path.Combine(programFilesX86, "Steam", "steam.exe");

                if (File.Exists(steamExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = steamExe,
                        Arguments = args,
                        UseShellExecute = true
                    };
                }

                // Fallback: Steam URI protocol registered in Windows Shell
                return new ProcessStartInfo
                {
                    FileName = "steam://open/main",
                    UseShellExecute = true
                };
            }
        ),

        // 2. Discord ("discord", "дискорд", "диск")
        new AppDefinition(
            CanonicalId: "discord",
            Aliases: new[] { "discord", "дискорд", "диск", "дискорда" },
            ProcessNames: new[] { "Discord" },
            CreateStartInfo: (args) =>
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string updateExe = Path.Combine(localAppData, "Discord", "Update.exe");

                if (File.Exists(updateExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = updateExe,
                        Arguments = "--processStart Discord.exe" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args),
                        UseShellExecute = true
                    };
                }

                // Check app-* directory inside Discord folder
                string discordDir = Path.Combine(localAppData, "Discord");
                if (Directory.Exists(discordDir))
                {
                    var appDirs = Directory.GetDirectories(discordDir, "app-*");
                    if (appDirs.Length > 0)
                    {
                        string latestExe = Path.Combine(appDirs.OrderByDescending(d => d).First(), "Discord.exe");
                        if (File.Exists(latestExe))
                        {
                            return new ProcessStartInfo
                            {
                                FileName = latestExe,
                                Arguments = args,
                                UseShellExecute = true
                            };
                        }
                    }
                }

                // Fallback: discord protocol or PATH
                return new ProcessStartInfo
                {
                    FileName = "discord",
                    Arguments = args,
                    UseShellExecute = true
                };
            }
        ),

        // 3. Telegram ("telegram", "телега", "телеграм")
        new AppDefinition(
            CanonicalId: "telegram",
            Aliases: new[] { "telegram", "телега", "телеграм", "телегу", "телеграмм", "tg" },
            ProcessNames: new[] { "Telegram" },
            CreateStartInfo: (args) =>
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string telegramExe = Path.Combine(appData, "Telegram Desktop", "Telegram.exe");

                if (File.Exists(telegramExe))
                {
                    return new ProcessStartInfo
                    {
                        FileName = telegramExe,
                        Arguments = args,
                        UseShellExecute = true
                    };
                }

                // Fallback: tg protocol or PATH
                return new ProcessStartInfo
                {
                    FileName = "telegram",
                    Arguments = args,
                    UseShellExecute = true
                };
            }
        ),

        // 4. Browser ("browser", "браузер", "brave", "хром", "chrome", etc.)
        new AppDefinition(
            CanonicalId: "browser",
            Aliases: new[]
            {
                "browser", "браузер", "браузера", "веб", "web",
                "brave", "брейв", "хром", "хрома", "chrome",
                "гугл", "гугл хром", "google chrome",
                "интернет", "сеть", "edge", "эдж", "firefox", "файрфокс", "опера", "opera", "яндекс", "yandex"
            },
            ProcessNames: new[] { "brave", "chrome", "msedge", "firefox", "opera", "browser" },
            CreateStartInfo: (args) => DefaultBrowserResolver.GetDefaultBrowserStartInfo(args)
        ),

        // 5. Notepad ("notepad", "блокнот")
        new AppDefinition(
            CanonicalId: "notepad",
            Aliases: new[] { "notepad", "блокнот", "блокнота" },
            ProcessNames: new[] { "notepad", "Notepad" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "notepad",
                Arguments = args,
                UseShellExecute = true
            }
        ),

        // 6. Calculator ("calc", "калькулятор")
        new AppDefinition(
            CanonicalId: "calc",
            Aliases: new[] { "calc", "калькулятор", "калькулятора" },
            ProcessNames: new[] { "CalculatorApp", "Calculator", "calc" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "calc",
                Arguments = args,
                UseShellExecute = true
            }
        ),

        // 7. Explorer ("explorer", "проводник")
        new AppDefinition(
            CanonicalId: "explorer",
            Aliases: new[] { "explorer", "проводник", "проводника", "файлы", "папка", "папку" },
            ProcessNames: new[] { "explorer" },
            CreateStartInfo: (args) => new ProcessStartInfo
            {
                FileName = "explorer",
                Arguments = args,
                UseShellExecute = true
            }
        )
    };

    public static bool IsKnownApp(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return false;
        string norm = nameOrAlias.Trim().ToLowerInvariant();
        if (norm.EndsWith(".exe")) norm = norm[..^4];
        return FindAppDefinition(norm) != null;
    }

    public static AppDefinition? FindAppDefinition(string nameOrAlias)
    {
        return KnownApps.FirstOrDefault(app =>
            app.CanonicalId.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase) ||
            app.Aliases.Any(alias => alias.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase))
        );
    }
}
