using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Gem.Handlers.App;

/// <summary>
/// Resolves the default web browser on Windows via registry associations or shell fallback.
/// </summary>
public static class DefaultBrowserResolver
{
    public static ProcessStartInfo GetDefaultBrowserStartInfo(string args = "")
    {
        string urlOrTarget = string.IsNullOrWhiteSpace(args) ? "https://" : args.Trim();

        // 1. Попытка получить зарегистрированный браузер по умолчанию из реестра Windows
        try
        {
            using var userChoiceKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string? progId = userChoiceKey?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
            {
                using var httpChoiceKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                progId = httpChoiceKey?.GetValue("ProgId") as string;
            }

            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                string? rawCmd = cmdKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(rawCmd))
                {
                    string exePath = rawCmd;
                    if (rawCmd.StartsWith('"'))
                    {
                        int nextQuote = rawCmd.IndexOf('"', 1);
                        if (nextQuote > 1)
                        {
                            exePath = rawCmd.Substring(1, nextQuote - 1);
                        }
                    }
                    else
                    {
                        int space = rawCmd.IndexOf(' ');
                        if (space > 0)
                        {
                            exePath = rawCmd[..space];
                        }
                    }

                    if (File.Exists(exePath))
                    {
                        return new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = string.IsNullOrWhiteSpace(args) ? string.Empty : args,
                            UseShellExecute = true
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultBrowserResolver] Ошибка чтения браузера по умолчанию из реестра: {ex.Message}");
        }

        // 2. Универсальный запуск браузера по умолчанию через shell cmd start без отображения консольного окна
        return new ProcessStartInfo("cmd", $"/c start {urlOrTarget}")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
    }
}
