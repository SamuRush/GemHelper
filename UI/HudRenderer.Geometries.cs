using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

public sealed partial class HudRenderer
{
    // Static frozen resources shared across all instances
    private static readonly SolidColorBrush StaticWhiteBrush;
    private static readonly SolidColorBrush StaticTipBrush;
    private static readonly Pen StaticCoreGlyphPen;
    private static readonly Pen StaticBiomorphicThickPen;
    private static readonly SolidColorBrush StaticBiomorphicNodeBrush;

    private static readonly Pen StaticOverloadGaugeTrackPen;
    private static readonly Pen StaticOverloadGaugeHotPen;
    private static readonly RadialGradientBrush StaticMoltenRiftBrush;
    private static readonly Pen StaticCrackPen;
    private static readonly Pen StaticSparkPen;
    private static readonly RadialGradientBrush StaticSparkHaloBrush;
    private static readonly RadialGradientBrush StaticCoreWhiteoutBrush;
    private static readonly Geometry StaticOverrideTextGeometry;
    private static readonly SolidColorBrush StaticCyanGlitchBrush;
    private static readonly SolidColorBrush StaticCrimsonGlitchBrush;
    private static readonly Pen[] StaticShockwavePens = PrecomputeShockwavePens();

    private static Pen[] PrecomputeShockwavePens()
    {
        var pens = new Pen[32];
        for (int i = 0; i < 32; i++)
        {
            byte alpha = (byte)Math.Clamp((int)(i / 31.0 * 255.0), 0, 255);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0xE5, 0xFF)), 3.0);
            pen.Freeze();
            pens[i] = pen;
        }
        return pens;
    }

    static HudRenderer()
    {
        StaticWhiteBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        StaticWhiteBrush.Freeze();

        StaticTipBrush = StaticWhiteBrush;

        StaticCoreGlyphPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0);
        StaticCoreGlyphPen.Freeze();

        StaticBiomorphicThickPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1.5);
        StaticBiomorphicThickPen.Freeze();

        StaticBiomorphicNodeBrush = StaticWhiteBrush;

        StaticOverloadGaugeTrackPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1.4)
        {
            DashStyle = new DashStyle(new double[] { 1, 3 }, 0)
        };
        StaticOverloadGaugeTrackPen.Freeze();

        StaticOverloadGaugeHotPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x3C)), 2.4);
        StaticOverloadGaugeHotPen.Freeze();

        StaticMoltenRiftBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        StaticMoltenRiftBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        StaticMoltenRiftBrush.GradientStops.Add(new GradientStop(Color.FromArgb(230, 220, 255, 255), 0.35));
        StaticMoltenRiftBrush.GradientStops.Add(new GradientStop(Color.FromArgb(170, 255, 0, 60), 0.70));
        StaticMoltenRiftBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 0, 60), 1.0));
        StaticMoltenRiftBrush.Freeze();

        StaticCrackPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1.8);
        StaticCrackPen.Freeze();

        StaticSparkPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), 2.2);
        StaticSparkPen.Freeze();

        StaticSparkHaloBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        StaticSparkHaloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 255, 255, 255), 0.0));
        StaticSparkHaloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 229, 255), 0.5));
        StaticSparkHaloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 229, 255), 1.0));
        StaticSparkHaloBrush.Freeze();

        StaticCoreWhiteoutBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        StaticCoreWhiteoutBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
        StaticCoreWhiteoutBrush.GradientStops.Add(new GradientStop(Color.FromArgb(250, 255, 255, 255), 0.45));
        StaticCoreWhiteoutBrush.GradientStops.Add(new GradientStop(Color.FromArgb(190, 225, 250, 255), 0.75));
        StaticCoreWhiteoutBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 200, 240, 255), 1.0));
        StaticCoreWhiteoutBrush.Freeze();

        StaticCyanGlitchBrush = new SolidColorBrush(Color.FromArgb(210, 0, 229, 255));
        StaticCyanGlitchBrush.Freeze();

        StaticCrimsonGlitchBrush = new SolidColorBrush(Color.FromArgb(210, 255, 0, 60));
        StaticCrimsonGlitchBrush.Freeze();

        var typeface = new Typeface(new FontFamily("Consolas, Courier New, monospace"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var formattedText = new FormattedText(
            "OVERRIDE!",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            18.0,
            Brushes.White,
            1.0
        );
        StaticOverrideTextGeometry = formattedText.BuildGeometry(new Point(-formattedText.Width / 2.0, -formattedText.Height / 2.0));
        StaticOverrideTextGeometry.Freeze();
    }

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

        _ring3SectorsGeometry = new StreamGeometry();
        using (var ctx = _ring3SectorsGeometry.Open())
        {
            for (int s = 0; s < 4; s++)
            {
                double startDeg = s * 90.0;
                double endRad = (startDeg + 42.0) * Math.PI / 180.0;
                double startRad = startDeg * Math.PI / 180.0;

                Point pStart = new Point(54.0 * Math.Cos(startRad), 54.0 * Math.Sin(startRad));
                Point pEnd = new Point(54.0 * Math.Cos(endRad), 54.0 * Math.Sin(endRad));

                ctx.BeginFigure(pStart, false, false);
                ctx.ArcTo(pEnd, new Size(54.0, 54.0), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        _ring3SectorsGeometry.Freeze();

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
}
