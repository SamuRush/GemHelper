using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Gem.Services.Audio;

/// <summary>
/// Enumerates and matches WASAPI audio sessions across audio endpoints.
/// </summary>
public static class AudioSessionFinder
{
    public static bool IsSessionIdentifierMatchingTarget(string sessionId, string target)
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

    public static bool IsDisplayNameMatchingTarget(string displayName, string target)
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
    public static List<(AudioSessionControl Session, string ProcessName, uint Pid)> FindSessions(
        string target,
        Func<string, HashSet<uint>> getFamilyPids,
        Func<string, string, bool> isProcessMatching)
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
            var targetFamilyPids = getFamilyPids(target);

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
                if (!isMatch && !string.IsNullOrEmpty(procName) && isProcessMatching(procName, target))
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
                else
                {
                    try { session.Dispose(); }
                    catch (Exception ex)
                    {
                        JarvisLog.Debug("AudioSessionFinder", $"Error disposing non-matching session: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioSessionFinder Warning] Error enumerating audio sessions: {ex.Message}");
        }

        return matched;
    }
}
