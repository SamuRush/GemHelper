using System.Diagnostics;
using System.Windows.Automation;
using Gem.Core;
using Gem.Handlers;
using Gem.Win32;

namespace Gem.Services.Media;

/// <summary>
/// Automation for launching YouTube in browser and clicking the first recommended video via UI Automation.
/// </summary>
public static class YouTubeLauncher
{
    private static readonly HashSet<string> YouTubeNavigationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "YouTube", "Главная", "Home", "Shorts", "Подписки", "Subscriptions",
        "Вы", "You", "История", "History", "Мои видео", "Your videos",
        "Смотреть позже", "Watch later", "Понравившиеся", "Liked videos",
        "В тренде", "Trending", "Музыка", "Music", "Игры", "Gaming",
        "Новости", "News", "Спорт", "Sports", "Подкасты", "Podcasts",
        "Войти", "Sign in", "Настройки", "Settings", "Пожаловаться",
        "Справка", "Help", "Отправить отзыв", "Send feedback",
        "Все", "All", "Видеоигры", "В эфире", "Live", "Недавно опубликованные",
        "Просмотрено", "Новое для вас", "Поиск", "Search", "Поиск с помощью голоса"
    };

    public static bool TryClickFirstRecommendedVideoViaUiAutomation(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            var windowElement = AutomationElement.FromHandle(hWnd);
            if (windowElement == null) return false;

            var linkCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
            var links = windowElement.FindAll(TreeScope.Descendants, linkCondition);

            foreach (AutomationElement link in links)
            {
                try
                {
                    string name = link.Current.Name?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (YouTubeNavigationNames.Contains(name) || name.Length < 3) continue;

                    var rect = link.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width < 50 || rect.Height < 20) continue;

                    if (rect.Top < 100 || rect.Left < 100) continue;

                    if (link.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj))
                    {
                        ((InvokePattern)invokeObj).Invoke();
                        return true;
                    }

                    int centerX = (int)(rect.Left + rect.Width / 2);
                    int centerY = (int)(rect.Top + rect.Height / 2);
                    _ = NativeMethods.ClickAtAsync(centerX, centerY);
                    return true;
                }
                catch (Exception ex)
                {
                    JarvisLog.Debug("YouTubeLauncher", $"Error processing video link element: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YouTubeLauncher UI Automation] Error: {ex.Message}");
        }

        return false;
    }

    public static async Task<bool> LaunchYouTubeAndPlayFirstRecommendedAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("[YouTubeLauncher] No active video stream. Launching YouTube and opening first recommended video.");

        var startInfo = AppControlCommandHandler.GetDefaultBrowserStartInfo("https://www.youtube.com");
        try
        {
            using var proc = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YouTubeLauncher] Error launching browser with YouTube: {ex.Message}");
            return false;
        }

        IntPtr ytHwnd = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && !cancellationToken.IsCancellationRequested)
        {
            ytHwnd = VideoWindowFinder.FindYouTubeWindow();
            if (ytHwnd != IntPtr.Zero)
            {
                break;
            }
            await Task.Delay(250, cancellationToken);
        }

        if (ytHwnd == IntPtr.Zero)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (VideoWindowFinder.IsBrowserWindow(fg))
            {
                ytHwnd = fg;
            }
        }

        if (ytHwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(ytHwnd);
        }

        await Task.Delay(2000, cancellationToken);

        if (ytHwnd != IntPtr.Zero)
        {
            bool clickedViaUia = TryClickFirstRecommendedVideoViaUiAutomation(ytHwnd);
            if (clickedViaUia)
            {
                Debug.WriteLine("[YouTubeLauncher] Successfully clicked first recommended video via UI Automation.");
                return true;
            }
        }

        if (ytHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(ytHwnd, out RECT rect))
        {
            int clickX = rect.Left + Math.Max(320, Math.Min(450, rect.Width / 3));
            int clickY = rect.Top + Math.Max(220, Math.Min(300, rect.Height / 3));
            await NativeMethods.ClickAtAsync(clickX, clickY);
            Debug.WriteLine($"[YouTubeLauncher] Clicked first recommended video at ({clickX}, {clickY}).");
            return true;
        }

        return true;
    }
}
