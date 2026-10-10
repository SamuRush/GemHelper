using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Gem.Win32;

namespace Gem.UI;

/// <summary>
/// Lightweight transparent click-through fullscreen overlay window for Overdrive Shatter particle explosions.
/// Spans SystemParameters.VirtualScreen and allows glowing crimson energy particles to disperse 300-700px
/// across the entire desktop without being clipped by the HUD widget frame.
/// Uses Win32 WS_EX_TRANSPARENT | WS_EX_LAYERED for click-through zero-interruption,
/// a fixed 42-shard struct pool, and batched DrawingVisual StreamGeometry for 60 FPS zero-GC rendering.
/// </summary>
public sealed class OverdriveParticleOverlayWindow : Window
{
    public const int ParticleCount = 42;

    public struct ScreenShardParticle
    {
        public double Angle;
        public double MaxDistance;
        public double CurrentDistance;
        public double BaseSize;
        public double Rotation;
        public double RotSpeed;
        public double CurveDirection;
        public float Alpha;
        public bool Active;
        public double ScreenX;
        public double ScreenY;
    }

    private sealed class OverlayCanvas : FrameworkElement
    {
        public readonly DrawingVisual Visual = new();

        public OverlayCanvas()
        {
            AddVisualChild(Visual);
            AddLogicalChild(Visual);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => Visual;
    }

    private readonly OverlayCanvas _canvas;
    private readonly ScreenShardParticle[] _shards = new ScreenShardParticle[ParticleCount];
    private readonly StreamGeometry _shardsGeometry = new();
    private readonly StreamGeometry _trailsGeometry = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private double _lastTime = 0.0;
    private bool _isRenderingHooked = false;

    private Point _widgetScreenCenter = new(960, 540);
    private bool _isOverdriveActive = false;
    private double _targetProgress = 0.0;
    private double _currentProgress = 0.0;
    private float _beatIntensity = 0.0f;

    // Static frozen brushes & pens for neon crimson Overdrive (#FF003C) and hot white plasma
    private static readonly SolidColorBrush CrimsonCoreBrush;
    private static readonly SolidColorBrush WhiteCoreBrush;
    private static readonly Pen CrimsonGlowPen;
    private static readonly Pen CrimsonTrailPen;

    static OverdriveParticleOverlayWindow()
    {
        CrimsonCoreBrush = new SolidColorBrush(Color.FromArgb(235, 255, 0, 60)); // Neon Crimson #FF003C
        CrimsonCoreBrush.Freeze();

        WhiteCoreBrush = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255)); // Hot White Center
        WhiteCoreBrush.Freeze();

        CrimsonGlowPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 30, 80)), 1.8);
        CrimsonGlowPen.Freeze();

        CrimsonTrailPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 0, 60)), 1.0);
        CrimsonTrailPen.Freeze();
    }

    public OverdriveParticleOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Visibility = Visibility.Hidden;

        // Cover the entire virtual desktop area across all monitors
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _canvas = new OverlayCanvas();
        Content = _canvas;

        InitParticles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowClickThrough(hwnd);
    }

    private void InitParticles()
    {
        var rand = new Random(42);
        for (int i = 0; i < ParticleCount; i++)
        {
            double angle = (i * (2.0 * Math.PI / ParticleCount)) + ((rand.NextDouble() - 0.5) * 0.2);
            // Dispersion distance: 300 to 700 pixels
            double maxDist = 300.0 + rand.NextDouble() * 400.0;
            double baseSize = 4.5 + rand.NextDouble() * 7.5;
            double rotSpeed = ((i % 2 == 0) ? 1.0 : -1.0) * (2.0 + rand.NextDouble() * 3.5);
            double curve = (rand.NextDouble() - 0.5) * 0.8;

            _shards[i] = new ScreenShardParticle
            {
                Angle = angle,
                MaxDistance = maxDist,
                CurrentDistance = 0.0,
                BaseSize = baseSize,
                Rotation = angle,
                RotSpeed = rotSpeed,
                CurveDirection = curve,
                Alpha = 0f,
                Active = false,
                ScreenX = 0,
                ScreenY = 0
            };
        }
    }

    public ScreenShardParticle[] GetShardSnapshot() => (ScreenShardParticle[])_shards.Clone();

    /// <summary>
    /// Updates overdrive state from the HUD widget.
    /// Manages window visibility and the 60 FPS CompositionTarget.Rendering hook.
    /// </summary>
    public void UpdateOverdrive(Point widgetScreenCenter, bool isOverdriveActive, double overdriveProgress, float beatIntensity)
    {
        _widgetScreenCenter = widgetScreenCenter;
        _isOverdriveActive = isOverdriveActive;
        _targetProgress = Math.Clamp(overdriveProgress, 0.0, 1.0);
        _beatIntensity = Math.Clamp(beatIntensity, 0.0f, 1.0f);

        if (_targetProgress > 0.01 || _isOverdriveActive)
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
            StartRendering();
        }
        else if (_targetProgress <= 0.01 && !_isOverdriveActive && _currentProgress <= 0.01)
        {
            StopRendering();
            ClearVisual();
            if (Visibility != Visibility.Hidden)
            {
                Visibility = Visibility.Hidden;
            }
        }
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

    private void ClearVisual()
    {
        using var dc = _canvas.Visual.RenderOpen();
        // Closes with empty visual
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;
        if (dt <= 0.0 || dt > 0.1) dt = 0.016;

        // Smooth progress interpolation
        double progressSpeed = _isOverdriveActive ? 8.0 : 4.0;
        _currentProgress += (_targetProgress - _currentProgress) * Math.Clamp(dt * progressSpeed, 0.0, 1.0);

        // Auto-dormancy check when reverse is completed
        if (_currentProgress <= 0.01 && !_isOverdriveActive && _targetProgress <= 0.01)
        {
            StopRendering();
            ClearVisual();
            Visibility = Visibility.Hidden;
            return;
        }

        UpdatePhysicsAndRender(dt);
    }

    private void UpdatePhysicsAndRender(double dt)
    {
        double winLeft = Left;
        double winTop = Top;
        double originX = _widgetScreenCenter.X - winLeft;
        double originY = _widgetScreenCenter.Y - winTop;

        // Update physics for all 42 screen shards (0 heap allocations)
        for (int i = 0; i < ParticleCount; i++)
        {
            double targetDist = _shards[i].MaxDistance * _currentProgress;
            // Pulsate outward in rhythm with incoming audio beat
            double beatKick = _beatIntensity * 38.0 * _currentProgress;
            double effectiveDist = targetDist + beatKick;

            double speed = _isOverdriveActive ? 11.0 : 5.5;
            _shards[i].CurrentDistance += (effectiveDist - _shards[i].CurrentDistance) * Math.Clamp(dt * speed, 0.0, 1.0);

            _shards[i].Rotation += _shards[i].RotSpeed * dt * (1.0 + _beatIntensity * 2.2);

            double curvedAngle = _shards[i].Angle + (_currentProgress * _shards[i].CurveDirection * 0.45);
            _shards[i].ScreenX = _widgetScreenCenter.X + _shards[i].CurrentDistance * Math.Cos(curvedAngle);
            _shards[i].ScreenY = _widgetScreenCenter.Y + _shards[i].CurrentDistance * Math.Sin(curvedAngle);

            _shards[i].Alpha = (float)Math.Clamp(_currentProgress * (0.6f + _beatIntensity * 0.4f), 0.0, 1.0);
            _shards[i].Active = _shards[i].CurrentDistance > 8.0 && _shards[i].Alpha > 0.02f;
        }

        // Batched zero-GC rendering into DrawingVisual
        using var dc = _canvas.Visual.RenderOpen();

        // 1. Draw laser energy trails connecting shards towards the reactor core during high beat intensity
        if (_beatIntensity > 0.40f && _currentProgress > 0.2)
        {
            using (var tCtx = _trailsGeometry.Open())
            {
                for (int i = 0; i < ParticleCount; i += 3)
                {
                    if (!_shards[i].Active) continue;
                    double sx = _shards[i].ScreenX - winLeft;
                    double sy = _shards[i].ScreenY - winTop;

                    Point pStart = new Point(originX, originY);
                    Point pEnd = new Point(sx, sy);
                    tCtx.BeginFigure(pStart, false, false);
                    tCtx.LineTo(pEnd, true, false);
                }
            }
            dc.DrawGeometry(null, CrimsonTrailPen, _trailsGeometry);
        }

        // 2. Batch sharp diamond crystal shards into _shardsGeometry
        using (var sCtx = _shardsGeometry.Open())
        {
            for (int i = 0; i < ParticleCount; i++)
            {
                if (!_shards[i].Active) continue;

                double sx = _shards[i].ScreenX - winLeft;
                double sy = _shards[i].ScreenY - winTop;
                double size = _shards[i].BaseSize * (1.0 + _beatIntensity * 0.55);
                double rot = _shards[i].Rotation;

                double cosR = Math.Cos(rot);
                double sinR = Math.Sin(rot);
                double tanX = -sinR;
                double tanY = cosR;

                Point sp0 = new Point(sx + size * 1.3 * cosR, sy + size * 1.3 * sinR);
                Point sp1 = new Point(sx + size * 0.5 * tanX, sy + size * 0.5 * tanY);
                Point sp2 = new Point(sx - size * 0.9 * cosR, sy - size * 0.9 * sinR);
                Point sp3 = new Point(sx - size * 0.5 * tanX, sy - size * 0.5 * tanY);

                sCtx.BeginFigure(sp0, true, true);
                sCtx.LineTo(sp1, true, false);
                sCtx.LineTo(sp2, true, false);
                sCtx.LineTo(sp3, true, false);
            }
        }
        dc.DrawGeometry(CrimsonCoreBrush, CrimsonGlowPen, _shardsGeometry);

        // 3. Draw blazing white center cores for brightest shards
        for (int i = 0; i < ParticleCount; i += 2)
        {
            if (!_shards[i].Active) continue;
            double sx = _shards[i].ScreenX - winLeft;
            double sy = _shards[i].ScreenY - winTop;
            double coreR = 1.2 * (1.0 + _beatIntensity * 0.5);
            dc.DrawEllipse(WhiteCoreBrush, null, new Point(sx, sy), coreR, coreR);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopRendering();
        base.OnClosed(e);
    }
}
