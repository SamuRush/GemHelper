using System.Diagnostics;
using System.Drawing;
using System.Text;
using Gem.Core;
using Gem.Win32;

namespace Gem.Services.Steam;

/// <summary>
/// Handles installation, uninstallation, launch, and dialog confirmation for Steam games.
/// </summary>
public static class SteamInstaller
{
    public static void LaunchInstallation(SteamGameInfo game)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Запуск установки: '{game.Title}' (AppID: {game.AppId})...");
        Console.ResetColor();

        try
        {
            Process.Start(new ProcessStartInfo($"steam://install/{game.AppId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller Error] Ошибка запуска протокола steam://install/: {ex.Message}");
            Console.ResetColor();
            return;
        }

        _ = Task.Run(async () =>
        {
            await PollAndConfirmInstallDialogAsync();
        });
    }

    public static async Task<bool> PollAndConfirmInstallDialogAsync()
    {
        JarvisLog.Debug("SteamInstaller", "Поиск главного видимого окна процесса 'steam'...");

        const int maxAttempts = 10;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            IntPtr hWnd = SteamUiAutomation.FindMainSteamWindow();
            if (hWnd != IntPtr.Zero)
            {
                SteamUiAutomation.ForceForegroundWindow(hWnd);
                await Task.Delay(1200);

                Point? installBtnPos = await SteamUiAutomation.TryClickBlueButtonAsync(hWnd, "Клик по кнопке 'Установить'");
                if (!installBtnPos.HasValue)
                {
                    JarvisLog.Warn("SteamInstaller", "Синяя кнопка 'Установить' не найдена.");
                    return false;
                }

                await Task.Delay(1200);

                var sb = new StringBuilder(512);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                string currentTitle = sb.ToString();

                Point? secondBtnPos;
                if (SteamUiAutomation.IsEulaDialogTitle(currentTitle))
                {
                    secondBtnPos = await SteamUiAutomation.TryConfirmEulaViaUiAutomationAsync(hWnd);
                }
                else
                {
                    secondBtnPos = await SteamUiAutomation.TryClickBlueButtonAsync(hWnd, "Подтверждение параметров установки");
                }

                if (secondBtnPos.HasValue)
                {
                    JarvisLog.Info("SteamInstaller", $"Параметры установки / соглашение подтверждены (X={secondBtnPos.Value.X}, Y={secondBtnPos.Value.Y}). Установка успешно запущена.");
                }
                else
                {
                    JarvisLog.Debug("SteamInstaller", "Дополнительное подтверждение не требуется или завершено. Установка успешно запущена.");
                }

                return true;
            }

            await Task.Delay(500);
        }

        JarvisLog.Warn("SteamInstaller", "Главное видимое окно процесса 'steam' не найдено.");
        return false;
    }

    public static async Task<bool> UninstallGameAsync(uint appId, string title, Action<uint>? onUninstalled = null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Инициирован запрос на удаление: {title} (AppId: {appId})");
        Console.ResetColor();

        try
        {
            Process.Start(new ProcessStartInfo($"steam://uninstall/{appId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller Error] Ошибка запуска протокола steam://uninstall/: {ex.Message}");
            Console.ResetColor();
            return false;
        }

        IntPtr dialogHwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            dialogHwnd = FindSteamUninstallDialog(title);
            if (dialogHwnd != IntPtr.Zero)
            {
                break;
            }
            await Task.Delay(200);
        }

        if (dialogHwnd == IntPtr.Zero)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller Warning] Диалоговое окно подтверждения удаления не обнаружено в течение таймаута.");
            Console.ResetColor();
            return false;
        }

        NativeMethods.GetWindowRect(dialogHwnd, out RECT dialogRect);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Найдено диалоговое окно удаления: HWND=0x{dialogHwnd.ToInt64():X}, Rect=[L:{dialogRect.Left}, T:{dialogRect.Top}, R:{dialogRect.Right}, B:{dialogRect.Bottom}]");
        Console.ResetColor();

        SteamUiAutomation.ForceForegroundWindow(dialogHwnd);
        await Task.Delay(400);

        Point? buttonCenter = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            buttonCenter = SteamUiAutomation.FindSteamUninstallButton(dialogHwnd);
            if (buttonCenter.HasValue)
            {
                break;
            }
            await Task.Delay(250);
        }

        if (!buttonCenter.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller Warning] Синяя кнопка подтверждения в диалоговом окне не найдена.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Кнопка 'Удалить' найдена в центре диалога: X={buttonCenter.Value.X}, Y={buttonCenter.Value.Y}. Выполняю клик мышью...");
        Console.ResetColor();

        await SteamUiAutomation.ClickMouseAtAsync(buttonCenter.Value);

        bool isClosed = false;
        var closeSw = Stopwatch.StartNew();
        while (closeSw.ElapsedMilliseconds < 3000)
        {
            if (!NativeMethods.IsWindowVisible(dialogHwnd))
            {
                isClosed = true;
                break;
            }
            await Task.Delay(150);
        }

        if (!isClosed)
        {
            Point? retryCenter = SteamUiAutomation.FindSteamUninstallButton(dialogHwnd) ?? buttonCenter;
            if (retryCenter.HasValue)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Повторный клик мышью по кнопке удаления (X={retryCenter.Value.X}, Y={retryCenter.Value.Y})...");
                Console.ResetColor();
                await SteamUiAutomation.ClickMouseAtAsync(retryCenter.Value);
                await Task.Delay(500);
                if (!NativeMethods.IsWindowVisible(dialogHwnd))
                {
                    isClosed = true;
                }
            }
        }

        if (isClosed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller] Диалоговое окно Steam подтверждено кликом мыши и закрыто.");
            Console.ResetColor();
            onUninstalled?.Invoke(appId);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamInstaller Warning] Диалоговое окно Steam не закрылось после клика.");
            Console.ResetColor();
        }

        return isClosed;
    }

    public static IntPtr FindSteamUninstallDialog(string title)
    {
        IntPtr foundHwnd = IntPtr.Zero;

        var steamProcesses = Process.GetProcessesByName("steam")
            .Concat(Process.GetProcessesByName("steamwebhelper"))
            .ToArray();
        var steamPids = steamProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (steamPids.Count == 0)
        {
            foreach (var p in steamProcesses) p.Dispose();
            return IntPtr.Zero;
        }

        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (!steamPids.Contains(pid)) return true;

                string wTitle = GetWindowTitle(hWnd);
                bool matchesUninstall = SteamUiAutomation.IsMatchingDialogTitle(wTitle, title);

                if (matchesUninstall && NativeMethods.GetWindowRect(hWnd, out RECT rect))
                {
                    int w = rect.Width;
                    int h = rect.Height;
                    if (w >= 250 && w <= 900 && h >= 120 && h <= 650)
                    {
                        foundHwnd = hWnd;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            foreach (var p in steamProcesses) p.Dispose();
        }

        return foundHwnd;
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }
}
