using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gem.Services.Audio;
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
    public static HashSet<uint> GetTargetFamilyPids(string target) =>
        ProcessFamilyTreeResolver.GetTargetFamilyPids(target, IsProcessNameMatchingTarget);

    /// <summary>
    /// Enumerates all active WASAPI audio sessions matching target.
    /// </summary>
    public static List<(AudioSessionControl Session, string ProcessName, uint Pid)> FindSessions(string target) =>
        AudioSessionFinder.FindSessions(target, GetTargetFamilyPids, IsProcessNameMatchingTarget);

    /// <summary>
    /// Searches for the Yandex Music window, checking the foreground window first,
    /// then enumerating visible top-level windows.
    /// </summary>
    public static IntPtr FindYandexMusicWindow(out bool isForeground) =>
        ElectronWindowController.FindYandexMusicWindow(out isForeground, IsMusicProcessName);

    /// <summary>
    /// Sends internal Electron player hotkeys (Ctrl+Up / Ctrl+Down) to the active or found Yandex Music window.
    /// </summary>
    public static bool TrySendElectronMusicHotkeys(bool increase, int repeatCount = 1) =>
        ElectronWindowController.TrySendElectronMusicHotkeys(increase, repeatCount, IsMusicProcessName);

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

        try
        {
            // Apply volume to ALL child and worker sessions of the target application
            foreach (var (session, _, _) in sessions)
            {
                session.SimpleAudioVolume.Volume = clamped;
                isMuted = session.SimpleAudioVolume.Mute;
            }
        }
        finally
        {
            foreach (var (session, _, _) in sessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { JarvisLog.Debug("AppAudioService", $"Dispose session: {ex.Message}"); }
            }
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

        try
        {
            // Apply volume to ALL child and worker sessions of the target application
            foreach (var (session, _, _) in sessions)
            {
                session.SimpleAudioVolume.Volume = newScalar;
                isMuted = session.SimpleAudioVolume.Mute;
            }
        }
        finally
        {
            foreach (var (session, _, _) in sessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { JarvisLog.Debug("AppAudioService", $"Dispose session: {ex.Message}"); }
            }
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

        try
        {
            foreach (var (session, _, _) in sessions)
            {
                session.SimpleAudioVolume.Mute = newMuteState;
            }
        }
        finally
        {
            foreach (var (session, _, _) in sessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { JarvisLog.Debug("AppAudioService", $"Dispose session: {ex.Message}"); }
            }
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
        float currentScalar;
        bool isMuted;

        try
        {
            currentScalar = sessions[0].Session.SimpleAudioVolume.Volume;
            isMuted = sessions[0].Session.SimpleAudioVolume.Mute;
        }
        finally
        {
            foreach (var (session, _, _) in sessions)
            {
                try { session.Dispose(); }
                catch (Exception ex) { JarvisLog.Debug("AppAudioService", $"Dispose session: {ex.Message}"); }
            }
        }

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
