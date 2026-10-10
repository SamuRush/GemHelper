using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Layer 3: High-voltage plasma arcs (True Neon Arcs).
/// Generates opposing branching lightning discharges:
/// - Scarlet (#FF003C) and Neon Cyan (#00E5FF) in direct opposition.
/// - 2-3 fading lateral forks radiating from each main discharge trunk.
/// Multi-layer rendering:
/// 1. Wide blurred outer neon halo (thickness 13 px, Opacity 0.6).
/// 2. Medium bright saturated arc (thickness 3.5 px).
/// 3. Central dazzling white core (thickness 1.2 px, #FFFFFF).
/// Animated with high-frequency procedural jitter and re-energized on audio kick beats.
/// </summary>
public sealed class NeonLightningGenerator
{
    public sealed class LightningArc
    {
        public bool IsScarlet { get; set; }
        public Point[] BaseNodes { get; set; } = Array.Empty<Point>();
        public Point[][] ForkNodes { get; set; } = Array.Empty<Point[]>();
    }

    public LightningArc[] Arcs { get; private set; } = Array.Empty<LightningArc>();
    public int ArcCount => Arcs.Length;
    public double TotalDuration { get; private set; } = 0.38; // 300-450 ms
    public double CurrentLife { get; private set; } = 0.0;
    public bool IsActive => CurrentLife > 0.0;

    private readonly Random _rand = new();

    private readonly StreamGeometry _scarletHaloGeometry = new();
    private readonly StreamGeometry _cyanHaloGeometry = new();
    private readonly StreamGeometry _scarletArcGeometry = new();
    private readonly StreamGeometry _cyanArcGeometry = new();
    private readonly StreamGeometry _whiteCoreGeometry = new();

    // Layer 1: Wide blurred outer neon halo (thickness 13 px, Opacity 0.60)
    private static readonly Pen StaticScarletHaloPen = InitFrozenPen(Color.FromArgb(153, 255, 0, 60), 13.0);
    private static readonly Pen StaticCyanHaloPen = InitFrozenPen(Color.FromArgb(153, 0, 229, 255), 13.0);

    // Layer 2: Medium bright saturated arc (thickness 3.5 px)
    private static readonly Pen StaticScarletArcPen = InitFrozenPen(Color.FromArgb(245, 255, 0, 60), 3.5);
    private static readonly Pen StaticCyanArcPen = InitFrozenPen(Color.FromArgb(245, 0, 229, 255), 3.5);

    // Layer 3: Central dazzling white core (thickness 1.2 px)
    private static readonly Pen StaticWhiteCorePen = InitFrozenPen(Color.FromArgb(255, 255, 255, 255), 1.2);

    private static Pen InitFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    public void Trigger(Point epicenter)
    {
        TotalDuration = 0.35 + _rand.NextDouble() * 0.08; // 350 - 430 ms
        CurrentLife = TotalDuration;

        int count = 2 + _rand.Next(3); // 2 to 4 arcs
        Arcs = new LightningArc[count];

        for (int i = 0; i < count; i++)
        {
            // Symmetrical opposition: Arc 0 is Scarlet (West), Arc 1 is Cyan (East)
            bool isScarlet = (i % 2 == 0);
            double baseAngle;

            if (i == 0)
            {
                // Scarlet discharge tearing left/west
                baseAngle = Math.PI + (_rand.NextDouble() - 0.5) * 0.55;
            }
            else if (i == 1)
            {
                // Cyan discharge tearing right/east in direct opposition
                baseAngle = (_rand.NextDouble() - 0.5) * 0.55;
            }
            else if (i == 2)
            {
                // Secondary discharge tearing diagonally
                baseAngle = isScarlet ? (-Math.PI * 0.65) : (Math.PI * 0.35);
            }
            else
            {
                baseAngle = isScarlet ? (Math.PI * 0.75) : (-Math.PI * 0.35);
            }

            int nodeCount = 10 + _rand.Next(5);
            var nodes = new Point[nodeCount];
            nodes[0] = epicenter;

            Point curPt = epicenter;
            for (int n = 1; n < nodeCount; n++)
            {
                double segLen = 35.0 + _rand.NextDouble() * 45.0;
                double angleDev = (_rand.NextDouble() - 0.5) * 0.60;
                curPt = new Point(
                    curPt.X + segLen * Math.Cos(baseAngle + angleDev),
                    curPt.Y + segLen * Math.Sin(baseAngle + angleDev));
                nodes[n] = curPt;
            }

            // 2-3 random fading lateral forks per arc
            int forkCount = 2 + _rand.Next(2); // strictly 2 or 3 branches
            var forks = new Point[forkCount][];

            for (int f = 0; f < forkCount; f++)
            {
                int forkStartIdx = 2 + _rand.Next(Math.Max(1, nodeCount - 4));
                Point forkPt = nodes[forkStartIdx];
                int forkSteps = 4 + _rand.Next(3);
                var fNodes = new Point[forkSteps];
                fNodes[0] = forkPt;

                double fAngle = baseAngle + ((f % 2 == 0 ? 1.0 : -1.0) * (0.45 + _rand.NextDouble() * 0.40));
                for (int s = 1; s < forkSteps; s++)
                {
                    double fLen = 22.0 + _rand.NextDouble() * 26.0;
                    double fDev = (_rand.NextDouble() - 0.5) * 0.50;
                    forkPt = new Point(
                        forkPt.X + fLen * Math.Cos(fAngle + fDev),
                        forkPt.Y + fLen * Math.Sin(fAngle + fDev));
                    fNodes[s] = forkPt;
                }
                forks[f] = fNodes;
            }

            Arcs[i] = new LightningArc
            {
                IsScarlet = isScarlet,
                BaseNodes = nodes,
                ForkNodes = forks
            };
        }
    }

    public void PulseOnBeat(float beatIntensity, Point epicenter)
    {
        if (beatIntensity > 0.38f)
        {
            if (Arcs.Length == 0 || CurrentLife <= 0.05)
            {
                Trigger(epicenter);
            }
            else
            {
                double reEnergized = TotalDuration * Math.Clamp(0.55 + beatIntensity * 0.45, 0.5, 1.0);
                CurrentLife = Math.Max(CurrentLife, reEnergized);
            }
        }
    }

    public void Update(double dt)
    {
        if (CurrentLife > 0.0)
        {
            CurrentLife = Math.Max(0.0, CurrentLife - dt);
        }
    }

    public void Render(DrawingContext dc, double dt, Point center)
    {
        if (CurrentLife <= 0.0 || Arcs.Length == 0) return;

        double opacity = Math.Clamp(CurrentLife / TotalDuration, 0.0, 1.0);
        double flicker = 0.84 + 0.16 * Math.Sin(CurrentLife * 70.0);
        double finalAlpha = opacity * flicker;

        dc.PushOpacity(finalAlpha);

        using (var sHalo = _scarletHaloGeometry.Open())
        using (var cHalo = _cyanHaloGeometry.Open())
        using (var sArc = _scarletArcGeometry.Open())
        using (var cArc = _cyanArcGeometry.Open())
        using (var wCore = _whiteCoreGeometry.Open())
        {
            for (int i = 0; i < Arcs.Length; i++)
            {
                var arc = Arcs[i];
                var haloCtx = arc.IsScarlet ? sHalo : cHalo;
                var arcCtx = arc.IsScarlet ? sArc : cArc;

                // Main trunk
                RenderJitteredTrunk(haloCtx, arcCtx, wCore, arc.BaseNodes);

                // 2-3 lateral forks
                for (int f = 0; f < arc.ForkNodes.Length; f++)
                {
                    RenderJitteredFork(haloCtx, arcCtx, wCore, arc.ForkNodes[f]);
                }
            }
        }

        // Layer 1: Wide blurred outer neon halo (13 px, 0.6 opacity)
        dc.DrawGeometry(null, StaticScarletHaloPen, _scarletHaloGeometry);
        dc.DrawGeometry(null, StaticCyanHaloPen, _cyanHaloGeometry);

        // Layer 2: Medium bright saturated arc (3.5 px)
        dc.DrawGeometry(null, StaticScarletArcPen, _scarletArcGeometry);
        dc.DrawGeometry(null, StaticCyanArcPen, _cyanArcGeometry);

        // Layer 3: Central dazzling white core (1.2 px, #FFFFFF)
        dc.DrawGeometry(null, StaticWhiteCorePen, _whiteCoreGeometry);

        dc.Pop();
    }

    private void RenderJitteredTrunk(
        StreamGeometryContext haloCtx,
        StreamGeometryContext arcCtx,
        StreamGeometryContext coreCtx,
        Point[] nodes)
    {
        if (nodes.Length < 2) return;

        Point p0 = nodes[0];
        haloCtx.BeginFigure(p0, false, false);
        arcCtx.BeginFigure(p0, false, false);
        coreCtx.BeginFigure(p0, false, false);

        for (int i = 1; i < nodes.Length; i++)
        {
            double jx = (_rand.NextDouble() - 0.5) * 8.0;
            double jy = (_rand.NextDouble() - 0.5) * 8.0;

            Point jPt = new Point(nodes[i].X + jx, nodes[i].Y + jy);
            haloCtx.LineTo(jPt, true, false);
            arcCtx.LineTo(jPt, true, false);
            coreCtx.LineTo(jPt, true, false);
        }
    }

    private void RenderJitteredFork(
        StreamGeometryContext haloCtx,
        StreamGeometryContext arcCtx,
        StreamGeometryContext coreCtx,
        Point[] nodes)
    {
        if (nodes.Length < 2) return;

        Point p0 = nodes[0];
        haloCtx.BeginFigure(p0, false, false);
        arcCtx.BeginFigure(p0, false, false);
        coreCtx.BeginFigure(p0, false, false);

        for (int i = 1; i < nodes.Length; i++)
        {
            double jx = (_rand.NextDouble() - 0.5) * 6.5;
            double jy = (_rand.NextDouble() - 0.5) * 6.5;

            Point jPt = new Point(nodes[i].X + jx, nodes[i].Y + jy);
            haloCtx.LineTo(jPt, true, false);
            arcCtx.LineTo(jPt, true, false);
            coreCtx.LineTo(jPt, true, false);
        }
    }
}
