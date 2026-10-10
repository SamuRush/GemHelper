using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Gem.Core;
using Gem.Services;

namespace Gem.UI;

/// <summary>
/// Pre-computed and frozen visual palette containing all pens and brushes for a state or color.
/// All brushes, pens, and gradients are frozen strictly once at creation to guarantee zero allocations at 60 FPS.
/// </summary>
public sealed class ReactorPalette
{
    public Color Color { get; }
    public SolidColorBrush PrimaryBrush { get; }
    public Pen PrimaryPen { get; }
    public Pen RayPen { get; }
    public RadialGradientBrush AuraBrush { get; }
    public RadialGradientBrush PlasmaBrush { get; }
    public RadialGradientBrush NuclearBrush { get; }
    public Pen PenRing1Track { get; }
    public Pen PenRing1Ticks { get; }
    public Pen PenRing2Dashed { get; }
    public Pen PenRing3Sectors { get; }
    public Pen PenRing4Dashed { get; }
    public Pen PenRing4Ticks { get; }
    public Pen PenRing5Coils { get; }
    public Pen PenContainment { get; }
    public Pen BiomorphicPen { get; }

    public ReactorPalette(Color color)
    {
        Color = color;

        PrimaryBrush = new SolidColorBrush(color);
        PrimaryBrush.Freeze();

        PrimaryPen = new Pen(PrimaryBrush, 1.2);
        PrimaryPen.Freeze();

        RayPen = new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), 1.4);
        RayPen.Freeze();

        AuraBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        AuraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(80, color.R, color.G, color.B), 0.0));
        AuraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(25, color.R, color.G, color.B), 0.65));
        AuraBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        AuraBrush.Freeze();

        PlasmaBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        PlasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        PlasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(235, 220, 255, 255), 0.22));
        PlasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, color.R, color.G, color.B), 0.55));
        PlasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(160, (byte)(color.R * 0.3), (byte)(color.G * 0.5), (byte)(color.B * 0.8)), 0.82));
        PlasmaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 10, 30), 1.0));
        PlasmaBrush.Freeze();

        NuclearBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        NuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        NuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 220, 255, 255), 0.5));
        NuclearBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        NuclearBrush.Freeze();

        PenRing1Track = new Pen(new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)), 0.8);
        PenRing1Track.Freeze();

        PenRing1Ticks = new Pen(new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)), 1.1);
        PenRing1Ticks.Freeze();

        PenRing2Dashed = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1.4)
        {
            DashStyle = new DashStyle(new double[] { 8, 5, 3, 5 }, 0)
        };
        PenRing2Dashed.Freeze();

        PenRing3Sectors = new Pen(new SolidColorBrush(Color.FromArgb(235, color.R, color.G, color.B)), 1.6);
        PenRing3Sectors.Freeze();

        PenRing4Dashed = new Pen(new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B)), 0.9)
        {
            DashStyle = new DashStyle(new double[] { 2, 3 }, 0)
        };
        PenRing4Dashed.Freeze();

        PenRing4Ticks = new Pen(new SolidColorBrush(Color.FromArgb(180, color.R, color.G, color.B)), 0.8);
        PenRing4Ticks.Freeze();

        PenRing5Coils = new Pen(new SolidColorBrush(Color.FromArgb(240, color.R, color.G, color.B)), 1.4);
        PenRing5Coils.Freeze();

        PenContainment = new Pen(new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)), 0.7)
        {
            DashStyle = new DashStyle(new double[] { 1, 6 }, 0)
        };
        PenContainment.Freeze();

        BiomorphicPen = new Pen(new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)), 1.2);
        BiomorphicPen.Freeze();
    }
}

/// <summary>
/// Color interpolation and HSL color space conversion utilities for Sci-Fi HUD visualizers.
/// </summary>
public static class ColorMath
{
    public static Color HslToRgb(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360.0) + 360.0) % 360.0;
        saturation = Math.Clamp(saturation, 0.0, 1.0);
        lightness = Math.Clamp(lightness, 0.0, 1.0);

        double c = (1.0 - Math.Abs(2.0 * lightness - 1.0)) * saturation;
        double x = c * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
        double m = lightness - c / 2.0;

        double r = 0, g = 0, b = 0;
        if (hue < 60)
        {
            r = c; g = x; b = 0;
        }
        else if (hue < 120)
        {
            r = x; g = c; b = 0;
        }
        else if (hue < 180)
        {
            r = 0; g = c; b = x;
        }
        else if (hue < 240)
        {
            r = 0; g = x; b = c;
        }
        else if (hue < 300)
        {
            r = x; g = 0; b = c;
        }
        else
        {
            r = c; g = 0; b = x;
        }

        byte red = (byte)Math.Clamp((int)Math.Round((r + m) * 255.0), 0, 255);
        byte green = (byte)Math.Clamp((int)Math.Round((g + m) * 255.0), 0, 255);
        byte blue = (byte)Math.Clamp((int)Math.Round((b + m) * 255.0), 0, 255);

        return Color.FromRgb(red, green, blue);
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}

/// <summary>
/// Predefined Sci-Fi holographic color themes for the JARVIS Arc Reactor visualizer.
/// </summary>
public enum HudColorTheme
{
    ArcReactor,
    CyberpunkFlow,
    SolarFlare
}

/// <summary>
/// Static registry of pre-computed, frozen palettes for all JARVIS states and smooth spectral gradient morphing.
/// </summary>
public static class ArcReactorPalettes
{
    public static readonly ReactorPalette Idle = new(Color.FromRgb(0x00, 0xE5, 0xFF));
    public static readonly ReactorPalette Listening = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    public static readonly ReactorPalette Thinking = new(Color.FromRgb(0xD5, 0x00, 0xF9));
    public static readonly ReactorPalette Action = new(Color.FromRgb(0x00, 0xE6, 0x76));

    // Pre-computed smooth spectral gradient palettes (64 frozen items each, 0 allocations at 60 FPS)
    public static readonly ReactorPalette[] SpectrumGradientPalettes = PrecomputeSpectrumPalettes();
    public static readonly ReactorPalette[] CyberpunkGradientPalettes = PrecomputeCyberpunkPalettes();
    public static readonly ReactorPalette[] SolarFlareGradientPalettes = PrecomputeSolarFlarePalettes();

    // Retained for backward-compatibility
    public static readonly ReactorPalette[] BeatPalettes = SpectrumGradientPalettes;

    private static ReactorPalette[] PrecomputeSpectrumPalettes()
    {
        Color baseCyan = Color.FromRgb(0x00, 0xE5, 0xFF);
        Color deepUltramarine = Color.FromRgb(0x00, 0x40, 0xFF);

        var palettes = new ReactorPalette[64];
        for (int i = 0; i < 64; i++)
        {
            float t = i / 63.0f;
            Color c = ColorMath.Lerp(baseCyan, deepUltramarine, t);
            palettes[i] = new ReactorPalette(c);
        }
        return palettes;
    }

    private static ReactorPalette[] PrecomputeCyberpunkPalettes()
    {
        // Cyan (#00E5FF) -> Violet (#8A2BE2) -> Magenta (#F500F5)
        Color cyan = Color.FromRgb(0x00, 0xE5, 0xFF);
        Color violet = Color.FromRgb(0x8A, 0x2B, 0xE2);
        Color magenta = Color.FromRgb(0xF5, 0x00, 0xF5);

        var palettes = new ReactorPalette[64];
        for (int i = 0; i < 64; i++)
        {
            float t = i / 63.0f;
            Color c = t < 0.5f
                ? ColorMath.Lerp(cyan, violet, t * 2.0f)
                : ColorMath.Lerp(violet, magenta, (t - 0.5f) * 2.0f);
            palettes[i] = new ReactorPalette(c);
        }
        return palettes;
    }

    private static ReactorPalette[] PrecomputeSolarFlarePalettes()
    {
        // Amber/Gold (#FFC400) -> Orange (#FF6400) -> Neon Red (#FF141E)
        Color amber = Color.FromRgb(255, 196, 0);
        Color orange = Color.FromRgb(255, 100, 0);
        Color neonRed = Color.FromRgb(255, 20, 30);

        var palettes = new ReactorPalette[64];
        for (int i = 0; i < 64; i++)
        {
            float t = i / 63.0f;
            Color c = t < 0.5f
                ? ColorMath.Lerp(amber, orange, t * 2.0f)
                : ColorMath.Lerp(orange, neonRed, (t - 0.5f) * 2.0f);
            palettes[i] = new ReactorPalette(c);
        }
        return palettes;
    }

    public static ReactorPalette GetThemePalette(HudColorTheme theme, float saturation)
    {
        int index = Math.Clamp((int)(saturation * 63.99f), 0, 63);
        return theme switch
        {
            HudColorTheme.CyberpunkFlow => CyberpunkGradientPalettes[index],
            HudColorTheme.SolarFlare => SolarFlareGradientPalettes[index],
            _ => SpectrumGradientPalettes[index]
        };
    }

    public static ReactorPalette GetPalette(JarvisState state, float saturation = 0f, HudColorTheme theme = HudColorTheme.ArcReactor)
    {
        if (state == JarvisState.Idle)
        {
            return GetThemePalette(theme, saturation);
        }

        return state switch
        {
            JarvisState.Listening => Listening,
            JarvisState.Thinking => Thinking,
            JarvisState.Action => Action,
            _ => Idle
        };
    }
}

/// <summary>
/// High-performance holographic Arc Reactor core element rendered at 60 FPS via DrawingVisual.
/// Replaces the legacy HUD sphere with Tony Stark / JARVIS Arc Reactor visual architecture:
/// - Layer 1: Radial audio spectrograph crown (128 bilateral procedural rays driven by honest FFT)
/// - Layer 2: Asynchronously rotating concentric orbital rings, tick scales, dashed arcs and HUD glyphs
/// - Layer 3: Volumetric plasma energy core with sinusoidal breathing, smooth sub-bass modulation (Lerp), and smooth color morphing
/// - Center: Biomorphic AI cybernetic face & polygonal energy lattice that deforms in sync with rhythm
/// Features smooth Lerp interpolation, event lifecycle management, and zero-GC allocations in the render loop:
/// reusable StreamGeometryContexts, frozen static resource palettes, and reusable transforms.
/// </summary>
public sealed class ArcReactorVisualHost : FrameworkElement
{
    private const int RayCount = AudioSpectrumAnalyzer.RayCount;
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
    private ReactorPalette _currentPalette = ArcReactorPalettes.Idle;

    // Sub-bass and spectral saturation smooth Lerp states
    private double _smoothedSubBass = 0.0;
    private double _smoothedSaturation = 0.0;

    // Audio spectrum & Peak Falloff / Decay arrays
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

    // Reusable dynamic StreamGeometry instances (re-opened every frame via StreamGeometryContext with zero heap allocations)
    private readonly StreamGeometry _dynamicRaysGeometry = new();
    private readonly StreamGeometry _dynamicTipsGeometry = new();
    private readonly StreamGeometry _biomorphicFaceThickGeometry = new();
    private readonly StreamGeometry _biomorphicFaceGeometry = new();

    // Reusable transform objects (updated every frame in-place without new allocations)
    private readonly ScaleTransform _rootScaleTransform = new();
    private readonly RotateTransform _rotRing1 = new();
    private readonly RotateTransform _rotRing2 = new();
    private readonly RotateTransform _rotRing3 = new();
    private readonly RotateTransform _rotRing4 = new();
    private readonly RotateTransform _rotRing5 = new();
    private readonly TranslateTransform _transCenter = new();

    // Static frozen resources shared across all instances
    private static readonly SolidColorBrush StaticWhiteBrush;
    private static readonly SolidColorBrush StaticTipBrush;
    private static readonly Pen StaticCoreGlyphPen;
    private static readonly Pen StaticBiomorphicThickPen;
    private static readonly SolidColorBrush StaticBiomorphicNodeBrush;

    static ArcReactorVisualHost()
    {
        StaticWhiteBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        StaticWhiteBrush.Freeze();

        StaticTipBrush = StaticWhiteBrush;

        StaticCoreGlyphPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0);
        StaticCoreGlyphPen.Freeze();

        StaticBiomorphicThickPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1.5);
        StaticBiomorphicThickPen.Freeze();

        StaticBiomorphicNodeBrush = StaticWhiteBrush;
    }

    public AudioSpectrumAnalyzer SpectrumAnalyzer => _analyzer;
    public JarvisState CurrentState => _currentState;
    public Color CurrentColor => _currentPalette.Color;

    public double GainSetting
    {
        get => _analyzer.Gain;
        set => _analyzer.Gain = value;
    }

    public double BassScaleSetting { get; set; } = 1.4;

    public double FalloffSetting
    {
        get => _analyzer.Falloff;
        set => _analyzer.Falloff = value;
    }

    public HudColorTheme CurrentTheme { get; set; } = HudColorTheme.ArcReactor;

    public bool EnableColorShiftSetting { get; set; } = true;

    public void ApplyHudSettings(HudSettingsConfig config)
    {
        if (config == null) return;
        GainSetting = config.Gain;
        BassScaleSetting = config.BassScale;
        FalloffSetting = config.Falloff;
        CurrentTheme = ParseTheme(config.ColorPalette);
        EnableColorShiftSetting = config.EnableColorShift;
    }

    public static HudColorTheme ParseTheme(string? palette)
    {
        if (string.IsNullOrWhiteSpace(palette)) return HudColorTheme.ArcReactor;
        if (palette.Contains("Cyberpunk", StringComparison.OrdinalIgnoreCase)) return HudColorTheme.CyberpunkFlow;
        if (palette.Contains("Solar", StringComparison.OrdinalIgnoreCase)) return HudColorTheme.SolarFlare;
        return HudColorTheme.ArcReactor;
    }

    public ArcReactorVisualHost()
    {
        AddVisualChild(_drawingVisual);
        AddLogicalChild(_drawingVisual);

        PrecomputeTrigonometry();
        BuildStaticGeometries();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartRendering();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    public void SetState(JarvisState state)
    {
        _currentState = state;
        _currentPalette = ArcReactorPalettes.GetPalette(state, 0f);
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
        // 1. Orbital ring rotation base speeds
        double speedMult = _currentState switch
        {
            JarvisState.Thinking  => 2.5,
            JarvisState.Action    => 1.9,
            JarvisState.Listening => 1.3,
            _ => 1.0
        };

        // Continuous smooth sub-bass modulation (no abrupt beat jumps)
        float targetSubBass = _analyzer.SubBassEnergy;
        double subBassLerpSpeed = targetSubBass > _smoothedSubBass ? 12.0 : 6.0;
        _smoothedSubBass += (targetSubBass - _smoothedSubBass) * Math.Clamp(dt * subBassLerpSpeed, 0.0, 1.0);

        // Spectral saturation smooth Lerp for color gradient
        float targetSaturation = _analyzer.SpectralSaturation;
        _smoothedSaturation += (targetSaturation - _smoothedSaturation) * Math.Clamp(dt * 5.0, 0.0, 1.0);

        // Gentle dynamic speed based on continuous sub-bass energy
        double dynamicSpeed = speedMult + _smoothedSubBass * 0.45;

        _angleRing1 = (_angleRing1 + 14.0 * dynamicSpeed * dt) % 360.0;
        _angleRing2 = (_angleRing2 - 28.0 * dynamicSpeed * dt) % 360.0;
        _angleRing3 = (_angleRing3 + 22.0 * dynamicSpeed * dt) % 360.0;
        _angleRing4 = (_angleRing4 - 42.0 * dynamicSpeed * dt) % 360.0;
        _angleRing5 = (_angleRing5 +  8.0 * dynamicSpeed * dt) % 360.0;

        // Continuous sinusoidal breathing
        double breathSpeed = _currentState switch
        {
            JarvisState.Thinking  => 4.5,
            JarvisState.Action    => 5.5,
            JarvisState.Listening => 3.2,
            _ => 2.6
        };
        _breathPhase += dt * breathSpeed;

        // Zero-allocation frozen palette selection: smooth gradient depending on active theme & color shift
        float effectiveSaturation = EnableColorShiftSetting ? (float)_smoothedSaturation : 0f;
        _currentPalette = ArcReactorPalettes.GetPalette(_currentState, effectiveSaturation, CurrentTheme);

        // Retrieve frequency spectrum (zero allocations via Span)
        _analyzer.GetTargetSpectrum(_targetSpectrum.AsSpan(), dt);

        // Peak Falloff / Decay: instant attack on hits, smooth gravitational decay per user FalloffSetting
        float falloffRate = MathF.Pow((float)FalloffSetting, (float)(dt * 60.0));
        for (int i = 0; i < RayCount; i++)
        {
            float target = _targetSpectrum[i];
            float current = _currentSpectrum[i];
            if (target > current)
            {
                _currentSpectrum[i] = target; // Instant upward attack
            }
            else
            {
                _currentSpectrum[i] = Math.Max(target, current * falloffRate); // Smooth gravitational falloff
            }
        }
    }

    private void RenderFrame(double now)
    {
        double width = ActualWidth > 0 ? ActualWidth : 220.0;
        double height = ActualHeight > 0 ? ActualHeight : 220.0;
        double cx = width / 2.0;
        double cy = height / 2.0;
        Point center = new Point(cx, cy);

        double baseScale = Math.Min(width, height) / 220.0;
        if (baseScale <= 0.01) baseScale = 1.0;

        // Central core radius smoothly modulated by sub-bass energy (20-150 Hz) via Lerp and BassScale setting
        double bassScaleMultiplier = BassScaleSetting / 1.4;
        double subBassScale = 1.0 + (0.12 * bassScaleMultiplier) * _smoothedSubBass;
        double finalScale = baseScale * subBassScale;

        var pal = _currentPalette;

        // Update pre-allocated transform fields in-place (0 allocations)
        _rootScaleTransform.ScaleX = finalScale;
        _rootScaleTransform.ScaleY = finalScale;
        _rootScaleTransform.CenterX = cx;
        _rootScaleTransform.CenterY = cy;

        _rotRing1.Angle = _angleRing1; _rotRing1.CenterX = cx; _rotRing1.CenterY = cy;
        _rotRing2.Angle = _angleRing2; _rotRing2.CenterX = cx; _rotRing2.CenterY = cy;
        _rotRing3.Angle = _angleRing3; _rotRing3.CenterX = cx; _rotRing3.CenterY = cy;
        _rotRing4.Angle = _angleRing4; _rotRing4.CenterX = cx; _rotRing4.CenterY = cy;
        _rotRing5.Angle = _angleRing5; _rotRing5.CenterX = cx; _rotRing5.CenterY = cy;

        _transCenter.X = cx;
        _transCenter.Y = cy;

        using (DrawingContext dc = _drawingVisual.RenderOpen())
        {
            dc.PushTransform(_rootScaleTransform);

            // =================================================================
            // LAYER 3: PLASMA ENERGY CORE (Volumetric Glow & Sinusoidal Breath)
            // =================================================================

            double idleBreath = Math.Sin(_breathPhase);

            // Sinusoidal idle breath smoothly modulated by sub-bass energy (no binary beat flashes)
            double auraBreath    = 1.0 + 0.05 * idleBreath;
            double auraSubBass   = 1.0 + (0.22 * bassScaleMultiplier) * _smoothedSubBass;
            double auraRadius    = 48.0 * auraBreath * auraSubBass;
            dc.DrawEllipse(pal.AuraBrush, null, center, auraRadius, auraRadius);

            double plasmaBreath  = 1.0 + 0.07 * idleBreath;
            double plasmaSubBass = 1.0 + (0.15 * bassScaleMultiplier) * _smoothedSubBass;
            double plasmaRadius  = 30.0 * plasmaBreath * plasmaSubBass;
            dc.DrawEllipse(pal.PlasmaBrush, null, center, plasmaRadius, plasmaRadius);

            double nuclearBreath  = 1.0 + 0.04 * idleBreath;
            double nuclearSubBass = 1.0 + (0.10 * bassScaleMultiplier) * _smoothedSubBass;
            double nuclearRadius  = 13.0 * nuclearBreath * nuclearSubBass;
            dc.DrawEllipse(pal.NuclearBrush, null, center, nuclearRadius, nuclearRadius);

            // =================================================================
            // BIOMORPHIC AI CENTER ("Face of AI" / Digital Polygonal Cyber-Mesh)
            // =================================================================
            RenderBiomorphicFace(dc, center, now, (float)_smoothedSubBass, pal);

            // =================================================================
            // LAYER 2: ORBITAL RINGS & HUD GLYPHS (Concentric Rotating Scales)
            // =================================================================

            // Ring 5 (Coil Bracket Ring, R = 34, angleRing5)
            dc.PushTransform(_rotRing5);
            dc.DrawEllipse(null, pal.PenRing5Coils, center, 34.0, 34.0);
            dc.PushTransform(_transCenter);
            dc.DrawGeometry(pal.PrimaryBrush, null, _ring5CoilsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 4 (Inner Fine Dashed Vernier Scale, R = 44, angleRing4)
            dc.PushTransform(_rotRing4);
            dc.DrawEllipse(null, pal.PenRing4Dashed, center, 44.0, 44.0);
            dc.PushTransform(_transCenter);
            dc.DrawGeometry(null, pal.PenRing4Ticks, _ring4TicksGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 3 (HUD Chevrons & Calibrated Sectors, R = 54, angleRing3)
            dc.PushTransform(_rotRing3);
            dc.PushTransform(_transCenter);
            dc.DrawGeometry(null, pal.PenRing3Sectors, _ring3SectorsGeometry);
            dc.DrawGeometry(pal.PrimaryBrush, null, _ring3ChevronsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 2 (Segmented Dashed Arc Ring, R = 64, angleRing2)
            dc.PushTransform(_rotRing2);
            dc.DrawEllipse(null, pal.PenRing2Dashed, center, 64.0, 64.0);
            dc.PushTransform(_transCenter);
            dc.DrawGeometry(null, pal.PrimaryPen, _ring2AccentsGeometry);
            dc.Pop();
            dc.Pop();

            // Ring 1 (Outer Calibrated Tick Ring, R = 72, angleRing1)
            dc.PushTransform(_rotRing1);
            dc.DrawEllipse(null, pal.PenRing1Track, center, 72.0, 72.0);
            dc.PushTransform(_transCenter);
            dc.DrawGeometry(null, pal.PenRing1Ticks, _ring1TicksGeometry);
            dc.Pop();
            dc.Pop();

            // =================================================================
            // LAYER 1: RADIAL AUDIO SPECTROGRAPH (Procedural Crown of 128 Rays)
            // Batch rendering via StreamGeometry & StreamGeometryContext (0 allocations)
            // =================================================================
            using (var rayCtx = _dynamicRaysGeometry.Open())
            {
                for (int i = 0; i < RayCount; i++)
                {
                    double cos = _cosTable[i];
                    double sin = _sinTable[i];
                    double theta = i * (2.0 * Math.PI / RayCount);

                    // Subtle organic idle wave when silent, smoothly blended with audio
                    double idleWave = 1.2 * Math.Sin(4.0 * theta + 2.5 * now) * Math.Cos(2.0 * theta - 1.8 * now);
                    double audioHeight = _currentSpectrum[i] * MaxRayHeight;
                    double waveBlend = Math.Max(0.0, 1.0 - _currentSpectrum[i] * 3.0);
                    double h = Math.Clamp(2.0 + idleWave * waveBlend + audioHeight, 1.5, MaxRayHeight + 4.0);

                    Point p0 = new Point(cx + BaseRadius * cos, cy + BaseRadius * sin);
                    Point p1 = new Point(cx + (BaseRadius + h) * cos, cy + (BaseRadius + h) * sin);

                    rayCtx.BeginFigure(p0, false, false);
                    rayCtx.LineTo(p1, true, false);
                }
            }
            dc.DrawGeometry(null, pal.RayPen, _dynamicRaysGeometry);

            // Ray tips batched in StreamGeometry
            using (var tipCtx = _dynamicTipsGeometry.Open())
            {
                for (int i = 0; i < RayCount; i++)
                {
                    double cos = _cosTable[i];
                    double sin = _sinTable[i];
                    double theta = i * (2.0 * Math.PI / RayCount);

                    double idleWave = 1.2 * Math.Sin(4.0 * theta + 2.5 * now) * Math.Cos(2.0 * theta - 1.8 * now);
                    double audioHeight = _currentSpectrum[i] * MaxRayHeight;
                    double waveBlend = Math.Max(0.0, 1.0 - _currentSpectrum[i] * 3.0);
                    double h = Math.Clamp(2.0 + idleWave * waveBlend + audioHeight, 1.5, MaxRayHeight + 4.0);

                    Point p1 = new Point(cx + (BaseRadius + h) * cos, cy + (BaseRadius + h) * sin);

                    tipCtx.BeginFigure(new Point(p1.X - 1.0, p1.Y), true, true);
                    tipCtx.ArcTo(new Point(p1.X + 1.0, p1.Y), new Size(1.0, 1.0), 0, false, SweepDirection.Clockwise, true, false);
                    tipCtx.ArcTo(new Point(p1.X - 1.0, p1.Y), new Size(1.0, 1.0), 0, false, SweepDirection.Clockwise, true, false);
                }
            }
            dc.DrawGeometry(StaticTipBrush, null, _dynamicTipsGeometry);

            // Outer containment dotted circle (R = 98)
            dc.DrawEllipse(null, pal.PenContainment, center, BaseRadius + MaxRayHeight, BaseRadius + MaxRayHeight);

            dc.Pop(); // _rootScaleTransform
        }
    }

    /// <summary>
    /// Renders procedural biomorphic AI face & polygonal energy lattice in the reactor core.
    /// Batches wireframe lines through StreamGeometryContext for zero heap allocation.
    /// </summary>
    private void RenderBiomorphicFace(DrawingContext dc, Point center, double now, float subBass, ReactorPalette pal)
    {
        double cx = center.X;
        double cy = center.Y;

        // 1. Natural blink cycle (occurs every 3.6 seconds, 160ms duration)
        double blinkPeriod = 3.6;
        double blinkPhase = now % blinkPeriod;
        double blinkScale = 1.0;
        if (blinkPhase < 0.16)
        {
            double p = blinkPhase / 0.16;
            blinkScale = Math.Max(0.08, 1.0 - Math.Sin(p * Math.PI));
        }

        // 2. Rhythm geometric deformation: vertices smoothly expand with sub-bass
        double deform = 1.0 + 0.12 * subBass;

        // Left Eye Contour
        Point eL1 = new Point(cx - 12.0 * deform, cy - 2.5 * deform);
        Point eL2 = new Point(cx - 7.0 * deform, cy - 5.5 * deform * blinkScale);
        Point eL3 = new Point(cx - 2.5 * deform, cy - 2.5 * deform);
        Point eL4 = new Point(cx - 7.0 * deform, cy - 0.2 * deform * blinkScale);

        // Right Eye Contour
        Point eR1 = new Point(cx + 2.5 * deform, cy - 2.5 * deform);
        Point eR2 = new Point(cx + 7.0 * deform, cy - 5.5 * deform * blinkScale);
        Point eR3 = new Point(cx + 12.0 * deform, cy - 2.5 * deform);
        Point eR4 = new Point(cx + 7.0 * deform, cy - 0.2 * deform * blinkScale);

        // Forehead & Brow Arch
        Point b0 = new Point(cx, cy - 13.0 * deform);
        Point bL = new Point(cx - 8.0 * deform, cy - 9.0 * deform);
        Point bR = new Point(cx + 8.0 * deform, cy - 9.0 * deform);

        // Nose Bridge & Cheekbones
        Point n0 = new Point(cx, cy + 1.5 * deform);
        Point chL = new Point(cx - 14.0 * deform, cy + 5.0 * deform);
        Point chR = new Point(cx + 14.0 * deform, cy + 5.0 * deform);

        // Jawline & Chin
        Point jL = new Point(cx - 8.0 * deform, cy + 12.0 * deform);
        Point jR = new Point(cx + 8.0 * deform, cy + 12.0 * deform);
        Point chin = new Point(cx, cy + 15.5 * deform);

        // Batch thick eye contours into _biomorphicFaceThickGeometry
        using (var ctx = _biomorphicFaceThickGeometry.Open())
        {
            ctx.BeginFigure(eL1, false, false); ctx.LineTo(eL2, true, false); ctx.LineTo(eL3, true, false);
            ctx.BeginFigure(eR1, false, false); ctx.LineTo(eR2, true, false); ctx.LineTo(eR3, true, false);
        }
        dc.DrawGeometry(null, StaticBiomorphicThickPen, _biomorphicFaceThickGeometry);

        // Batch standard lines into _biomorphicFaceGeometry
        using (var ctx = _biomorphicFaceGeometry.Open())
        {
            // Left eye lower
            ctx.BeginFigure(eL3, false, false); ctx.LineTo(eL4, true, false); ctx.LineTo(eL1, true, false);
            // Right eye lower
            ctx.BeginFigure(eR3, false, false); ctx.LineTo(eR4, true, false); ctx.LineTo(eR1, true, false);
            // Forehead
            ctx.BeginFigure(bL, false, false); ctx.LineTo(b0, true, false); ctx.LineTo(bR, true, false);
            ctx.BeginFigure(b0, false, false); ctx.LineTo(n0, true, false);
            // Cheek & Jaw
            ctx.BeginFigure(eL1, false, false); ctx.LineTo(chL, true, false); ctx.LineTo(jL, true, false); ctx.LineTo(chin, true, false);
            ctx.BeginFigure(eR3, false, false); ctx.LineTo(chR, true, false); ctx.LineTo(jR, true, false); ctx.LineTo(chin, true, false);
            ctx.BeginFigure(n0, false, false); ctx.LineTo(chin, true, false);
        }
        dc.DrawGeometry(null, pal.BiomorphicPen, _biomorphicFaceGeometry);

        // Pupil / Retina glints inside eyes
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, new Point(cx - 7.0 * deform, cy - 2.5 * deform), 1.1, 1.1 * blinkScale);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, new Point(cx + 7.0 * deform, cy - 2.5 * deform), 1.1, 1.1 * blinkScale);

        // Digital Neural Lattice Nodes
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, b0, 1.2, 1.2);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, n0, 1.2, 1.2);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chin, 1.4, 1.4);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chL, 1.1, 1.1);
        dc.DrawEllipse(StaticBiomorphicNodeBrush, null, chR, 1.1, 1.1);
    }
}
