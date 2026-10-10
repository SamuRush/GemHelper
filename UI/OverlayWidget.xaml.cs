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
/// Holographic Arc Reactor HUD overlay widget for JARVIS (Iron Man aesthetic).
/// Displays a 60 FPS DrawingVisual reactor reflecting assistant states:
/// - Idle (#00E5FF, sinusoidal breathing pulsation)
/// - Listening (#FFD600, attentive energetic pulse, reactive FFT crown)
/// - Thinking (#D500F9, high-speed neural rotation)
/// - Action (#00E676, emerald expansion flash)
/// Features real-time radial audio FFT spectrograph, orbital gyro rings, speech subtitles,
/// active timer badges, window dragging (DragMove), and right-click ContextMenu.
/// </summary>
public sealed partial class OverlayWidget : Window
{
    public static OverlayWidget? Instance { get; private set; }

    private JarvisState _currentState = JarvisState.Idle;
    private readonly DispatcherTimer _actionResetTimer;
    private readonly DispatcherTimer _subtitleFadeTimer;
    private readonly DispatcherTimer _timerPollingTimer;
    private readonly DispatcherTimer _periodicMemoryTrimTimer;
    private SettingsWindow? _settingsWindow;
    private LlmIntentService? _llmService;
    private VoiceListener? _attachedListener;
    private IVoiceFeedbackService? _attachedFeedback;
    private NAudio.Wave.WasapiLoopbackCapture? _loopbackCapture;
    private bool _isSpeaking = false;
    private OverdriveParticleOverlayWindow? _particleOverlay;
    private bool _isOverdriveGlowApplied = false;

    public JarvisState CurrentState => _currentState;
    public bool IgnoreTtsInLoopback { get; set; } = true;

    public OverlayWidget()
    {
        Instance = this;
        InitializeComponent();

        try
        {
            var config = AppSettingsService.Load();
            ApplyHudSettings(config.HudSettings);
        }
        catch { }

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

        _periodicMemoryTrimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _periodicMemoryTrimTimer.Tick += (s, e) => MemoryTrimmer.TrimMemory("HUD Periodic", 45.0);

        Loaded += (s, e) =>
        {
            PositionInBottomRightCorner();
            SetState(JarvisState.Idle);
            _timerPollingTimer.Start();
            _periodicMemoryTrimTimer.Start();

            try
            {
                _particleOverlay = new OverdriveParticleOverlayWindow();
                _particleOverlay.Owner = this;
            }
            catch (Exception ex)
            {
                JarvisLog.Warn("HUD", $"Failed to initialize OverdriveParticleOverlayWindow: {ex.Message}");
            }

            ArcReactor.OnOverdriveFrame += OnArcReactorOverdriveFrame;

            if (VoiceListener.Instance != null && _attachedListener == null)
            {
                AttachVoiceListener(VoiceListener.Instance);
            }

            if (CompositeVoiceFeedbackService.Instance != null && _attachedFeedback == null)
            {
                AttachVoiceFeedback(CompositeVoiceFeedbackService.Instance);
            }

            StartLoopbackCapture();
        };

        // Subscribe to Edge-TTS decoded audio output chunks
        EdgeTtsEngine.OnAudioPlaybackChunk += OnTtsAudioPlaybackChunk;
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
    /// Binds voice listener events to the HUD for waveform reactivity, FFT crown, and subtitles.
    /// </summary>
    public void AttachVoiceListener(VoiceListener? listener)
    {
        if (listener == null) return;
        if (_attachedListener != null)
        {
            _attachedListener.OnRmsLevelChanged -= UpdateAudioWaveform;
            _attachedListener.OnAudioBufferAvailable -= OnMicrophoneAudioData;
            _attachedListener.OnPartialResult -= ShowSpeechSubtitle;
            _attachedListener.OnFinalResult -= ShowSpeechSubtitle;
        }

        _attachedListener = listener;
        _attachedListener.OnRmsLevelChanged += UpdateAudioWaveform;
        _attachedListener.OnAudioBufferAvailable += OnMicrophoneAudioData;
        _attachedListener.OnPartialResult += ShowSpeechSubtitle;
        _attachedListener.OnFinalResult += ShowSpeechSubtitle;
    }

    /// <summary>
    /// Binds TTS voice feedback service for real-time speech visualizer envelope tracking.
    /// </summary>
    public void AttachVoiceFeedback(IVoiceFeedbackService? feedback)
    {
        if (feedback == null) return;
        if (_attachedFeedback != null)
        {
            _attachedFeedback.OnSpeakingStarted -= OnTtsSpeakingStarted;
            _attachedFeedback.OnSpeakingFinished -= OnTtsSpeakingFinished;
        }

        _attachedFeedback = feedback;
        _attachedFeedback.OnSpeakingStarted += OnTtsSpeakingStarted;
        _attachedFeedback.OnSpeakingFinished += OnTtsSpeakingFinished;
    }

    private void OnTtsSpeakingStarted()
    {
        _isSpeaking = true;
        ArcReactor.SpectrumAnalyzer.SetTtsSpeaking(true);
    }

    private void OnTtsSpeakingFinished()
    {
        _isSpeaking = false;
        ArcReactor.SpectrumAnalyzer.SetTtsSpeaking(false);
    }

    private void OnMicrophoneAudioData(byte[] buffer, int count)
    {
        ArcReactor.SpectrumAnalyzer.ProcessPcm16(buffer, count);
    }

    private void OnTtsAudioPlaybackChunk(byte[] buffer, int count)
    {
        ArcReactor.SpectrumAnalyzer.ProcessPcm16(buffer, count);
    }

    /// <summary>
    /// Reactive audio waveform pulse binding: scales analyzer energy level according to RMS.
    /// </summary>
    public void UpdateAudioWaveform(float rms)
    {
        ArcReactor.SpectrumAnalyzer.UpdateRms(rms);
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

        // Animate DropShadowEffect glow
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
        Point center;
        try
        {
            center = PointToScreen(new Point(ActualWidth / 2.0, ActualHeight / 2.0));
        }
        catch
        {
            center = new Point(Left + ActualWidth / 2.0, Top + ActualHeight / 2.0);
        }

        _particleOverlay?.UpdateOverdrive(center, isActive, progress, beatIntensity);

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

    private void StartLoopbackCapture()
    {
        try
        {
            _loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture();
            _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
            _loopbackCapture.StartRecording();
            JarvisLog.Info("HUD", "WASAPI Loopback capture started successfully for music and beat reactivity.");
        }
        catch (Exception ex)
        {
            JarvisLog.Warn("HUD", $"WASAPI Loopback capture not available on current audio device: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies HUD visualizer settings (Gain, BassScale, Falloff, Palette, ColorShift, IgnoreTTS) live at runtime.
    /// </summary>
    public void ApplyHudSettings(HudSettingsConfig config)
    {
        if (config == null) return;
        IgnoreTtsInLoopback = config.IgnoreTtsInLoopback;
        ArcReactor.ApplyHudSettings(config);
    }

    /// <summary>
    /// Processes incoming WASAPI Loopback system audio bytes strictly in-place with zero queue allocations.
    /// Bytes are read immediately into SpectrumAnalyzer and discarded without persisting in any List or unbounded Queue.
    /// </summary>
    private void OnLoopbackDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (IgnoreTtsInLoopback && _isSpeaking)
        {
            return;
        }

        if (_loopbackCapture != null && e.BytesRecorded > 0)
        {
            try
            {
                ArcReactor.SpectrumAnalyzer.ProcessLoopback(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("HUD", $"OnLoopbackDataAvailable handling exception: {ex.Message}");
            }
        }
    }

    private void StopLoopbackCapture()
    {
        if (_loopbackCapture != null)
        {
            _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
            try
            {
                _loopbackCapture.StopRecording();
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("HUD", $"StopRecording loopback: {ex.Message}");
            }
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Instance == this) Instance = null;

        StopLoopbackCapture();
        ArcReactor.StopRendering();
        ArcReactor.OnOverdriveFrame -= OnArcReactorOverdriveFrame;

        if (_particleOverlay != null)
        {
            try
            {
                _particleOverlay.Close();
            }
            catch { }
            _particleOverlay = null;
        }

        _actionResetTimer.Stop();
        _actionResetTimer.Tick -= OnActionResetTimerTick;
        _subtitleFadeTimer.Stop();
        _subtitleFadeTimer.Tick -= OnSubtitleFadeTimerTick;
        _timerPollingTimer.Stop();
        _timerPollingTimer.Tick -= OnTimerPollingTick;
        _periodicMemoryTrimTimer.Stop();

        EdgeTtsEngine.OnAudioPlaybackChunk -= OnTtsAudioPlaybackChunk;

        if (_attachedListener != null)
        {
            _attachedListener.OnRmsLevelChanged -= UpdateAudioWaveform;
            _attachedListener.OnAudioBufferAvailable -= OnMicrophoneAudioData;
            _attachedListener.OnPartialResult -= ShowSpeechSubtitle;
            _attachedListener.OnFinalResult -= ShowSpeechSubtitle;
            _attachedListener = null;
        }

        if (_attachedFeedback != null)
        {
            _attachedFeedback.OnSpeakingStarted -= OnTtsSpeakingStarted;
            _attachedFeedback.OnSpeakingFinished -= OnTtsSpeakingFinished;
            _attachedFeedback = null;
        }

        base.OnClosed(e);
    }
}
