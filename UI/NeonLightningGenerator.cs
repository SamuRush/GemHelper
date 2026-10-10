using System;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Layer 3: High-voltage plasma arcs (Neon Lightning Arcs).
/// Generates 2-4 branching lightning arcs radiating from the split core epicenter:
/// Scarlet (#FF003C) and Neon Cyan (#00E5FF) with dazzling white core.
/// Vertices are procedurally jittered each frame and smoothly decay over 300-450 ms.
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

    private static readonly Pen StaticScarletGlowPen = InitFrozenPen(Color.FromArgb(230, 255, 0, 60), 6.0);
    private static readonly Pen StaticCyanGlowPen = InitFrozenPen(Color.FromArgb(230, 0, 229, 255), 6.0);
    private static readonly Pen StaticWhiteCorePen = InitFrozenPen(Color.FromArgb(255, 255, 255, 255), 1.8);

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
        double flicker = 0.80 + 0.20 * Math.Sin(CurrentLife * 60.0);
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

        // Draw outer glow and white core
        dc.DrawGeometry(null, StaticScarletGlowPen, _scarletGlowGeometry);
        dc.DrawGeometry(null, StaticCyanGlowPen, _cyanGlowGeometry);
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
            // High-frequency procedural jitter: +-3.5 to +-6.5 px
            double jitterX = (_rand.NextDouble() - 0.5) * 8.0;
            double jitterY = (_rand.NextDouble() - 0.5) * 8.0;

            Point jitteredPt = new Point(nodes[i].X + jitterX, nodes[i].Y + jitterY);
            glowCtx.LineTo(jitteredPt, true, false);
            coreCtx.LineTo(jitteredPt, true, false);
        }
    }
}
