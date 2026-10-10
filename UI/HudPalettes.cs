using System;
using System.Windows;
using System.Windows.Media;
using Gem.Core;

namespace Gem.UI;

/// <summary>
/// Pre-computed and frozen visual palette containing all pens and brushes for a state or color.
/// All brushes, pens, and gradients are frozen strictly once at creation to guarantee zero allocations at 60 FPS.
/// </summary>
public sealed class ReactorPalette
{
    public Color Color { get; }
    public Color RayPenColor { get; }
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

    public ReactorPalette(Color color, Color? rayPenColor = null)
    {
        Color = color;
        RayPenColor = rayPenColor ?? Color.FromArgb(230, color.R, color.G, color.B);

        PrimaryBrush = new SolidColorBrush(color);
        PrimaryBrush.Freeze();

        PrimaryPen = new Pen(PrimaryBrush, 1.2);
        PrimaryPen.Freeze();

        RayPen = new Pen(new SolidColorBrush(RayPenColor), 1.4);
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

    public static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            string clean = hex.Trim().TrimStart('#');
            if (clean.Length == 6)
            {
                byte r = Convert.ToByte(clean.Substring(0, 2), 16);
                byte g = Convert.ToByte(clean.Substring(2, 2), 16);
                byte b = Convert.ToByte(clean.Substring(4, 2), 16);
                return Color.FromRgb(r, g, b);
            }
            if (clean.Length == 8)
            {
                byte a = Convert.ToByte(clean.Substring(0, 2), 16);
                byte r = Convert.ToByte(clean.Substring(2, 2), 16);
                byte g = Convert.ToByte(clean.Substring(4, 2), 16);
                byte b = Convert.ToByte(clean.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // fallback
        }
        return fallback;
    }
}

/// <summary>
/// Predefined Sci-Fi holographic color themes for the JARVIS Arc Reactor visualizer.
/// </summary>
public enum HudColorTheme
{
    ArcReactor,
    CyberpunkFlow,
    SolarFlare,
    Custom
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

    public static readonly ReactorPalette[] SpectrumGradientPalettes = PrecomputeSpectrumPalettes();
    public static readonly ReactorPalette[] CyberpunkGradientPalettes = PrecomputeCyberpunkPalettes();
    public static readonly ReactorPalette[] SolarFlareGradientPalettes = PrecomputeSolarFlarePalettes();
    public static ReactorPalette[] CustomGradientPalettes { get; private set; } = PrecomputeCustomPalettes(
        Color.FromRgb(0x00, 0xE5, 0xFF),
        Color.FromRgb(0x00, 0x40, 0xFF),
        Color.FromRgb(0x00, 0xFF, 0xAA)
    );

    public static readonly ReactorPalette[] BeatPalettes = SpectrumGradientPalettes;
    public static readonly ReactorPalette[] OverdriveCrimsonPalettes = PrecomputeOverdriveCrimsonPalettes();

    private static ReactorPalette[] PrecomputeOverdriveCrimsonPalettes()
    {
        Color crimson = Color.FromRgb(0xFF, 0x00, 0x3C);
        Color hotCrimson = Color.FromRgb(0xFF, 0x2A, 0x55);

        var palettes = new ReactorPalette[64];
        for (int i = 0; i < 64; i++)
        {
            float t = i / 63.0f;
            Color c = ColorMath.Lerp(crimson, hotCrimson, t);
            palettes[i] = new ReactorPalette(c, crimson);
        }
        return palettes;
    }

    public static void UpdateCustomPalette(Color core, Color glow, Color rays)
    {
        CustomGradientPalettes = PrecomputeCustomPalettes(core, glow, rays);
    }

    private static ReactorPalette[] PrecomputeCustomPalettes(Color core, Color glow, Color rays)
    {
        var palettes = new ReactorPalette[64];
        for (int i = 0; i < 64; i++)
        {
            float t = i / 63.0f;
            Color c = ColorMath.Lerp(core, glow, t);
            palettes[i] = new ReactorPalette(c, rays);
        }
        return palettes;
    }

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
            HudColorTheme.Custom => CustomGradientPalettes[index],
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
