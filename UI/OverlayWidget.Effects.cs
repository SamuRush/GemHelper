using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Gem.Core;
using Gem.Services;

namespace Gem.UI;

public sealed partial class OverlayWidget
{
    /// <summary>
    /// Gets the specified glow DropShadowEffect color per the roadmap:
    /// Idle: #00E5FF, Listening: #FFD600, Thinking: #D500F9, Action: #00E676.
    /// </summary>
    public static Color GetStateGlowColor(JarvisState state) => state switch
    {
        JarvisState.Idle => Color.FromRgb(0x00, 0xE5, 0xFF),
        JarvisState.Listening => Color.FromRgb(0xFF, 0xD6, 0x00),
        JarvisState.Thinking => Color.FromRgb(0xD5, 0x00, 0xF9),
        JarvisState.Action => Color.FromRgb(0x00, 0xE6, 0x76),
        _ => Color.FromRgb(0x00, 0xE5, 0xFF)
    };

    /// <summary>
    /// Thread-safe state update that transitions DropShadowEffect glow and reactor visuals.
    /// </summary>
    public void SetState(JarvisState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetState(state));
            return;
        }

        if (_currentState == state && state != JarvisState.Action)
        {
            return;
        }

        _currentState = state;
        ArcReactor.SetState(state);

        ApplyGlowAnimation(state);

        if (state == JarvisState.Action)
        {
            _actionResetTimer.Stop();
            _actionResetTimer.Start();
        }
        else
        {
            _actionResetTimer.Stop();
        }
    }

    private void ApplyGlowAnimation(JarvisState state)
    {
        if (_isOverdriveGlowApplied) return;
        Color targetColor = GetStateGlowColor(state);
        var glowAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        OuterGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnArcReactorOverdriveFrame(bool isActive, double progress, float beatIntensity)
    {
        if (isActive && progress > 0.1)
        {
            if (!_isOverdriveGlowApplied)
            {
                _isOverdriveGlowApplied = true;
                AnimateOverdriveGlow(Color.FromRgb(0xFF, 0x00, 0x3C));
            }
        }
        else if (!isActive && progress <= 0.05)
        {
            if (_isOverdriveGlowApplied)
            {
                _isOverdriveGlowApplied = false;
                ApplyGlowAnimation(_currentState);
            }
        }
    }

    private void AnimateOverdriveGlow(Color targetColor)
    {
        var glowAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        OuterGlow.BeginAnimation(DropShadowEffect.ColorProperty, glowAnim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Displays speech recognition subtitles with auto-fadeout after 3 seconds of silence.
    /// </summary>
    public void ShowSpeechSubtitle(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowSpeechSubtitle(text));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        SpeechSubtitleText.Text = text.Trim();
        _subtitleFadeTimer.Stop();
        SpeechSubtitlePopup.IsOpen = true;

        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        SpeechSubtitleBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);

        _subtitleFadeTimer.Start();
    }

    private void OnSubtitleFadeTimerTick(object? sender, EventArgs e)
    {
        _subtitleFadeTimer.Stop();
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (s, args) => SpeechSubtitlePopup.IsOpen = false;
        SpeechSubtitleBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnTimerPollingTick(object? sender, EventArgs e)
    {
        var activeTimers = TimerService.Instance?.GetActiveTimers();
        if (activeTimers != null && activeTimers.Count > 0)
        {
            var earliest = activeTimers.OrderBy(t => t.Remaining).FirstOrDefault();
            if (earliest != null && !earliest.IsExpired)
            {
                TimerOverlayBorder.Visibility = Visibility.Visible;
                TimerOverlayText.Text = $"⏱ {earliest.Remaining:mm\\:ss}";
                UpdateAuxiliaryPositions();
                return;
            }
        }

        TimerOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private void OnActionResetTimerTick(object? sender, EventArgs e)
    {
        _actionResetTimer.Stop();
        if (_currentState == JarvisState.Action)
        {
            SetState(JarvisState.Idle);
        }
    }
}
