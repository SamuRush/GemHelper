using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace Gem.Services;

/// <summary>
/// Production service for managing per-application audio volume (AudioSession)
/// without modifying or conflicting with Windows master system volume.
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
        if (norm.Contains("music", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.Contains("yandex", StringComparison.OrdinalIgnoreCase) && norm.Contains("music", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static bool IsVideoProcessName(string processName)
    {
        return MediaSessionService.IsVideoProcessName(processName);
    }

    private static List<(AudioSessionControl Session, string ProcessName)> FindSessions(string target)
    {
        var matched = new List<(AudioSessionControl, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null) return matched;

            var sessionManager = defaultDevice.AudioSessionManager;
            if (sessionManager == null) return matched;

            var sessions = sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;
                if (pid == 0) continue;

                string procName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (IsSessionMatchingTarget(procName, target))
                {
                    matched.Add((session, procName));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppAudioService Warning] Error enumerating audio sessions: {ex.Message}");
        }

        return matched;
    }

    private static bool IsSessionMatchingTarget(string procName, string target)
    {
        string normTarget = target.ToLowerInvariant().Trim();

        if (normTarget == "player" || normTarget == "music" || normTarget == "yandexmusic")
        {
            if (IsMusicProcessName(procName)) return true;
        }

        if (normTarget == "video")
        {
            if (IsVideoProcessName(procName)) return true;
        }

        if (procName.Contains(normTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public Task<AppVolumeResult> SetAppVolumeAsync(string target, float scalar)
    {
        float clamped = Math.Clamp(scalar, 0.0f, 1.0f);
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
        bool isMuted = false;
        foreach (var (session, _) in sessions)
        {
            session.SimpleAudioVolume.Volume = clamped;
            isMuted = session.SimpleAudioVolume.Mute;
        }

        int percentage = (int)Math.Round(clamped * 100.0f);
        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Громкость приложения '{matchedName}' установлена на {percentage}%.",
            Level: percentage,
            Scalar: clamped,
            IsMuted: isMuted,
            AppName: matchedName
        ));
    }

    public Task<AppVolumeResult> ChangeAppVolumeAsync(string target, float deltaScalar)
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
        float newScalar = Math.Clamp(currentScalar + deltaScalar, 0.0f, 1.0f);
        bool isMuted = false;

        foreach (var (session, _) in sessions)
        {
            session.SimpleAudioVolume.Volume = newScalar;
            isMuted = session.SimpleAudioVolume.Mute;
        }

        int percentage = (int)Math.Round(newScalar * 100.0f);
        return Task.FromResult(new AppVolumeResult(
            Success: true,
            Message: $"Громкость приложения '{matchedName}' изменена на {percentage}%.",
            Level: percentage,
            Scalar: newScalar,
            IsMuted: isMuted,
            AppName: matchedName
        ));
    }

    public Task<AppVolumeResult> SetAppMuteAsync(string target, bool? isMuted)
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
        bool newMuteState = isMuted ?? !sessions[0].Session.SimpleAudioVolume.Mute;
        float currentScalar = sessions[0].Session.SimpleAudioVolume.Volume;

        foreach (var (session, _) in sessions)
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
