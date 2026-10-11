using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
/// Displays a 60 FPS borderless fullscreen canvas with selective click-through:
/// - Core circle (radius 75px): HTCLIENT (left-drag positioning, double-click settings, right-click menu, tooltips)
/// - Rest of screen: HTTRANSPARENT (all mouse clicks and wheel pass directly through to desktop & background apps)
/// Displays dynamic radial FFT spectrum rays (uncapped), plasma core, biomorphic AI face,
/// elastic screen-boundary bouncing shard particles, and stuck shard mini-equalizers.
/// </summary>
public sealed partial class OverlayWidget : Window
{
    public static OverlayWidget? Instance { get; private set; }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;
    public const double CoreHitRadius = 75.0;

    private JarvisState _currentState = JarvisState.Idle;
    private readonly DispatcherTimer _actionResetTimer;
    private readonly DispatcherTimer _subtitleFadeTimer;
    private readonly DispatcherTimer _timerPollingTimer;
    private readonly DispatcherTimer _periodicMemoryTrimTimer;
    private SettingsWindow? _settingsWindow;
    private LlmIntentService? _llmService;
    private VoiceListener? _attachedListener;
    private IVoiceFeedbackService? _attachedFeedback;
    private readonly WasapiLoopbackCaptureService _loopbackCaptureService = new();
    private OverdriveParticleOverlayWindow? _particleOverlay;
    private bool _isSpeaking = false;
    private bool _isOverdriveGlowApplied = false;

    private bool _isDraggingCore = false;
    private Point _dragStartPos;
    private Point _initialCenter;

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

            _particleOverlay = new OverdriveParticleOverlayWindow();
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            ArcReactor.HostHwnd = source.Handle;
        }
    }

    /// <summary>
    /// Selective click-through hook via Win32 WM_NCHITTEST:
    /// Inside CoreHitRadius (~75px) -> HTCLIENT (interactive clicks, drag, context menu).
    /// Outside CoreHitRadius -> HTTRANSPARENT (clicks pass straight through to desktop/background apps).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            if (_isDraggingCore)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }

            int screenX = unchecked((short)(long)lParam);
            int screenY = unchecked((short)((long)lParam >> 16));

            Point clientPt;
            try
            {
                clientPt = PointFromScreen(new Point(screenX, screenY));
            }
            catch
            {
                clientPt = new Point(screenX - Left, screenY - Top);
            }

            Point center = ArcReactor.Center;
            double dx = clientPt.X - center.X;
            double dy = clientPt.Y - center.Y;

            if (dx * dx + dy * dy <= CoreHitRadius * CoreHitRadius)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }

            handled = true;
            return (IntPtr)HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }


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


    public void AttachServices(LlmIntentService llmService)
    {
        _llmService = llmService;
    }

    /// <summary>
    /// Positions the overlay as a borderless fullscreen window covering the primary display,
    /// with the Arc Reactor core positioned in the bottom-right corner of the work area.
    /// </summary>
    public void PositionInBottomRightCorner()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920.0;
        Height = SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080.0;

        var workArea = SystemParameters.WorkArea;
        ArcReactor.Center = new Point(workArea.Right - 130.0, workArea.Bottom - 130.0);
        UpdateAuxiliaryPositions();
    }

    /// <summary>
    /// Synchronizes auxiliary UI elements (Glow anchor ellipse, Timer badge) to ArcReactor.Center.
    /// </summary>
    public void UpdateAuxiliaryPositions()
    {
        Point center = ArcReactor.Center;
        Canvas.SetLeft(CoreGlowAnchor, center.X - CoreGlowAnchor.Width / 2.0);
        Canvas.SetTop(CoreGlowAnchor, center.Y - CoreGlowAnchor.Height / 2.0);

        double timerW = TimerOverlayBorder.ActualWidth > 0 ? TimerOverlayBorder.ActualWidth : 60.0;
        Canvas.SetLeft(TimerOverlayBorder, center.X - timerW / 2.0);
        Canvas.SetTop(TimerOverlayBorder, center.Y + 48.0);
    }


    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            Point mousePos = e.GetPosition(this);
            Point center = ArcReactor.Center;
            double dx = mousePos.X - center.X;
            double dy = mousePos.Y - center.Y;

            if (dx * dx + dy * dy <= CoreHitRadius * CoreHitRadius)
            {
                _isDraggingCore = true;
                _dragStartPos = mousePos;
                _initialCenter = center;
                CaptureMouse();
                e.Handled = true;
            }
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingCore)
        {
            Point curPos = e.GetPosition(this);
            double deltaX = curPos.X - _dragStartPos.X;
            double deltaY = curPos.Y - _dragStartPos.Y;

            double maxW = ActualWidth > 0 ? ActualWidth : (SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920.0);
            double maxH = ActualHeight > 0 ? ActualHeight : (SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080.0);

            double newX = Math.Clamp(_initialCenter.X + deltaX, CoreHitRadius, maxW - CoreHitRadius);
            double newY = Math.Clamp(_initialCenter.Y + deltaY, CoreHitRadius, maxH - CoreHitRadius);

            ArcReactor.Center = new Point(newX, newY);
            UpdateAuxiliaryPositions();
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingCore && e.ChangedButton == MouseButton.Left)
        {
            _isDraggingCore = false;
            ReleaseMouseCapture();
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
        _loopbackCaptureService.OnDataAvailable += OnLoopbackDataAvailable;
        _loopbackCaptureService.Start();
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
    private void OnLoopbackDataAvailable(byte[] buffer, int bytesRecorded, NAudio.Wave.WaveFormat waveFormat)
    {
        if (IgnoreTtsInLoopback && _isSpeaking)
        {
            return;
        }

        if (bytesRecorded > 0)
        {
            try
            {
                ArcReactor.SpectrumAnalyzer.ProcessLoopback(buffer, bytesRecorded, waveFormat);
            }
            catch (Exception ex)
            {
                JarvisLog.Debug("HUD", $"OnLoopbackDataAvailable handling exception: {ex.Message}");
            }
        }
    }

    private void StopLoopbackCapture()
    {
        _loopbackCaptureService.OnDataAvailable -= OnLoopbackDataAvailable;
        _loopbackCaptureService.Stop();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Instance == this) Instance = null;

        StopLoopbackCapture();
        ArcReactor.StopRendering();
        ArcReactor.OnOverdriveFrame -= OnArcReactorOverdriveFrame;
        _particleOverlay?.Close();
        _particleOverlay = null;

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
