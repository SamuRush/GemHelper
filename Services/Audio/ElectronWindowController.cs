using System;
using System.Diagnostics;
using System.Text;
using Gem.Win32;

namespace Gem.Services.Audio;

/// <summary>
/// Finds Electron player windows (such as Yandex Music) and controls their internal UI sliders
/// using synthetic keystroke combinations (Ctrl+Up / Ctrl+Down).
/// </summary>
public static class ElectronWindowController
{
    /// <summary>
    /// Searches for the Yandex Music window, checking the foreground window first,
    /// then enumerating visible top-level windows.
    /// </summary>
    public static IntPtr FindYandexMusicWindow(out bool isForeground, Func<string, bool> isMusicProcess)
    {
        isForeground = false;
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(fg, sb, sb.Capacity);
            string fgTitle = sb.ToString();

            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            string fgProcName = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                fgProcName = p.ProcessName;
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("ElectronWindowController", $"Error getting process for foreground window PID {pid}: {ex.Message}");
            }

            if (isMusicProcess(fgProcName) ||
                fgTitle.Contains("Яндекс Музыка", StringComparison.OrdinalIgnoreCase) ||
                fgTitle.Contains("Yandex Music", StringComparison.OrdinalIgnoreCase) ||
                fgTitle.Contains("Y.Music", StringComparison.OrdinalIgnoreCase))
            {
                isForeground = true;
                return fg;
            }
        }

        IntPtr found = IntPtr.Zero;
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = "";
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    procName = p.ProcessName;
                }
                catch (Exception ex)
                {
                    JarvisLog.Debug("ElectronWindowController", $"Error getting process for window PID {pid}: {ex.Message}");
                }

                if (isMusicProcess(procName) ||
                    title.Contains("Яндекс Музыка", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Yandex Music", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Y.Music", StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ElectronWindowController] EnumWindows error: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Sends internal Electron player hotkeys (Ctrl+Up / Ctrl+Down) to the active or found Yandex Music window
    /// to synchronously shift the internal UI volume slider.
    /// </summary>
    public static bool TrySendElectronMusicHotkeys(bool increase, int repeatCount, Func<string, bool> isMusicProcess)
    {
        IntPtr musicWnd = FindYandexMusicWindow(out bool isForeground, isMusicProcess);
        if (musicWnd == IntPtr.Zero)
        {
            return false;
        }

        ushort vk = increase ? NativeConstants.VK_UP : NativeConstants.VK_DOWN;

        IntPtr previousForeground = IntPtr.Zero;
        if (!isForeground)
        {
            previousForeground = NativeMethods.GetForegroundWindow();
            NativeMethods.SetForegroundWindow(musicWnd);
        }

        try
        {
            int count = Math.Clamp(repeatCount, 1, 10);
            for (int i = 0; i < count; i++)
            {
                MediaKeyService.SendCtrlKeyCombination(vk);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ElectronWindowController] Error sending Electron hotkeys: {ex.Message}");
            return false;
        }
        finally
        {
            if (previousForeground != IntPtr.Zero && previousForeground != musicWnd)
            {
                NativeMethods.SetForegroundWindow(previousForeground);
            }
        }
    }
}
