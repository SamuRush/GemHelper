using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Gem.Core;
using Gem.Win32;

namespace Gem.Services.Steam;

/// <summary>
/// Win32 and UI Automation helpers for inspecting and clicking Steam modal dialogs, buttons, and EULA agreements.
/// </summary>
public static class SteamUiAutomation
{
    private static readonly string[] AllowedEulaButtonTexts =
    [
        "Принять", "Согласен", "Accept", "I Agree", "Я принимаю", "Я согласен"
    ];

    private static readonly string[] DefaultTargetButtonNames =
    [
        "Удалить", "Uninstall", "Установить", "Install"
    ];

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public static IntPtr FindMainSteamWindow()
    {
        IntPtr mainHwnd = IntPtr.Zero;

        var steamProcesses = Process.GetProcessesByName("steam").ToArray();
        var steamPids = steamProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (steamPids.Count > 0)
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (steamPids.Contains(pid) && NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowRect(hWnd, out RECT rect);
                    if (rect.Width > 600 && rect.Height > 400)
                    {
                        mainHwnd = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }

        foreach (var p in steamProcesses) p.Dispose();

        if (mainHwnd != IntPtr.Zero) return mainHwnd;

        var helperProcesses = Process.GetProcessesByName("steamwebhelper").ToArray();
        var helperPids = helperProcesses.Select(p => (uint)p.Id).ToHashSet();

        if (helperPids.Count > 0)
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (helperPids.Contains(pid) && NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowRect(hWnd, out RECT rect);
                    if (rect.Width > 600 && rect.Height > 400)
                    {
                        mainHwnd = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }

        foreach (var p in helperProcesses) p.Dispose();

        return mainHwnd;
    }

    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        try
        {
            uint foreThread = GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
            uint appThread = GetCurrentThreadId();

            if (foreThread != 0 && foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, true);
            }

            ShowWindow(hWnd, 9); // SW_RESTORE
            NativeMethods.SetForegroundWindow(hWnd);

            if (foreThread != 0 && foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamUiAutomation Warning] ForceForegroundWindow: {ex.Message}");
            NativeMethods.SetForegroundWindow(hWnd);
        }
    }

    public static async Task ClickMouseAtAsync(Point pt)
    {
        SetCursorPos(pt.X, pt.Y);
        await Task.Delay(100);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        await Task.Delay(80);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    public static Point? FindSteamBlueButton(IntPtr hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        for (int y = height - 15; y > height / 3; y -= 4)
        {
            for (int x = 30; x < width - 30; x += 4)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.B > 170 && c.B > c.R + 60 && c.G > 90)
                {
                    return new Point(rect.Left + x, rect.Top + y);
                }
            }
        }
        return null;
    }

    public static bool IsEulaDialogTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;
        string t = windowTitle.Trim();
        return t.Contains("лицензион", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("соглашен", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("eula", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("license", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("terms", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("agreement", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEulaButtonName(string? buttonText)
    {
        if (string.IsNullOrWhiteSpace(buttonText)) return false;
        string trimmed = buttonText.Trim();
        foreach (var allowed in AllowedEulaButtonTexts)
        {
            if (trimmed.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                trimmed.IndexOf(allowed, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    public static Point? FindEulaButtonViaUiAutomation(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;

        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element == null) return null;

            string windowTitle = element.Current.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                var sb = new StringBuilder(512);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                windowTitle = sb.ToString();
            }

            if (!IsEulaDialogTitle(windowTitle))
            {
                JarvisLog.Warn("SteamUiAutomation", $"Окно ('{windowTitle}') не подтверждено как EULA. Клик строго заблокирован.");
                return null;
            }

            var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var buttons = element.FindAll(TreeScope.Descendants, buttonCondition);

            foreach (AutomationElement btn in buttons)
            {
                string btnName = btn.Current.Name;
                if (IsEulaButtonName(btnName))
                {
                    var rect = btn.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                    {
                        int centerX = (int)(rect.Left + rect.Width / 2);
                        int centerY = (int)(rect.Top + rect.Height / 2);

                        JarvisLog.Info("SteamUiAutomation", $"Кнопка согласия EULA '{btnName}' подтверждена через UI Automation: ({centerX}, {centerY}) в окне '{windowTitle}'.");
                        return new Point(centerX, centerY);
                    }
                }
            }

            JarvisLog.Warn("SteamUiAutomation", $"В окне '{windowTitle}' не найдена валидная кнопка EULA через UI Automation.");
        }
        catch (Exception ex)
        {
            JarvisLog.Warn("SteamUiAutomation", $"Ошибка UI Automation при проверке EULA: {ex.Message}.");
        }

        return null;
    }

    public static async Task<Point?> TryConfirmEulaViaUiAutomationAsync(IntPtr hWnd)
    {
        Point? pt = FindEulaButtonViaUiAutomation(hWnd);
        if (!pt.HasValue)
        {
            JarvisLog.Warn("SteamUiAutomation", "Подтверждение EULA отклонено системой безопасности.");
            return null;
        }

        await ClickMouseAtAsync(pt.Value);
        JarvisLog.Info("SteamUiAutomation", $"EULA успешно подтверждено (X={pt.Value.X}, Y={pt.Value.Y}).");
        return pt;
    }

    public static bool IsMatchingButtonName(string? name, string[]? targetNames = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var targets = targetNames ?? DefaultTargetButtonNames;
        string trimmed = name.Trim();
        foreach (var target in targets)
        {
            if (trimmed.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsMatchingDialogTitle(string? windowTitle, string? gameTitle = null)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;
        string trimmed = windowTitle.Trim();

        if (trimmed.Contains("удаление", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("удалить", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("видален", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("установка", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("установить", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(gameTitle) && trimmed.Contains(gameTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static Point? FindSteamButtonViaUiAutomation(IntPtr hWnd, string[]? targetButtonNames = null)
    {
        if (hWnd == IntPtr.Zero) return null;

        try
        {
            var element = AutomationElement.FromHandle(hWnd);
            if (element == null) return null;

            var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var buttons = element.FindAll(TreeScope.Descendants, buttonCondition);

            foreach (AutomationElement btn in buttons)
            {
                string name = btn.Current.Name;
                if (IsMatchingButtonName(name, targetButtonNames))
                {
                    var rect = btn.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                    {
                        int centerX = (int)(rect.Left + rect.Width / 2);
                        int centerY = (int)(rect.Top + rect.Height / 2);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamUiAutomation] Кнопка '{name}' найдена через AutomationElement: Center=({centerX}, {centerY}), Bounds={rect}");
                        Console.ResetColor();

                        return new Point(centerX, centerY);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SteamUiAutomation] Ошибка UI Automation: {ex.Message}.");
            Console.ResetColor();
        }

        return null;
    }

    public static Point? FindSteamBlueButtonInWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        if (!NativeMethods.GetWindowRect(hWnd, out RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        static bool IsSteamAccentBlue(Color c)
        {
            return (c.B >= 150 && c.B > c.R + 50 && c.G >= 75 && c.R < 120) ||
                   (c.B > 170 && c.B > c.R + 60 && c.G > 90);
        }

        var bluePixels = new List<Point>();
        int startY = Math.Max((int)(height * 0.55), 0);
        int endY = Math.Max(height - 10, startY);
        int startX = 15;
        int endX = Math.Min(Math.Max((int)(width * 0.50), 100), width - 15);

        for (int y = endY; y >= startY; y -= 2)
        {
            for (int x = startX; x <= endX; x += 2)
            {
                Color c = bmp.GetPixel(x, y);
                if (IsSteamAccentBlue(c))
                {
                    bluePixels.Add(new Point(x, y));
                }
            }
        }

        if (bluePixels.Count == 0)
        {
            for (int y = endY; y >= startY; y -= 2)
            {
                for (int x = startX; x < width - 15; x += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (IsSteamAccentBlue(c))
                    {
                        bluePixels.Add(new Point(x, y));
                    }
                }
            }
        }

        if (bluePixels.Count == 0)
        {
            return null;
        }

        int minX = bluePixels.Min(p => p.X);
        int maxX = bluePixels.Max(p => p.X);
        int minY = bluePixels.Min(p => p.Y);
        int maxY = bluePixels.Max(p => p.Y);

        int centerX = rect.Left + (minX + maxX) / 2;
        int centerY = rect.Top + (minY + maxY) / 2;

        return new Point(centerX, centerY);
    }

    public static Point? FindSteamUninstallButton(IntPtr hWnd)
    {
        Point? uiaPoint = FindSteamButtonViaUiAutomation(hWnd, ["Удалить", "Uninstall"]);
        if (uiaPoint.HasValue)
        {
            return uiaPoint;
        }

        return FindSteamBlueButtonInWindow(hWnd);
    }

    public static async Task<Point?> TryClickBlueButtonAsync(IntPtr hWnd, string? actionLabel = null)
    {
        Point? buttonPos = FindSteamButtonViaUiAutomation(hWnd, ["Установить", "Install", "Далее", "Next"]) ?? FindSteamBlueButton(hWnd);
        if (buttonPos.HasValue)
        {
            await ClickMouseAtAsync(buttonPos.Value);
            string label = !string.IsNullOrWhiteSpace(actionLabel)
                ? actionLabel
                : "Клик по кнопке выполнен";
            JarvisLog.Info("SteamUiAutomation", $"{label} (X={buttonPos.Value.X}, Y={buttonPos.Value.Y}).");
            return buttonPos;
        }
        return null;
    }
}
