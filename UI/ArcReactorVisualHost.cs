using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Gem.Core;
using Gem.Services;
using Gem.Win32;

namespace Gem.UI;

/// <summary>
/// Holographic Arc Reactor visual host element rendered at 60 FPS via DrawingVisual.
/// Facade coordinating the HUD vector renderer, particle & collision engine,
/// overdrive state machine, and audio spectrum analyzer.
/// </summary>
public sealed class ArcReactorVisualHost : FrameworkElement
{
    public const int RayCount = HudRenderer.RayCount;
    public const double BaseRadius = HudRenderer.BaseRadius;
    public const int DefaultShardCount = 32;
    public const int MaxShardCount = ParticleEngine.MaxShardCount;
    public const int MaxGlitchBlocks = ParticleEngine.MaxGlitchBlocks;

    private readonly DrawingVisual _drawingVisual = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastTime = 0;
    private double _currentTime = 0;
    private bool _isRenderingHooked = false;

    private readonly AudioSpectrumAnalyzer _analyzer = new();
    private readonly WindowBoundsTracker _windowTracker = new();
    private readonly OverdriveStateMachine _overdrive = new();
    private readonly ParticleEngine _particleEngine;
    private readonly HudRenderer _renderer = new();

    private double _angleRing1 = 0;
    private double _angleRing2 = 0;
    private double _angleRing3 = 0;
    private double _angleRing4 = 0;
    private double _angleRing5 = 0;
    private double _breathPhase = 0;

    private JarvisState _currentState = JarvisState.Idle;
    private ReactorPalette _currentPalette = ArcReactorPalettes.Idle;
    private double _smoothedSubBass = 0.0;
    private double _smoothedSaturation = 0.0;

    private readonly float[] _targetSpectrum = new float[RayCount];
    private readonly float[] _currentSpectrum = new float[RayCount];

    private Point? _centerOverride = null;
    public Point Center
    {
        get
        {
            if (_centerOverride.HasValue) return _centerOverride.Value;
            double w = ActualWidth > 0 ? ActualWidth : (SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920.0);
            double h = ActualHeight > 0 ? ActualHeight : (SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080.0);
            if (w <= 300) return new Point(w / 2.0, h / 2.0);
            return new Point(w - 150.0, h - 150.0);
        }
        set
        {
            _centerOverride = value;
            _particleEngine?.InitShards(value);
        }
    }

    public IntPtr HostHwnd { get; set; } = IntPtr.Zero;
    public bool EnableMouseRepulsion { get; set; } = true;
    public bool EnableWindowCollisions { get; set; } = true;
    public bool EnableColorShiftSetting { get; set; } = true;
    public double BassScaleSetting { get; set; } = 1.4;
    public HudColorTheme CurrentTheme { get; set; } = HudColorTheme.ArcReactor;

    public AudioSpectrumAnalyzer SpectrumAnalyzer => _analyzer;
    public JarvisState CurrentState => _currentState;
    public Color CurrentColor => _currentPalette.Color;
    public WindowBoundsTracker WindowTracker => _windowTracker;

    public double GainSetting
    {
        get => _analyzer.Gain;
        set => _analyzer.Gain = value;
    }

    public double FalloffSetting
    {
        get => _analyzer.Falloff;
        set => _analyzer.Falloff = value;
    }

    public bool EnableShatterOverdrive
    {
        get => _overdrive.EnableShatterOverdrive;
        set => _overdrive.EnableShatterOverdrive = value;
    }

    public bool EnableFullscreenOverdrive
    {
        get => _overdrive.EnableFullscreenOverdrive;
        set => _overdrive.EnableFullscreenOverdrive = value;
    }

    public double OverdriveThreshold
    {
        get => _overdrive.OverdriveThreshold;
        set => _overdrive.OverdriveThreshold = value;
    }

    public double OverdriveCooldown
    {
        get => _overdrive.OverdriveCooldown;
        set => _overdrive.OverdriveCooldown = value;
    }

    public double OverdriveDuration
    {
        get => _overdrive.OverdriveDuration;
        set => _overdrive.OverdriveDuration = value;
    }

    public bool EnableMemoryGlitch
    {
        get => _particleEngine.EnableMemoryGlitch;
        set => _particleEngine.EnableMemoryGlitch = value;
    }

    public bool IsOverdriveActive => _overdrive.IsOverdriveActive;
    public double OverdriveProgress => _overdrive.OverdriveProgress;
    public double CooldownRemaining => _overdrive.CooldownTimer;
    public bool IsOnCooldown => _overdrive.CooldownTimer > 0.0;
    public bool IsReassembling => _overdrive.IsReassembling;
    public ReassemblyPhase CurrentReassemblyPhase => _overdrive.ReassemblyPhase;
    public bool IsOverrideTitleVisible => (_overdrive.SmoothedCharge >= 0.80 && !_overdrive.IsOverdriveActive) || _overdrive.DetonationTitleBurstTimer > 0.0;
    public double ShockwaveRadius => _overdrive.ShockwaveRadius;
    public double ShockwaveOpacity => _overdrive.ShockwaveOpacity;
    public double WhiteoutIntensity => _overdrive.WhiteoutIntensity;
    public double OverdriveRemainingTimer => _overdrive.OverdriveTimer;
    public double OverloadCharge => _overdrive.SmoothedCharge;

    public int ShardCount
    {
        get => _particleEngine.ActiveShardCount;
        set => _particleEngine.ActiveShardCount = Math.Clamp(value, 20, MaxShardCount);
    }

    public int ActiveGlitchBlockCount
    {
        get
        {
            int count = 0;
            var blocks = _particleEngine.GlitchBlocks;
            for (int i = 0; i < MaxGlitchBlocks; i++)
            {
                if (blocks[i].Active) count++;
            }
            return count;
        }
    }

    public event Action<bool, double, float>? OnOverdriveFrame
    {
        add => _overdrive.OnOverdriveFrame += value;
        remove => _overdrive.OnOverdriveFrame -= value;
    }

    public ArcReactorVisualHost()
    {
        AddVisualChild(_drawingVisual);
        _particleEngine = new ParticleEngine(Center);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public static HudColorTheme ParseTheme(string? palette)
    {
        if (string.IsNullOrWhiteSpace(palette)) return HudColorTheme.ArcReactor;
        if (palette.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
            palette.Contains("Пользователь", StringComparison.OrdinalIgnoreCase))
            return HudColorTheme.Custom;
        if (palette.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase)) return HudColorTheme.CyberpunkFlow;
        if (palette.Contains("Solar", StringComparison.OrdinalIgnoreCase)) return HudColorTheme.SolarFlare;
        return HudColorTheme.ArcReactor;
    }

    public void ApplyHudSettings(HudSettingsConfig config)
    {
        if (config == null) return;

        GainSetting = Math.Clamp(config.Gain, 1.0, 15.0);
        BassScaleSetting = Math.Clamp(config.BassScale, 1.0, 3.0);
        FalloffSetting = Math.Clamp(config.Falloff, 0.70, 0.98);

        CurrentTheme = ParseTheme(config.ColorPalette);

        EnableColorShiftSetting = config.EnableColorShift;
        EnableShatterOverdrive = config.EnableShatterOverdrive;
        EnableFullscreenOverdrive = config.EnableFullscreenOverdrive;
        OverdriveThreshold = Math.Clamp(config.OverdriveThreshold, 0.40, 1.50);
        OverdriveCooldown = Math.Clamp(config.OverdriveCooldown, 5.0, 30.0);
        OverdriveDuration = Math.Clamp(config.OverdriveDuration, 5.0, 30.0);
        ShardCount = Math.Clamp(config.ShardCount, 20, MaxShardCount);
        EnableMouseRepulsion = config.EnableMouseRepulsion;
        EnableWindowCollisions = config.EnableWindowCollisions;
        EnableMemoryGlitch = config.EnableMemoryGlitch;

        Color customCore = ColorMath.ParseHex(config.CustomCoreColor, Color.FromRgb(0x00, 0xE5, 0xFF));
        Color customGlow = ColorMath.ParseHex(config.CustomGlowColor, Color.FromRgb(0x00, 0x40, 0xFF));
        Color customRays = ColorMath.ParseHex(config.CustomRaysColor, Color.FromRgb(0x00, 0xFF, 0xAA));
        ArcReactorPalettes.UpdateCustomPalette(customCore, customGlow, customRays);
    }

    public ShardParticle[] GetShardSnapshot()
    {
        int count = _particleEngine.ActiveShardCount;
        var snapshot = new ShardParticle[count];
        Array.Copy(_particleEngine.Shards, snapshot, count);
        return snapshot;
    }

    public GlitchCodeBlock[] GetGlitchBlocksSnapshot()
    {
        var snapshot = new GlitchCodeBlock[MaxGlitchBlocks];
        Array.Copy(_particleEngine.GlitchBlocks, snapshot, MaxGlitchBlocks);
        return snapshot;
    }

    public bool SpawnGlitchBlock(double boundsW, double boundsH, Point center) =>
        _particleEngine.SpawnGlitchBlock(boundsW, boundsH, center);

    public double CalculateSplitDistance() => _overdrive.CalculateSplitDistance();

    public void TriggerBassFrame(float subBassEnergy, float beatIntensity, double dt)
    {
        Point center = Center;
        double boundsW = ActualWidth > 0 ? ActualWidth : (SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920.0);
        double boundsH = ActualHeight > 0 ? ActualHeight : (SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080.0);

        _overdrive.TriggerBassFrame(subBassEnergy, beatIntensity, dt, out bool shouldExplode);
        if (shouldExplode)
        {
            _particleEngine.ExplodeShards(center, beatIntensity);
        }

        // JIT Window tracking
        if (EnableWindowCollisions && (_overdrive.SmoothedCharge > 0.50 || _overdrive.IsOverdriveActive))
        {
            double nowTime = _currentTime > 0 ? _currentTime : _stopwatch.Elapsed.TotalSeconds;
            _windowTracker.Update(HostHwnd, nowTime, boundsW, boundsH);
        }
        else
        {
            _windowTracker.Clear();
        }

        // Mouse coordinates for particle repulsion
        Point? screenMouse = null;
        if (EnableMouseRepulsion && NativeMethods.GetCursorPos(out POINT cursorPt))
        {
            try { screenMouse = PointFromScreen(new Point(cursorPt.X, cursorPt.Y)); }
            catch { screenMouse = new Point(cursorPt.X, cursorPt.Y); }
        }

        double phaseTimer = _overdrive.ReassemblyPhaseTimer;
        _particleEngine.UpdatePhysics(
            dt,
            center,
            boundsW,
            boundsH,
            _overdrive.IsOverdriveActive,
            _overdrive.OverdriveProgress,
            beatIntensity,
            subBassEnergy,
            FalloffSetting,
            _currentSpectrum,
            _windowTracker,
            EnableWindowCollisions,
            EnableMouseRepulsion,
            screenMouse,
            _overdrive.IsReassembling,
            _overdrive.ReassemblyPhase,
            ref phaseTimer,
            out var nextPhase);

        _overdrive.ReassemblyPhaseTimer = phaseTimer;
        _overdrive.ReassemblyPhase = nextPhase;

        _particleEngine.UpdateGlitchBlocks(dt, _currentTime > 0 ? _currentTime : _stopwatch.Elapsed.TotalSeconds, boundsW, boundsH, center, _overdrive.IsOverdriveActive);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _drawingVisual;

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        Point pt = hitTestParameters.HitPoint;
        Point center = Center;
        double dx = pt.X - center.X;
        double dy = pt.Y - center.Y;
        if (dx * dx + dy * dy <= 75.0 * 75.0)
        {
            return new PointHitTestResult(this, pt);
        }
        return null;
    }

    public void StartRendering()
    {
        if (!_isRenderingHooked)
        {
            _lastTime = _stopwatch.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }
    }

    public void StopRendering()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartRendering();
    private void OnUnloaded(object sender, RoutedEventArgs e) => StopRendering();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartRendering();
        else StopRendering();
    }

    public void SetState(JarvisState state)
    {
        _currentState = state;
        _currentPalette = ArcReactorPalettes.GetPalette(state, (float)_smoothedSaturation, CurrentTheme);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        double now = _stopwatch.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTime, 0.001, 0.05);
        _lastTime = now;

        UpdatePhysics(dt, now);

        using (DrawingContext dc = _drawingVisual.RenderOpen())
        {
            _renderer.RenderFrame(
                dc,
                Center,
                ActualWidth,
                ActualHeight,
                now,
                _breathPhase,
                _angleRing1,
                _angleRing2,
                _angleRing3,
                _angleRing4,
                _angleRing5,
                _smoothedSubBass,
                BassScaleSetting,
                GainSetting,
                _currentSpectrum,
                _analyzer.BeatIntensity,
                _currentPalette,
                _overdrive,
                _particleEngine);
        }
    }

    private void UpdatePhysics(double dt, double now)
    {
        _currentTime = now;

        double speedMult = _currentState switch
        {
            JarvisState.Thinking => 2.5,
            JarvisState.Action => 1.9,
            JarvisState.Listening => 1.3,
            _ => 1.0
        };

        float targetSubBass = _analyzer.SubBassEnergy;
        double subBassLerpSpeed = targetSubBass > _smoothedSubBass ? 12.0 : 6.0;
        _smoothedSubBass += (targetSubBass - _smoothedSubBass) * Math.Clamp(dt * subBassLerpSpeed, 0.0, 1.0);

        float targetSaturation = _analyzer.SpectralSaturation;
        _smoothedSaturation += (targetSaturation - _smoothedSaturation) * Math.Clamp(dt * 5.0, 0.0, 1.0);

        double dynamicSpeed = speedMult + _smoothedSubBass * 0.45;
        _angleRing1 = (_angleRing1 + 14.0 * dynamicSpeed * dt) % 360.0;
        _angleRing2 = (_angleRing2 - 28.0 * dynamicSpeed * dt) % 360.0;
        _angleRing3 = (_angleRing3 + 22.0 * dynamicSpeed * dt) % 360.0;
        _angleRing4 = (_angleRing4 - 42.0 * dynamicSpeed * dt) % 360.0;
        _angleRing5 = (_angleRing5 + 8.0 * dynamicSpeed * dt) % 360.0;

        double breathSpeed = _currentState switch
        {
            JarvisState.Thinking => 4.5,
            JarvisState.Action => 5.5,
            JarvisState.Listening => 3.2,
            _ => 2.6
        };
        _breathPhase += dt * breathSpeed;

        TriggerBassFrame(_analyzer.SubBassEnergy, _analyzer.BeatIntensity, dt);

        float effectiveSaturation = EnableColorShiftSetting ? (float)_smoothedSaturation : 0f;
        _currentPalette = ArcReactorPalettes.GetPalette(_currentState, effectiveSaturation, CurrentTheme);

        if (OverdriveProgress > 0.05 && (EnableShatterOverdrive || EnableFullscreenOverdrive))
        {
            int oIdx = Math.Clamp((int)(OverdriveProgress * 63.0), 0, 63);
            _currentPalette = ArcReactorPalettes.OverdriveCrimsonPalettes[oIdx];
        }

        _analyzer.GetTargetSpectrum(_targetSpectrum.AsSpan(), dt);

        float falloffRate = MathF.Pow((float)FalloffSetting, (float)(dt * 60.0));
        for (int i = 0; i < RayCount; i++)
        {
            float target = _targetSpectrum[i];
            float current = _currentSpectrum[i];
            if (target > current) _currentSpectrum[i] = target;
            else _currentSpectrum[i] = Math.Max(target, current * falloffRate);
        }
    }
}
