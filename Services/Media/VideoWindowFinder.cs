using System.Diagnostics;
using System.Text;
using Gem.Core;
using Gem.Win32;

namespace Gem.Services.Media;

/// <summary>
/// Detects and enumerates video playback windows, browser tabs, and dedicated media player processes.
/// </summary>
public static class VideoWindowFinder
{
    public static readonly string[] MusicProcessNames =
    [
        "YandexMusic",
        "yandexmusic",
        "Yandex.Music",
        "yandex-music"
    ];

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "browser", // Yandex Browser executable is browser.exe
        "opera",
        "opera_gx",
        "brave",
        "vivaldi"
    };

    private static readonly HashSet<string> MediaPlayerProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc",
        "mpc-hc",
        "mpc-hc64",
        "mpc-be",
        "mpc-be64",
        "wmplayer",
        "potplayer",
        "potplayermini",
        "potplayermini64",
        "kmplayer"
    };

    public static bool IsBrowserProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }
        return BrowserProcessNames.Contains(norm);
    }

    public static bool IsMediaPlayerProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }
        return MediaPlayerProcessNames.Contains(norm);
    }

    public static bool IsVideoProcessName(string processName)
    {
        return IsBrowserProcessName(processName) || IsMediaPlayerProcessName(processName);
    }

    public static bool IsVideoTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        return title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("ютуб", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("VK Video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("ВК Видео", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("vk.com/video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Кинопоиск", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("kinopoisk", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("твич", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("RuTube", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("рутуб", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("нетфликс", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Дзен", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Dzen", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Okko", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("окко", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Ivi", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("иви", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Premier", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("премьер", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Vimeo", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(" - Видео", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(" - Video", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mkv", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".avi", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(".mov", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        string processName = "";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWindowFinder] Error getting process for window {hWnd}: {ex.Message}");
            return false;
        }

        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title)) return false;

        if (IsMediaPlayerProcessName(processName))
        {
            return true;
        }

        if (IsBrowserProcessName(processName))
        {
            return IsVideoTitle(title);
        }

        return IsVideoTitle(title);
    }

    public static bool IsBrowserWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hWnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return IsBrowserProcessName(proc.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    public static IntPtr FindActiveVideoWindow()
    {
        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        if (fgWnd != IntPtr.Zero && IsVideoWindow(fgWnd))
        {
            return fgWnd;
        }

        IntPtr foundWnd = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (IsVideoWindow(hWnd))
                {
                    foundWnd = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWindowFinder] Error enumerating video windows: {ex.Message}");
        }

        return foundWnd;
    }

    public static IntPtr FindWindowForProcessName(string procNameOrAppId)
    {
        if (string.IsNullOrWhiteSpace(procNameOrAppId)) return IntPtr.Zero;
        string norm = procNameOrAppId.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }

        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            if (proc.ProcessName.Equals(norm, StringComparison.OrdinalIgnoreCase))
                            {
                                found = hWnd;
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            JarvisLog.Debug("VideoWindowFinder", $"Process lookup error for PID {pid}: {ex.Message}");
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWindowFinder] FindWindowForProcessName error: {ex.Message}");
        }
        return found;
    }

    public static IntPtr FindYouTubeWindow()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0)
                    {
                        string procName = "";
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            procName = proc.ProcessName;
                        }
                        catch (Exception ex)
                        {
                            JarvisLog.Debug("VideoWindowFinder", $"Process lookup error for PID {pid}: {ex.Message}");
                        }

                        if (IsBrowserProcessName(procName))
                        {
                            var sb = new StringBuilder(512);
                            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                            string title = sb.ToString();
                            if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                                title.Contains("ютуб", StringComparison.OrdinalIgnoreCase))
                            {
                                found = hWnd;
                                return false;
                            }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWindowFinder] Error finding YouTube window: {ex.Message}");
        }
        return found;
    }

    public static Process? FindRunningMusicProcess()
    {
        foreach (var name in MusicProcessNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VideoWindowFinder] Error getting processes for '{name}': {ex.Message}");
                continue;
            }

            if (procs.Length > 0)
            {
                var chosen = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs[0];
                foreach (var p in procs)
                {
                    if (p != chosen) p.Dispose();
                }
                return chosen;
            }
        }
        return null;
    }

    public static IntPtr FindWindowForProcess(int processId)
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == processId)
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoWindowFinder] EnumWindows error: {ex.Message}");
        }

        return found;
    }
}
