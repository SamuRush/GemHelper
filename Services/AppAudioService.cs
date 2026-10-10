using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Gem.Win32;
using NAudio.CoreAudioApi;

namespace Gem.Services;

/// <summary>
/// Production service for managing per-application audio volume (AudioSession)
/// without modifying or conflicting with Windows master system volume.
/// Specifically addresses multi-process Electron applications (e.g. Yandex Music)
/// by enumerating all active sessions via AudioSessionManager2, matching child PIDs,
/// and supporting Electron UI slider hotkey synchronization (Ctrl+Up / Ctrl+Down).
/// </summary>
public class AppAudioService : IAppAudioService
{
    private static readonly Lazy<AppAudioService> _instance = new(() => new AppAudioService());
    public static AppAudioService Instance => _instance.Value;

    private static readonly HashSet<string> MusicProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Y.Music",
        "YandexMusic",
        "yandexmusic",
        "Yandex.Music",
        "yandex-music",
        "spotify",
        "aimp",
        "foobar2000",
        "music"
    };

    #region Win32 Toolhelp32 Declarations for Process Family Resolution

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    #endregion

    public static bool IsMusicProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string norm = processName.Trim();
        if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm[..^4];
        }

        if (MusicProcessNames.Contains(norm)) return true;
        if (norm.Contains("yandexmusic", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.Contains("yandex", StringComparison.OrdinalIgnoreCase) && norm.Contains("music", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.Contains("music", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static bool IsVideoProcessName(string processName)
    {
        return MediaSessionService.IsVideoProcessName(processName);
    }

    public static bool IsProcessNameMatchingTarget(string procName, string target)
    {
        if (string.IsNullOrWhiteSpace(procName)) return false;
        string normProc = procName.Trim();
        if (normProc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normProc = normProc[..^4];
        }

        string normTarget = target.ToLowerInvariant().Trim();

        if (normTarget == "player" || normTarget == "music" || normTarget == "yandexmusic")
        {
            return IsMusicProcessName(normProc);
        }

        if (normTarget == "video")
        {
            return IsVideoProcessName(normProc);
        }

        return normProc.Contains(normTarget, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects all main and descendant child PIDs belonging to the target process family
    /// to reliably match multi-process Electron audio worker/renderer subprocesses.
    /// </summary>
    public static HashSet<uint> GetTargetFamilyPids(string target)
    {
        var familyPids = new HashSet<uint>();
        try
        {
            var parentToChildren = new Dictionary<uint, List<uint>>();
            var rootPids = new List<uint>();

            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot != IntPtr.Zero && snapshot != (IntPtr)(-1))
            {
                try
                {
                    var entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();

                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            uint pid = entry.th32ProcessID;
                            uint parentPid = entry.th32ParentProcessID;
                            string exe = entry.szExeFile;

                            if (!parentToChildren.TryGetValue(parentPid, out var children))
                            {
                                children = new List<uint>();
                                parentToChildren[parentPid] = children;
                            }
                            children.Add(pid);

                            if (IsProcessNameMatchingTarget(exe, target))
                            {
                                rootPids.Add(pid);
                            }
                        } while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
            }
            else
            {
                // Fallback: standard Process enumeration
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (IsProcessNameMatchingTarget(proc.ProcessName, target))
                        {
                            rootPids.Add((uint)proc.Id);
                        }
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }

            // Breadth-First Search to gather all child and grandchild PIDs
            var queue = new Queue<uint>(rootPids);
            while (queue.Count > 0)
            {
                uint currentPid = queue.Dequeue();
                if (familyPids.Add(currentPid))
                {
                    if (parentToChildren.TryGetValue(currentPid, out var childList))
                    {
                        foreach (var childPid in childList)
                        {
                            queue.Enqueue(childPid);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppAudioService] Error collecting family PIDs: {ex.Message}");
        }

        return familyPids;
    }

    private static bool IsSessionIdentifierMatchingTarget(string sessionId, string target)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        string normTarget = target.ToLowerInvariant().Trim();

        if (normTarget == "player" || normTarget == "music" || normTarget == "yandexmusic")
        {
            return sessionId.Contains("YandexMusic", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("Y.Music", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("yandex-music", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("spotify", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("aimp", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("music", StringComparison.OrdinalIgnoreCase);
        }

        if (normTarget == "video")
        {
            return sessionId.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("msedge", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("browser", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("opera", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("brave", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("vlc", StringComparison.OrdinalIgnoreCase)
                || sessionId.Contains("mpc", StringComparison.OrdinalIgnoreCase);
        }

        return sessionId.Contains(normTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisplayNameMatchingTarget(string displayName, string target)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        string normTarget = target.ToLowerInvariant().Trim();

        if (normTarget == "player" || normTarget == "music" || normTarget == "yandexmusic")
        {
            return displayName.Contains("Яндекс Музыка", StringComparison.OrdinalIgnoreCase)
                || displayName.Contains("Yandex Music", StringComparison.OrdinalIgnoreCase)
                || displayName.Contains("Music", StringComparison.OrdinalIgnoreCase);
        }

        return displayName.Contains(normTarget, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates all active WASAPI audio sessions via AudioSessionManager2.GetSessionEnumerator(),
    /// checking process name, child PIDs, session identifiers, and display names.
    /// Returns all matching sessions (critical for multi-process Electron apps where multiple sessions exist).
    /// </summary>
    public static List<(AudioSessionControl Session, string ProcessName, uint Pid)> FindSessions(string target)
    {
        var matched = new List<(AudioSessionControl, string, uint)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null) return matched;

            var sessionManager = defaultDevice.AudioSessionManager;
            if (sessionManager == null) return matched;

            // Refresh sessions to invoke AudioSessionManager2.GetSessionEnumerator()
            sessionManager.RefreshSessions();

            var sessions = sessionManager.Sessions;
            if (sessions == null || sessions.Count == 0) return matched;

            // Collect all parent and child PIDs belonging to the target process family
            var targetFamilyPids = GetTargetFamilyPids(target);

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;

                string procName = "";
                if (pid != 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                    }
                    catch
                    {
                        // Subprocess may be protected or transient
                    }
                }

                string sessionId = session.GetSessionIdentifier ?? "";
                string displayName = session.DisplayName ?? "";

                bool isMatch = false;

                // 1. Session PID matches any process or child PID in the family
                if (pid != 0 && targetFamilyPids.Contains(pid))
                {
                    isMatch = true;
                }

                // 2. Process name matches target (e.g. "YandexMusic")
                if (!isMatch && !string.IsNullOrEmpty(procName) && IsProcessNameMatchingTarget(procName, target))
                {
                    isMatch = true;
                }

                // 3. Session Identifier contains the target executable or pattern
                if (!isMatch && IsSessionIdentifierMatchingTarget(sessionId, target))
                {
                    isMatch = true;
                }

                // 4. Session Display Name matches
                if (!isMatch && IsDisplayNameMatchingTarget(displayName, target))
                {
                    isMatch = true;
                }

                if (isMatch)
                {
                    string label = !string.IsNullOrEmpty(procName) ? procName : (!string.IsNullOrEmpty(displayName) ? displayName : target);
                    matched.Add((session, label, pid));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppAudioService Warning] Error enumerating audio sessions: {ex.Message}");
        }

        return matched;
    }

    #region Electron Window & Hotkey Fallback (Ctrl+Up / Ctrl+Down)

    /// <summary>
    /// Searches for the Yandex Music window, checking the foreground window first,
    /// then enumerating visible top-level windows.
    /// </summary>
    public static IntPtr FindYandexMusicWindow(out bool isForeground)
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
            catch { }

            if (IsMusicProcessName(fgProcName) ||
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
                catch { }

                if (IsMusicProcessName(procName) ||
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
            Debug.WriteLine($"[AppAudioService] EnumWindows error: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Sends internal Electron player hotkeys (Ctrl+Up / Ctrl+Down) to the active or found Yandex Music window
    /// to synchronously shift the internal UI volume slider.
    /// </summary>
    public static bool TrySendElectronMusicHotkeys(bool increase, int repeatCount = 1)
    {
        IntPtr musicWnd = FindYandexMusicWindow(out bool isForeground);
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
            Debug.WriteLine($"[AppAudioService] Error sending Electron hotkeys: {ex.Message}");
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

    #endregion

    public Task<AppVolumeResult> SetAppVolumeAsync(string target, float scalar)
    {
        float clamped = Math.Clamp(scalar, 0.0f, 1.0f);
        var sessions = FindSessions(target);

        bool isMusicTarget = target.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                             target.Equals("music", StringComparison.OrdinalIgnoreCase) ||
                             target.Contains("yandex", StringComparison.OrdinalIgnoreCase);

        if (sessions.Count == 0)
        {
            // Electron Fallback: if no audio sessions registered yet, use internal app hotkeys
            if (isMusicTarget && TrySendElectronMusicHotkeys(clamped > 0.5f, 2))
            {
                int fallbackPercentage = (int)Math.Round(clamped * 100.0f);
                return Task.FromResult(new AppVolumeResult(
                    Success: true,
                    Message: $"Громкость плеера Яндекс Музыка изменена через хоткеи приложения (Ctrl+{(clamped > 0.5f ? "Up" : "Down")}).",
                    Level: fallbackPercentage,
                    Scalar: clamped,
                    IsMuted: false,
                    AppName: "YandexMusic"
                ));
            }

            return Task.FromResult(new AppVolumeResult(
                Success: false,
                Message: $"Активная аудиосессия для '{target}' не найдена.",
                Level: 0,
                Scalar: 0,
                IsMuted: false,
                AppName: target
            ));
        }

        string matchedName = sessions[0].ProcessName;
        bool isMuted = false;

        // Apply volume to ALL child and worker sessions of the target application
        foreach (var (session, _, _) in sessions)
        {
            session.SimpleAudioVolume.Volume = clamped;
            isMuted = session.SimpleAudioVolume.Mute;
        }

        // Synchronously shift Electron UI slider if window is found
        if (isMusicTarget)
        {
            TrySendElectronMusicHotkeys(clamped > 0.5f, 1);
        }

        int percentage = (int)Math.Round(clamped * 100.0f);
        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Громкость приложения '{matchedName}' ({sessions.Count} сессий) установлена на {percentage}%.",
            Level: percentage,
            Scalar: clamped,
            IsMuted: isMuted,
            AppName: matchedName
        ));
    }

    public Task<AppVolumeResult> ChangeAppVolumeAsync(string target, float deltaScalar)
    {
        var sessions = FindSessions(target);

        bool isMusicTarget = target.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                             target.Equals("music", StringComparison.OrdinalIgnoreCase) ||
                             target.Contains("yandex", StringComparison.OrdinalIgnoreCase);

        int hotkeySteps = Math.Max(1, (int)Math.Round(Math.Abs(deltaScalar) * 10.0f));

        if (sessions.Count == 0)
        {
            // Electron Fallback: if no audio sessions registered yet, use internal app hotkeys
            if (isMusicTarget && TrySendElectronMusicHotkeys(deltaScalar > 0, hotkeySteps))
            {
                return Task.FromResult(new AppVolumeResult(
                    Success: true,
                    Message: $"Громкость плеера Яндекс Музыка изменена через хоткеи приложения (Ctrl+{(deltaScalar > 0 ? "Up" : "Down")}).",
                    Level: deltaScalar > 0 ? 60 : 40,
                    Scalar: deltaScalar > 0 ? 0.6f : 0.4f,
                    IsMuted: false,
                    AppName: "YandexMusic"
                ));
            }

            return Task.FromResult(new AppVolumeResult(
                Success: false,
                Message: $"Активная аудиосессия для '{target}' не найдена.",
                Level: 0,
                Scalar: 0,
                IsMuted: false,
                AppName: target
            ));
        }

        string matchedName = sessions[0].ProcessName;
        float currentScalar = sessions[0].Session.SimpleAudioVolume.Volume;
        float newScalar = Math.Clamp(currentScalar + deltaScalar, 0.0f, 1.0f);
        bool isMuted = false;

        // Apply volume to ALL child and worker sessions of the target application
        foreach (var (session, _, _) in sessions)
        {
            session.SimpleAudioVolume.Volume = newScalar;
            isMuted = session.SimpleAudioVolume.Mute;
        }

        // Synchronously shift Electron UI slider if window is found
        if (isMusicTarget)
        {
            TrySendElectronMusicHotkeys(deltaScalar > 0, hotkeySteps);
        }

        int percentage = (int)Math.Round(newScalar * 100.0f);
        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Громкость приложения '{matchedName}' ({sessions.Count} сессий) изменена на {percentage}%.",
            Level: percentage,
            Scalar: newScalar,
            IsMuted: isMuted,
            AppName: matchedName
        ));
    }

    public Task<AppVolumeResult> SetAppMuteAsync(string target, bool? isMuted)
    {
        var sessions = FindSessions(target);

        bool isMusicTarget = target.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                             target.Equals("music", StringComparison.OrdinalIgnoreCase) ||
                             target.Contains("yandex", StringComparison.OrdinalIgnoreCase);

        if (sessions.Count == 0)
        {
            if (isMusicTarget)
            {
                bool shouldMute = isMuted ?? true;
                if (shouldMute)
                {
                    TrySendElectronMusicHotkeys(false, 5);
                }
                else
                {
                    TrySendElectronMusicHotkeys(true, 3);
                }

                return Task.FromResult(new AppVolumeResult(
                    Success: true,
                    Message: $"Режим звука для 'YandexMusic': {(shouldMute ? "Отключен" : "Включен")} через хоткеи приложения.",
                    Level: shouldMute ? 0 : 50,
                    Scalar: shouldMute ? 0f : 0.5f,
                    IsMuted: shouldMute,
                    AppName: "YandexMusic"
                ));
            }

            return Task.FromResult(new AppVolumeResult(
                Success: false,
                Message: $"Активная аудиосессия для '{target}' не найдена.",
                Level: 0,
                Scalar: 0,
                IsMuted: false,
                AppName: target
            ));
        }

        string matchedName = sessions[0].ProcessName;
        bool newMuteState = isMuted ?? !sessions[0].Session.SimpleAudioVolume.Mute;
        float currentScalar = sessions[0].Session.SimpleAudioVolume.Volume;

        foreach (var (session, _, _) in sessions)
        {
            session.SimpleAudioVolume.Mute = newMuteState;
        }

        int percentage = (int)Math.Round(currentScalar * 100.0f);
        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Режим звука для '{matchedName}': {(newMuteState ? "Отключен" : "Включен")}.",
            Level: percentage,
            Scalar: currentScalar,
            IsMuted: newMuteState,
            AppName: matchedName
        ));
    }

    public Task<AppVolumeResult> GetAppVolumeAsync(string target)
    {
        var sessions = FindSessions(target);

        if (sessions.Count == 0)
        {
            return Task.FromResult(new AppVolumeResult(
                Success: false,
                Message: $"Активная аудиосессия для '{target}' не найдена.",
                Level: 0,
                Scalar: 0,
                IsMuted: false,
                AppName: target
            ));
        }

        string matchedName = sessions[0].ProcessName;
        float currentScalar = sessions[0].Session.SimpleAudioVolume.Volume;
        bool isMuted = sessions[0].Session.SimpleAudioVolume.Mute;
        int percentage = (int)Math.Round(currentScalar * 100.0f);

        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Громкость '{matchedName}': {percentage}%, Muted: {isMuted}.",
            Level: percentage,
            Scalar: currentScalar,
            IsMuted: isMuted,
            AppName: matchedName
        ));
    }
}
