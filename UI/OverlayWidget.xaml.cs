using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Gem.Core;
using Gem.Services;
using Gem.Voice;

namespace Gem.UI;

/// <summary>
/// Minimalist floating HUD overlay widget for JARVIS.
/// Displays digital core animation reflecting assistant states (Idle, Listening, Thinking, Action),
/// reactive audio waveform pulses, real-time speech subtitles, and active timer countdown overlay.
/// Memory-optimized: strictly stops and detaches previous animation clocks to prevent composition clock accumulation.
/// </summary>
public sealed partial class OverlayWidget : Window
{
    // =========================================================================
    // Static Cached Colors (Prevents string parsing & heap churn on every transition)
    // =========================================================================
    private static readonly Color ColorAuraIdle1 = Color.FromRgb(0x00, 0xD2, 0xFF);
    private static readonly Color ColorAuraIdle2 = Color.FromRgb(0x00, 0x66, 0xCC);
    private static readonly Color ColorHighlightIdle = Color.FromRgb(0xE6, 0xFF, 0xFF);
    private static readonly Color ColorPrimaryIdle = Color.FromRgb(0x00, 0xD2, 0xFF);
    private static readonly Color ColorSecondaryIdle = Color.FromRgb(0x00, 0x55, 0xAA);
    private static readonly Color ColorDarkIdle = Color.FromRgb(0x00, 0x18, 0x33);

    private static readonly Color ColorAuraListen1 = Color.FromRgb(0xFF, 0xD6, 0x00);
    private static readonly Color ColorAuraListen2 = Color.FromRgb(0xFF, 0xA0, 0x00);
    private static readonly Color ColorHighlightListen = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color ColorPrimaryListen = Color.FromRgb(0xFF, 0xD6, 0x00);
    private static readonly Color ColorSecondaryListen = Color.FromRgb(0xFF, 0x8F, 0x00);
    private static readonly Color ColorDarkListen = Color.FromRgb(0x33, 0x22, 0x00);

    private static readonly Color ColorAuraThink1 = Color.FromRgb(0xD5, 0x00, 0xF9);
    private static readonly Color ColorAuraThink2 = Color.FromRgb(0xAA, 0x00, 0xFF);
    private static readonly Color ColorHighlightThink = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color ColorPrimaryThink = Color.FromRgb(0xD5, 0x00, 0xF9);
    private static readonly Color ColorSecondaryThink = Color.FromRgb(0x7B, 0x1F, 0xA2);
    private static readonly Color ColorDarkThink = Color.FromRgb(0x1A, 0x00, 0x33);

    private static readonly Color ColorWhite = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color ColorAuraAction1 = Color.FromRgb(0x00, 0xE6, 0x76);
    private static readonly Color ColorAuraAction2 = Color.FromRgb(0x00, 0xC8, 0x53);
    private static readonly Color ColorHighlightAction = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color ColorPrimaryAction = Color.FromRgb(0x00, 0xE6, 0x76);
    private static readonly Color ColorSecondaryAction = Color.FromRgb(0x00, 0xA0, 0x40);
    private static readonly Color ColorDarkAction = Color.FromRgb(0x00, 0x33, 0x18);

    private JarvisState _currentState = JarvisState.Idle;
    private readonly DispatcherTimer _actionResetTimer;
    private readonly DispatcherTimer _subtitleFadeTimer;
    private readonly DispatcherTimer _timerPollingTimer;
    private SettingsWindow? _settingsWindow;
    private LlmIntentService? _llmService;
    private VoiceListener? _attachedListener;

    public JarvisState CurrentState => _currentState;

    public OverlayWidget()
    {
        InitializeComponent();

        _actionResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500)
        };
        _actionResetTimer.Tick += OnActionResetTimerTick;

        _subtitleFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _subtitleFadeTimer.Tick += OnSubtitleFadeTimerTick;

        _timerPollingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timerPollingTimer.Tick += OnTimerPollingTick;

        Loaded += (s, e) =>
        {
            PositionInBottomRightCorner();
            SetState(JarvisState.Idle);
            _timerPollingTimer.Start();

            if (VoiceListener.Instance != null && _attachedListener == null)
            {
                AttachVoiceListener(VoiceListener.Instance);
            }
        };
    }

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
    /// Binds voice listener events to the HUD for waveform reactivity and subtitles.
    /// </summary>
    public void AttachVoiceListener(VoiceListener? listener)
    {
        if (listener == null) return;
        if (_attachedListener != null)
        {
            _attachedListener.OnRmsLevelChanged -= UpdateAudioWaveform;
            _attachedListener.OnPartialResult -= ShowSpeechSubtitle;
            _attachedListener.OnFinalResult -= ShowSpeechSubtitle;
        }

        _attachedListener = listener;
        _attachedListener.OnRmsLevelChanged += UpdateAudioWaveform;
        _attachedListener.OnPartialResult += ShowSpeechSubtitle;
        _attachedListener.OnFinalResult += ShowSpeechSubtitle;
    }

    /// <summary>
    /// Reactive audio waveform pulse binding: dynamically scales neon ring according to RMS.
    /// </summary>
    public void UpdateAudioWaveform(float rms)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateAudioWaveform(rms));
            return;
        }

        if (_currentState == JarvisState.Action) return;

        float normalized = Math.Clamp(rms / 2500f, 0f, 1f);
        double targetScale = 1.0 + (normalized * 0.45);
        double targetOpacity = 0.4 + (normalized * 0.6);

        RingScale.ScaleX = targetScale;
        RingScale.ScaleY = targetScale;
        InnerRing.Opacity = targetOpacity;
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

    public void AttachServices(LlmIntentService llmService)
    {
        _llmService = llmService;
    }

    /// <summary>
    /// Positions the overlay in the bottom right corner of the primary display above the taskbar.
    /// </summary>
    public void PositionInBottomRightCorner()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;
    }

    /// <summary>
    /// Completely detaches and stops all active animation clocks across all animated properties.
    /// Crucial for preventing memory leaks in WPF where forever-running clocks pile up on properties.
    /// </summary>
    private void StopCurrentAnimations()
    {
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AuraGlow.BeginAnimation(UIElement.OpacityProperty, null);

        AuraColor1.BeginAnimation(GradientStop.ColorProperty, null);
        AuraColor2.BeginAnimation(GradientStop.ColorProperty, null);
        CoreStopHighlight.BeginAnimation(GradientStop.ColorProperty, null);
        CoreStopPrimary.BeginAnimation(GradientStop.ColorProperty, null);
        CoreStopSecondary.BeginAnimation(GradientStop.ColorProperty, null);
        CoreStopDark.BeginAnimation(GradientStop.ColorProperty, null);
    }

    /// <summary>
    /// Thread-safe state update that transitions DropShadowEffect glow via WPF Storyboard
    /// and safely stops previous animations before starting new ones.
    /// </summary>
    public void SetState(JarvisState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetState(state));
            return;
        }

        // Avoid re-applying the same continuous animation if already in that state
        if (_currentState == state && state != JarvisState.Action)
        {
            return;
        }

        _currentState = state;

        // 1. Explicitly stop and clear previous animation clocks
        StopCurrentAnimations();

        // 2. Animate DropShadowEffect glow morphing using WPF Storyboard
        ApplyGlowStoryboard(state);

        // 3. Apply state core animation
        switch (state)
        {
            case JarvisState.Idle:
                _actionResetTimer.Stop();
                ApplyIdleAnimation();
                break;

            case JarvisState.Listening:
                _actionResetTimer.Stop();
                ApplyListeningAnimation();
                break;

            case JarvisState.Thinking:
                _actionResetTimer.Stop();
                ApplyThinkingAnimation();
                break;

            case JarvisState.Action:
                ApplyActionAnimation();
                _actionResetTimer.Stop();
                _actionResetTimer.Start();
                break;
        }
    }

    private void ApplyGlowStoryboard(JarvisState state)
    {
        Color targetColor = GetStateGlowColor(state);
        var glowAnim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        Storyboard.SetTarget(glowAnim, OuterGlow);
        Storyboard.SetTargetProperty(glowAnim, new PropertyPath(DropShadowEffect.ColorProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(glowAnim);
        storyboard.Begin();
    }

    private void ApplyIdleAnimation()
    {
        // 1. Set base colors (Cyan)
        AuraColor1.Color = ColorAuraIdle1;
        AuraColor2.Color = ColorAuraIdle2;
        CoreStopHighlight.Color = ColorHighlightIdle;
        CoreStopPrimary.Color = ColorPrimaryIdle;
        CoreStopSecondary.Color = ColorSecondaryIdle;
        CoreStopDark.Color = ColorDarkIdle;

        // 2. Slow calm "breathing" scale (2.4s)
        var scaleAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 1.08,
            Duration = TimeSpan.FromSeconds(2.4),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);

        // 3. Subtle aura breathing
        var auraAnim = new DoubleAnimation
        {
            From = 0.35,
            To = 0.65,
            Duration = TimeSpan.FromSeconds(2.4),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        AuraGlow.BeginAnimation(UIElement.OpacityProperty, auraAnim, HandoffBehavior.SnapshotAndReplace);

        // 4. Reset ring
        RingScale.ScaleX = 1.0;
        RingScale.ScaleY = 1.0;
        InnerRing.Opacity = 0.3;
    }

    private void ApplyListeningAnimation()
    {
        // 1. Bright Amber/Gold (#FFD600)
        AuraColor1.Color = ColorAuraListen1;
        AuraColor2.Color = ColorAuraListen2;
        CoreStopHighlight.Color = ColorHighlightListen;
        CoreStopPrimary.Color = ColorPrimaryListen;
        CoreStopSecondary.Color = ColorSecondaryListen;
        CoreStopDark.Color = ColorDarkListen;

        // 2. Energetic pulsing scale (0.45s)
        var scaleAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 1.22,
            Duration = TimeSpan.FromMilliseconds(450),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);

        // 3. Strong aura glow
        var auraAnim = new DoubleAnimation
        {
            From = 0.6,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(450),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        AuraGlow.BeginAnimation(UIElement.OpacityProperty, auraAnim, HandoffBehavior.SnapshotAndReplace);

        InnerRing.Opacity = 0.7;
    }

    private void ApplyThinkingAnimation()
    {
        // 1. Shifting Purple/Violet gradient (#D500F9)
        AuraColor1.Color = ColorAuraThink1;
        AuraColor2.Color = ColorAuraThink2;
        CoreStopHighlight.Color = ColorHighlightThink;
        CoreStopPrimary.Color = ColorPrimaryThink;
        CoreStopSecondary.Color = ColorSecondaryThink;
        CoreStopDark.Color = ColorDarkThink;

        // 2. Shimmering pulse (0.75s)
        var scaleAnim = new DoubleAnimation
        {
            From = 0.94,
            To = 1.14,
            Duration = TimeSpan.FromMilliseconds(750),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);

        // 3. Shimmering aura
        var auraAnim = new DoubleAnimation
        {
            From = 0.45,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(750),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        AuraGlow.BeginAnimation(UIElement.OpacityProperty, auraAnim, HandoffBehavior.SnapshotAndReplace);

        InnerRing.Opacity = 0.5;
    }

    private void ApplyActionAnimation()
    {
        // 1. Emerald flash (#00E676)
        AuraColor1.Color = ColorAuraAction1;
        AuraColor2.Color = ColorAuraAction2;
        CoreStopHighlight.Color = ColorHighlightAction;
        CoreStopPrimary.Color = ColorPrimaryAction;
        CoreStopSecondary.Color = ColorSecondaryAction;
        CoreStopDark.Color = ColorDarkAction;

        // 2. Expansion flash and settling
        var scaleAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 1.28,
            Duration = TimeSpan.FromMilliseconds(220),
            AutoReverse = false,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);

        AuraGlow.Opacity = 0.95;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSettings();
    }

    private void ContextMenuSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void ContextMenuClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_llmService);
        _settingsWindow.Owner = this;
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Show();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _actionResetTimer.Stop();
        _actionResetTimer.Tick -= OnActionResetTimerTick;
        _subtitleFadeTimer.Stop();
        _subtitleFadeTimer.Tick -= OnSubtitleFadeTimerTick;
        _timerPollingTimer.Stop();
        _timerPollingTimer.Tick -= OnTimerPollingTick;

        if (_attachedListener != null)
        {
            _attachedListener.OnRmsLevelChanged -= UpdateAudioWaveform;
            _attachedListener.OnPartialResult -= ShowSpeechSubtitle;
            _attachedListener.OnFinalResult -= ShowSpeechSubtitle;
            _attachedListener = null;
        }

        StopCurrentAnimations();
        base.OnClosed(e);
    }
}
