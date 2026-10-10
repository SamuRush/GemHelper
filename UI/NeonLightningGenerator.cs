using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Layer 3: High-voltage plasma arcs (Neon Lightning Arcs).
/// Generates 2-4 branching lightning arcs radiating from the split core epicenter:
/// Scarlet (#FF003C) and Neon Cyan (#00E5FF) with dazzling white core.
/// Features 3-pass HDR bloom rendering:
/// - 24 px wide soft bloom aura (#5000E5FF / #50FF003C)
/// - 9 px intense neon glow (#F000E5FF / #F0FF003C)
/// - 2.5 px blinding white core (#FFFFFF)
/// Vertices are procedurally jittered each frame and re-energized by audio kick drum beats (PulseOnBeat).
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

    private readonly StreamGeometry _scarletGlowGeometry = new();
    private readonly StreamGeometry _cyanGlowGeometry = new();
    private readonly StreamGeometry _whiteCoreGeometry = new();

    // 3-Pass HDR Bloom Pens
    // Pass 1: Wide bloom aura (24 px)
    private static readonly Pen StaticScarletAuraPen = InitFrozenPen(Color.FromArgb(80, 255, 0, 60), 24.0);
    private static readonly Pen StaticCyanAuraPen = InitFrozenPen(Color.FromArgb(80, 0, 229, 255), 24.0);

    // Pass 2: Intense neon glow (9 px)
    private static readonly Pen StaticScarletNeonPen = InitFrozenPen(Color.FromArgb(240, 255, 0, 60), 9.0);
    private static readonly Pen StaticCyanNeonPen = InitFrozenPen(Color.FromArgb(240, 0, 229, 255), 9.0);

    // Pass 3: Blinding white core (2.5 px)
    private static readonly Pen StaticWhiteCorePen = InitFrozenPen(Color.FromArgb(255, 255, 255, 255), 2.5);

    private static Pen InitFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void Trigger(Point epicenter)
    {
        TotalDuration = 0.35 + _rand.NextDouble() * 0.08; // 350 - 430 ms
        CurrentLife = TotalDuration;

        int count = 2 + _rand.Next(3); // 2 to 4 powerful lightning arcs
        Arcs = new LightningArc[count];

        for (int i = 0; i < count; i++)
        {
            bool isScarlet = (i % 2 == 0); // Alternate scarlet and cyan
            // Scarlet arcs blast left/west (angles around PI), Cyan arcs blast right/east (angles around 0)
            double baseAngle = isScarlet
                ? (Math.PI + (_rand.NextDouble() - 0.5) * 0.70)
                : ((_rand.NextDouble() - 0.5) * 0.70);

            int nodeCount = 10 + _rand.Next(6);
            var nodes = new Point[nodeCount];
            nodes[0] = epicenter;

            Point curPt = epicenter;

            for (int n = 1; n < nodeCount; n++)
            {
                double segLen = 35.0 + _rand.NextDouble() * 45.0;
                double angleDev = (_rand.NextDouble() - 0.5) * 0.65;
                curPt = new Point(
                    curPt.X + segLen * Math.Cos(baseAngle + angleDev),
                    curPt.Y + segLen * Math.Sin(baseAngle + angleDev));
                nodes[n] = curPt;
            }

            // Generate 1-2 secondary forks per arc
            int forkCount = 1 + _rand.Next(2);
            var forks = new Point[forkCount][];

            for (int f = 0; f < forkCount; f++)
            {
                int forkStartIdx = 2 + _rand.Next(nodeCount - 4);
                Point forkPt = nodes[forkStartIdx];
                int forkSteps = 4 + _rand.Next(3);
                var fNodes = new Point[forkSteps];
                fNodes[0] = forkPt;

                double fAngle = baseAngle + ((f % 2 == 0 ? 1.0 : -1.0) * (0.50 + _rand.NextDouble() * 0.40));
                for (int s = 1; s < forkSteps; s++)
                {
                    double fLen = 25.0 + _rand.NextDouble() * 30.0;
                    double fDev = (_rand.NextDouble() - 0.5) * 0.55;
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

    /// <summary>
    /// Re-energizes or generates new plasma arcs when audio kick drops occur during Overdrive.
    /// </summary>
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
                // Refresh energy life based on kick impulse
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
        // Flickering plasma noise
        double flicker = 0.82 + 0.18 * Math.Sin(CurrentLife * 65.0);
        double finalAlpha = opacity * flicker;

        dc.PushOpacity(finalAlpha);

        using (var sCtx = _scarletGlowGeometry.Open())
        using (var cCtx = _cyanGlowGeometry.Open())
        using (var wCtx = _whiteCoreGeometry.Open())
        {
            for (int i = 0; i < Arcs.Length; i++)
            {
                var arc = Arcs[i];
                var glowCtx = arc.IsScarlet ? sCtx : cCtx;

                // Main lightning branch with procedural noise jitter
                RenderJitteredPath(glowCtx, wCtx, arc.BaseNodes);

                // Fork sub-branches
                for (int f = 0; f < arc.ForkNodes.Length; f++)
                {
                    RenderJitteredPath(glowCtx, wCtx, arc.ForkNodes[f]);
                }
            }
        }

        // Pass 1: Wide bloom aura (24 px)
        dc.DrawGeometry(null, StaticScarletAuraPen, _scarletGlowGeometry);
        dc.DrawGeometry(null, StaticCyanAuraPen, _cyanGlowGeometry);

        // Pass 2: Intense neon glow (9 px)
        dc.DrawGeometry(null, StaticScarletNeonPen, _scarletGlowGeometry);
        dc.DrawGeometry(null, StaticCyanNeonPen, _cyanGlowGeometry);

        // Pass 3: Dazzling white core (2.5 px)
        dc.DrawGeometry(null, StaticWhiteCorePen, _whiteCoreGeometry);

        dc.Pop();
    }

    private void RenderJitteredPath(
        StreamGeometryContext glowCtx,
        StreamGeometryContext coreCtx,
        Point[] nodes)
    {
        if (nodes.Length < 2) return;

        Point p0 = nodes[0];
        glowCtx.BeginFigure(p0, false, false);
        coreCtx.BeginFigure(p0, false, false);

        for (int i = 1; i < nodes.Length; i++)
        {
            // High-frequency procedural jitter: +-4.0 to +-7.5 px
            double jitterX = (_rand.NextDouble() - 0.5) * 8.5;
            double jitterY = (_rand.NextDouble() - 0.5) * 8.5;

            Point jitteredPt = new Point(nodes[i].X + jitterX, nodes[i].Y + jitterY);
            glowCtx.LineTo(jitteredPt, true, false);
            coreCtx.LineTo(jitteredPt, true, false);
        }
    }
}
