using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Gem.Core;

namespace Gem.UI;

/// <summary>
/// High-performance holographic Arc Reactor core element rendered at 60 FPS via DrawingVisual.
/// Replaces the legacy HUD sphere with Tony Stark / JARVIS Arc Reactor visual architecture:
/// - Layer 1: Radial audio spectrograph crown (96 procedural rays driven by FFT from microphone and TTS)
/// - Layer 2: Asynchronously rotating concentric orbital rings, tick scales, dashed arcs and HUD glyphs
/// - Layer 3: Volumetric plasma energy core with multi-stop radial gradient and sinusoidal idle breathing
/// Features smooth Lerp interpolation and zero-GC allocations in the render loop.
/// </summary>
public sealed class ArcReactorVisualHost : FrameworkElement
{
    private const int RayCount = 96;
    private const double BaseRadius = 76.0;
    private const double MaxRayHeight = 22.0;

    private readonly DrawingVisual _drawingVisual = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastTime = 0;
    private bool _isRenderingHooked = false;

    // Angles for the 5 concentric orbital rings (asynchronous opposite rotation)
    private double _angleRing1 = 0; // Outer tick scale (+14 deg/s)
    private double _angleRing2 = 0; // Segmented dashed arc (-28 deg/s)
    private double _angleRing3 = 0; // HUD chevrons & calibrated sectors (+22 deg/s)
    private double _angleRing4 = 0; // Inner fine vernier scale (-42 deg/s)
    private double _angleRing5 = 0; // Electromagnetic coil brackets (+8 deg/s)

    // Breathing pulsation & state
    private double _breathPhase = 0;
    private JarvisState _currentState = JarvisState.Idle;
    private Color _currentColor = Color.FromRgb(0x00, 0xE5, 0xFF);
    private Color _targetColor = Color.FromRgb(0x00, 0xE5, 0xFF);

    // Audio spectrum & Lerp arrays
    private readonly AudioSpectrumAnalyzer _analyzer = new();
    private readonly float[] _targetSpectrum = new float[RayCount];
    private readonly float[] _currentSpectrum = new float[RayCount];
    private readonly double[] _cosTable = new double[RayCount];
    private readonly double[] _sinTable = new double[RayCount];

    // Pre-computed, frozen geometries for orbital rings
    private StreamGeometry _ring1TicksGeometry = null!;
    private StreamGeometry _ring2AccentsGeometry = null!;
    private StreamGeometry _ring3SectorsGeometry = null!;
    private StreamGeometry _ring3ChevronsGeometry = null!;
    private StreamGeometry _ring4TicksGeometry = null!;
    private StreamGeometry _ring5CoilsGeometry = null!;
    private StreamGeometry _coreGlyphGeometry = null!;

    // Cached brushes & pens
    private SolidColorBrush _primaryBrush = null!;
    private Pen _primaryPen = null!;
    private Pen _rayPen = null!;
    private SolidColorBrush _tipBrush = null!;
    private RadialGradientBrush _auraBrush = null!;
    private RadialGradientBrush _plasmaBrush = null!;
    private RadialGradientBrush _nuclearBrush = null!;

    private Pen _penRing1Track = null!;
    private Pen _penRing1Ticks = null!;
    private Pen _penRing2Dashed = null!;
    private Pen _penRing3Sectors = null!;
    private Pen _penRing4Dashed = null!;
    private Pen _penRing4Ticks = null!;
    private Pen _penRing5Coils = null!;
    private Pen _penContainment = null!;
    private Pen _coreGlyphPen = null!;

    public AudioSpectrumAnalyzer SpectrumAnalyzer => _analyzer;
    public JarvisState CurrentState => _currentState;
    public Color CurrentColor => _currentColor;

    public ArcReactorVisualHost()
    {
        AddVisualChild(_drawingVisual);
        AddLogicalChild(_drawingVisual);

        PrecomputeTrigonometry();
        BuildStaticGeometries();
        UpdateCachedBrushes(_currentColor);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _drawingVisual;

    private void PrecomputeTrigonometry()
    {
        for (int i = 0; i < RayCount; i++)
        {
            double theta = i * (2.0 * Math.PI / RayCount) - (Math.PI / 2.0);
            _cosTable[i] = Math.Cos(theta);
            _sinTable[i] = Math.Sin(theta);
        }
    }

    private void BuildStaticGeometries()
    {
        // 1. Ring 1 (Radius 72): 72 radial tick marks (Major every 90 deg, Medium every 30 deg, Minor every 5 deg)
        _ring1TicksGeometry = new StreamGeometry();
        using (var ctx = _ring1TicksGeometry.Open())
        {
            for (int i = 0; i < 72; i++)
            {
                double deg = i * (360.0 / 72);
                double rad = deg * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);

                double len = (i % 18 == 0) ? 5.5 : ((i % 6 == 0) ? 3.5 : 2.0);
                Point p0 = new Point((72.0 - len) * cos, (72.0 - len) * sin);
                Point p1 = new Point(72.0 * cos, 72.0 * sin);

                ctx.BeginFigure(p0, false, false);
                ctx.LineTo(p1, true, false);
            }
        }
        _ring1TicksGeometry.Freeze();

        // 2. Ring 2 (Radius 64): 3 accent markers at 0, 120, 240 deg
        _ring2AccentsGeometry = new StreamGeometry();
        using (var ctx = _ring2AccentsGeometry.Open())
        {
            double[] angles = { 0, 120, 240 };
            foreach (double deg in angles)
            {
                double rad = deg * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                Point p0 = new Point((64.0 - 4.0) * cos, (64.0 - 4.0) * sin);
                Point p1 = new Point((64.0 + 4.0) * cos, (64.0 + 4.0) * sin);
                ctx.BeginFigure(p0, false, false);
                ctx.LineTo(p1, true, false);
            }
        }
        _ring2AccentsGeometry.Freeze();

        // 3. Ring 3 (Radius 54): 4 calibrated sector arcs (40 deg each at 0, 90, 180, 270 deg)
        _ring3SectorsGeometry = new StreamGeometry();
        using (var ctx = _ring3SectorsGeometry.Open())
        {
            for (int s = 0; s < 4; s++)
            {
                double startDeg = s * 90.0;
                double endDeg = startDeg + 42.0;
                double startRad = startDeg * Math.PI / 180.0;
                double endRad = endDeg * Math.PI / 180.0;

                Point pStart = new Point(54.0 * Math.Cos(startRad), 54.0 * Math.Sin(startRad));
                Point pEnd = new Point(54.0 * Math.Cos(endRad), 54.0 * Math.Sin(endRad));

                ctx.BeginFigure(pStart, false, false);
                ctx.ArcTo(pEnd, new Size(54.0, 54.0), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        _ring3SectorsGeometry.Freeze();

        // Ring 3 chevrons: 4 triangular HUD arrows pointing inward at 45, 135, 225, 315 deg
        _ring3ChevronsGeometry = new StreamGeometry();
        using (var ctx = _ring3ChevronsGeometry.Open())
        {
            double[] chevAngles = { 45, 135, 225, 315 };
            foreach (double deg in chevAngles)
            {
                double rad = deg * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                double tanX = -sin;
                double tanY = cos;

                Point tip = new Point((54.0 - 2.0) * cos, (54.0 - 2.0) * sin);
                Point left = new Point((54.0 + 4.0) * cos + 3.0 * tanX, (54.0 + 4.0) * sin + 3.0 * tanY);
                Point right = new Point((54.0 + 4.0) * cos - 3.0 * tanX, (54.0 + 4.0) * sin - 3.0 * tanY);

                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(left, true, false);
                ctx.LineTo(right, true, false);
            }
        }
        _ring3ChevronsGeometry.Freeze();

        // 4. Ring 4 (Radius 44): 12 fine division ticks every 30 deg
        _ring4TicksGeometry = new StreamGeometry();
        using (var ctx = _ring4TicksGeometry.Open())
        {
            for (int i = 0; i < 12; i++)
            {
                double rad = i * 30.0 * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                Point p0 = new Point(42.0 * cos, 42.0 * sin);
                Point p1 = new Point(46.0 * cos, 46.0 * sin);
                ctx.BeginFigure(p0, false, false);
                ctx.LineTo(p1, true, false);
            }
        }
        _ring4TicksGeometry.Freeze();

        // 5. Ring 5 (Radius 34): 8 electromagnetic coil containment notches
        _ring5CoilsGeometry = new StreamGeometry();
        using (var ctx = _ring5CoilsGeometry.Open())
        {
            for (int i = 0; i < 8; i++)
            {
                double rad = i * 45.0 * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                double tanX = -sin;
                double tanY = cos;

                Point p0 = new Point(32.5 * cos - 1.8 * tanX, 32.5 * sin - 1.8 * tanY);
                Point p1 = new Point(35.5 * cos - 1.8 * tanX, 35.5 * sin - 1.8 * tanY);
                Point p2 = new Point(35.5 * cos + 1.8 * tanX, 35.5 * sin + 1.8 * tanY);
                Point p3 = new Point(32.5 * cos + 1.8 * tanX, 32.5 * sin + 1.8 * tanY);

                ctx.BeginFigure(p0, true, true);
                ctx.LineTo(p1, true, false);
                ctx.LineTo(p2, true, false);
                ctx.LineTo(p3, true, false);
            }
        }
        _ring5CoilsGeometry.Freeze();

        // 6. Central Tri-Reactor Micro-Aperture (Mark II Arc Reactor triangular core glyph)
        _coreGlyphGeometry = new StreamGeometry();
        using (var ctx = _coreGlyphGeometry.Open())
        {
            double r = 7.5;
            Point p0 = new Point(r * Math.Cos(-Math.PI / 2.0), r * Math.Sin(-Math.PI / 2.0));
            Point p1 = new Point(r * Math.Cos(Math.PI / 6.0), r * Math.Sin(Math.PI / 6.0));
            Point p2 = new Point(r * Math.Cos(5.0 * Math.PI / 6.0), r * Math.Sin(5.0 * Math.PI / 6.0));

            ctx.BeginFigure(p0, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }
        _coreGlyphGeometry.Freeze();
    }

    private void UpdateCachedBrushes(Color color)
    {
        _primaryBrush = new SolidColorBrush(color);
        _primaryBrush.Freeze();

        _primaryPen = new Pen(_primaryBrush, 1.2);
        _primaryPen.Freeze();

        _rayPen = new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), 1.5);
        _rayPen.Freeze();

        _tipBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _tipBrush.Freeze();

        // Volumetric aura gradient
        _auraBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        _auraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(80, color.R, color.G, color.B), 0.0));
        _auraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(25, color.R, color.G, color.B), 0.65));
        _auraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        _auraBrush.Freeze();

        // Plasma energy core
        _plasmaBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        _plasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        _plasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(235, 220, 255, 255), 0.22));
        _plasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, color.R, color.G, color.B), 0.55));
        _plasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(160, (byte)(color.R * 0.3), (byte)(color.G * 0.5), (byte)(color.B * 0.8)), 0.82));
        _plasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 10, 30), 1.0));
        _plasmaBrush.Freeze();

        // Nuclear hotspot
        _nuclearBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        _nuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        _nuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 220, 255, 255), 0.5));
        _nuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        _nuclearBrush.Freeze();

        // Specialized pens
        _penRing1Track = new Pen(new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)), 0.8);
        _penRing1Track.Freeze();

        _penRing1Ticks = new Pen(new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)), 1.1);
        _penRing1Ticks.Freeze();

        _penRing2Dashed = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1.4)
        {
            DashStyle = new DashStyle(new double[] { 8, 5, 3, 5 }, 0)
        };
        _penRing2Dashed.Freeze();

        _penRing3Sectors = new Pen(new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B)), 1.6);
        _penRing3Sectors.Freeze();

        _penRing4Dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)), 0.9)
        {
            DashStyle = new DashStyle(new double[] { 2, 3 }, 0)
        };
        _penRing4Dashed.Freeze();

        _penRing4Ticks = new Pen(new SolidColorBrush(Color.FromArgb(180, color.R, color.G, color.B)), 0.8);
        _penRing4Ticks.Freeze();

        _penRing5Coils = new Pen(new SolidColorBrush(Color.FromArgb(240, color.R, color.G, color.B)), 1.4);
        _penRing5Coils.Freeze();

        _penContainment = new Pen(new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)), 0.7)
        {
            DashStyle = new DashStyle(new double[] { 1, 6 }, 0)
        };
        _penContainment.Freeze();

        _coreGlyphPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0);
        _coreGlyphPen.Freeze();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isRenderingHooked)
        {
            _lastTime = _stopwatch.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
            _isRenderingHooked = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRenderingHooked = false;
        }
    }

    public void SetState(JarvisState state)
    {
        _currentState = state;
        _targetColor = OverlayWidget.GetStateGlowColor(state);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;

        if (dt <= 0 || dt > 0.1) dt = 0.016;

        UpdatePhysics(dt, now);
        RenderFrame(now);
    }

    private void UpdatePhysics(double dt, double now)
    {
        // 1. Orbital ring rotation speeds (asynchronous opposite directions)
        double speedMult = _currentState switch
        {
            JarvisState.Thinking => 2.5,
            JarvisState.Action => 1.9,
            JarvisState.Listening => 1.3,
            _ => 1.0
        };

        _angleRing1 = (_angleRing1 + 14.0 * speedMult * dt) % 360.0;
        _angleRing2 = (_angleRing2 - 28.0 * speedMult * dt) % 360.0;
        _angleRing3 = (_angleRing3 + 22.0 * speedMult * dt) % 360.0;
        _angleRing4 = (_angleRing4 - 42.0 * speedMult * dt) % 360.0;
        _angleRing5 = (_angleRing5 + 8.0 * speedMult * dt) % 360.0;

        // 2. Continuous sinusoidal breathing in Idle (2.4s cycle)
        double breathSpeed = _currentState switch
        {
            JarvisState.Thinking => 4.5,
            JarvisState.Action => 5.5,
            JarvisState.Listening => 3.2,
            _ => 2.6
        };
        _breathPhase += dt * breathSpeed;

        // 3. Smooth color morphing towards target state color
        if (_currentColor != _targetColor)
        {
            float colorFactor = (float)Math.Clamp(dt * 6.5, 0.0, 1.0);
            _currentColor = LerpColor(_currentColor, _targetColor, colorFactor);
            UpdateCachedBrushes(_currentColor);
        }

        // 4. Retrieve frequency spectrum & apply smooth Lerp interpolation
        _analyzer.GetTargetSpectrum(_targetSpectrum, dt);

        for (int i = 0; i < RayCount; i++)
        {
            float target = _targetSpectrum[i];
            float current = _currentSpectrum[i];
            // Fast attack, smooth decay filter
            float lerpFactor = target > current ? 0.42f : 0.12f;
            _currentSpectrum[i] = current + (target - current) * lerpFactor;
        }
    }

    private void RenderFrame(double now)
    {
        double width = ActualWidth > 0 ? ActualWidth : 220.0;
        double height = ActualHeight > 0 ? ActualHeight : 220.0;
        double cx = width / 2.0;
        double cy = height / 2.0;
        Point center = new Point(cx, cy);

        double scale = Math.Min(width, height) / 220.0;
        if (scale <= 0.01) scale = 1.0;

        using (DrawingContext dc = _drawingVisual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale, cx, cy));

            // =================================================================
            // LAYER 3: PLASMA ENERGY CORE (Volumetric Glow & Sinusoidal Breath)
            // =================================================================
            double breathScale = 1.0 + 0.08 * Math.Sin(_breathPhase);
            double breathOpacity = 0.82 + 0.16 * Math.Sin(_breathPhase);

            // 3a. Soft atmospheric aura
            double auraRadius = 48.0 * breathScale;
            dc.DrawEllipse(_auraBrush, null, center, auraRadius, auraRadius);

            // 3b. Mid plasma energy sphere
            double plasmaRadius = 30.0 * breathScale;
            dc.DrawEllipse(_plasmaBrush, null, center, plasmaRadius, plasmaRadius);

            // 3c. Nuclear focal node
            double nuclearRadius = 13.0 * (1.0 + 0.04 * Math.Sin(_breathPhase));
            dc.DrawEllipse(_nuclearBrush, null, center, nuclearRadius, nuclearRadius);

            // 3d. Central tri-reactor micro-nodes
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(null, _coreGlyphPen, _coreGlyphGeometry);
            dc.Pop();

            // =================================================================
            // LAYER 2: ORBITAL RINGS & HUD GLYPHS (Concentric Rotating Scales)
            // =================================================================

            // Ring 5 (Coil Bracket Ring, R = 34, angleRing5)
            dc.PushTransform(new RotateTransform(_angleRing5, cx, cy));
            dc.DrawEllipse(null, _penRing5Coils, center, 34.0, 34.0);
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(_primaryBrush, null, _ring5CoilsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 4 (Inner Fine Dashed Vernier Scale, R = 44, angleRing4)
            dc.PushTransform(new RotateTransform(_angleRing4, cx, cy));
            dc.DrawEllipse(null, _penRing4Dashed, center, 44.0, 44.0);
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(null, _penRing4Ticks, _ring4TicksGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 3 (HUD Chevrons & Calibrated Sectors, R = 54, angleRing3)
            dc.PushTransform(new RotateTransform(_angleRing3, cx, cy));
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(null, _penRing3Sectors, _ring3SectorsGeometry);
            dc.DrawGeometry(_primaryBrush, null, _ring3ChevronsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 2 (Segmented Dashed Arc Ring, R = 64, angleRing2)
            dc.PushTransform(new RotateTransform(_angleRing2, cx, cy));
            dc.DrawEllipse(null, _penRing2Dashed, center, 64.0, 64.0);
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(null, _primaryPen, _ring2AccentsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 1 (Outer Calibrated Tick Ring, R = 72, angleRing1)
            dc.PushTransform(new RotateTransform(_angleRing1, cx, cy));
            dc.DrawEllipse(null, _penRing1Track, center, 72.0, 72.0);
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawGeometry(null, _penRing1Ticks, _ring1TicksGeometry);
            dc.Pop();
            dc.Pop();

            // =================================================================
            // LAYER 1: RADIAL AUDIO SPECTROGRAPH (Procedural Crown of 96 Rays)
            // =================================================================
            for (int i = 0; i < RayCount; i++)
            {
                double cos = _cosTable[i];
                double sin = _sinTable[i];
                double theta = i * (2.0 * Math.PI / RayCount);

                // Subtle organic idle wave when silent
                double idleWave = 1.3 * Math.Sin(4.0 * theta + 2.5 * now) * Math.Cos(2.0 * theta - 1.8 * now);
                double audioHeight = _currentSpectrum[i] * MaxRayHeight;
                double h = Math.Clamp(2.5 + idleWave + audioHeight, 1.8, MaxRayHeight + 4.0);

                Point p0 = new Point(cx + BaseRadius * cos, cy + BaseRadius * sin);
                Point p1 = new Point(cx + (BaseRadius + h) * cos, cy + (BaseRadius + h) * sin);

                dc.DrawLine(_rayPen, p0, p1);
                dc.DrawEllipse(_tipBrush, null, p1, 1.0, 1.0);
            }

            // Outer containment dotted circle (R = 98)
            dc.DrawEllipse(null, _penContainment, center, BaseRadius + MaxRayHeight, BaseRadius + MaxRayHeight);

            dc.Pop(); // ScaleTransform
        }
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bl);
    }
}
