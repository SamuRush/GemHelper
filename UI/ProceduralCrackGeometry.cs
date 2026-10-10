using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Layer 2: Volumetric crack facets (Volumetric Facets).
/// Generates dual-contour fracture bevels: bright specular highlight (#FFFFFF, 1.5 px)
/// and deep shadow (#05050A, 2.5 px) simulating 6-8 mm thick cut glass,
/// along with concentric rings forming distinct polygonal glass plates.
/// All geometries are frozen immediately for 0 GC overhead in 60 FPS rendering.
/// </summary>
public sealed class ProceduralCrackGeometry
{
    public const double SpecularHighlightThickness = 1.5;
    public const double DeepShadowThickness = 2.5;
    public const double GlassThicknessOffset = 2.4; // 6-8 mm glass bevel offset

    public StreamGeometry? HighlightGeometry { get; private set; }
    public StreamGeometry? ShadowGeometry { get; private set; }
    public StreamGeometry? PolygonalShardsGeometry { get; private set; }
    public StreamGeometry? CombinedClipGeometry { get; private set; }
    public int PolygonalPlateCount { get; private set; } = 0;
    public bool HasGeometry => HighlightGeometry != null && ShadowGeometry != null;

    private readonly Random _rand = new();

    public void Generate(Point epicenter, double boundsW, double boundsH)
    {
        double w = boundsW > 0 ? boundsW : 1920.0;
        double h = boundsH > 0 ? boundsH : 1080.0;
        double maxDx = Math.Max(epicenter.X, w - epicenter.X);
        double maxDy = Math.Max(epicenter.Y, h - epicenter.Y);
        double maxRadius = Math.Sqrt(maxDx * maxDx + maxDy * maxDy) + 120.0;

        int rayCount = 6 + _rand.Next(3); // 6 to 8 main radial cracks
        var rayNodeLists = new List<List<Point>>(rayCount);

        var highlightGeo = new StreamGeometry();
        var shadowGeo = new StreamGeometry();
        var platesGeo = new StreamGeometry();
        var clipGeo = new StreamGeometry();

        using var hCtx = highlightGeo.Open();
        using var sCtx = shadowGeo.Open();
        using var pCtx = platesGeo.Open();
        using var cCtx = clipGeo.Open();

        // 1. Generate main radial rays
        for (int r = 0; r < rayCount; r++)
        {
            var nodes = new List<Point>(45);
            double baseAngle = r * (2.0 * Math.PI / rayCount) + (_rand.NextDouble() - 0.5) * 0.30;
            Point curPt = new Point(
                epicenter.X + 20.0 * Math.Cos(baseAngle),
                epicenter.Y + 20.0 * Math.Sin(baseAngle));

            nodes.Add(curPt);
            double travelDist = 20.0;

            while (travelDist < maxRadius)
            {
                double segLen = 22.0 + _rand.NextDouble() * 20.0;
                double angleDev = (_rand.NextDouble() - 0.5) * (34.0 * Math.PI / 180.0);
                double segAngle = baseAngle + angleDev;

                Point nextPt = new Point(
                    curPt.X + segLen * Math.Cos(segAngle),
                    curPt.Y + segLen * Math.Sin(segAngle));

                // Add dual-contour bevel segment
                AddDualContourSegment(hCtx, sCtx, curPt, nextPt, GlassThicknessOffset);
                nodes.Add(nextPt);

                curPt = nextPt;
                travelDist += segLen;
            }

            rayNodeLists.Add(nodes);

            // 2. Generate secondary branching cracks
            int branchCount = 2 + _rand.Next(2);
            for (int b = 0; b < branchCount; b++)
            {
                if (nodes.Count < 6) continue;
                int startIdx = 2 + _rand.Next(nodes.Count - 4);
                Point branchPt = nodes[startIdx];

                double branchAngle = baseAngle + ((_rand.Next(2) == 0 ? 1.0 : -1.0) * (0.60 + _rand.NextDouble() * 0.35));
                int steps = 3 + _rand.Next(3);

                for (int s = 0; s < steps; s++)
                {
                    double bLen = 20.0 + _rand.NextDouble() * 18.0;
                    double bDev = (_rand.NextDouble() - 0.5) * 0.30;
                    Point nextBranchPt = new Point(
                        branchPt.X + bLen * Math.Cos(branchAngle + bDev),
                        branchPt.Y + bLen * Math.Sin(branchAngle + bDev));

                    AddDualContourSegment(hCtx, sCtx, branchPt, nextBranchPt, GlassThicknessOffset * 0.85);
                    branchPt = nextBranchPt;
                }
            }
        }

        // 3. Concentric spiderweb rings & polygonal glass plates
        int ringCount = 3 + _rand.Next(2); // 3 to 4 concentric rings
        int plateCount = 0;

        for (int ring = 1; ring <= ringCount; ring++)
        {
            int nodeIdx = Math.Min(ring * 5, 25);
            int prevNodeIdx = Math.Max(1, (ring - 1) * 5);

            for (int r = 0; r < rayCount; r++)
            {
                var rayA = rayNodeLists[r];
                var rayB = rayNodeLists[(r + 1) % rayCount];

                if (nodeIdx >= rayA.Count || nodeIdx >= rayB.Count) continue;

                Point pA = rayA[nodeIdx];
                Point pB = rayB[nodeIdx];

                // Transverse bar dual-contour
                Point mid = new Point(
                    (pA.X + pB.X) * 0.5 + (_rand.NextDouble() - 0.5) * 12.0,
                    (pA.Y + pB.Y) * 0.5 + (_rand.NextDouble() - 0.5) * 12.0);

                AddDualContourSegment(hCtx, sCtx, pA, mid, GlassThicknessOffset * 0.9);
                AddDualContourSegment(hCtx, sCtx, mid, pB, GlassThicknessOffset * 0.9);

                // Build closed polygonal shard plate between ring levels
                if (prevNodeIdx < rayA.Count && prevNodeIdx < rayB.Count)
                {
                    Point pA0 = rayA[prevNodeIdx];
                    Point pB0 = rayB[prevNodeIdx];

                    pCtx.BeginFigure(pA0, true, true);
                    pCtx.LineTo(pA, true, false);
                    pCtx.LineTo(mid, true, false);
                    pCtx.LineTo(pB, true, false);
                    pCtx.LineTo(pB0, true, false);

                    // Combined clip figure for refraction layer
                    cCtx.BeginFigure(pA0, true, true);
                    cCtx.LineTo(pA, true, false);
                    cCtx.LineTo(mid, true, false);
                    cCtx.LineTo(pB, true, false);
                    cCtx.LineTo(pB0, true, false);

                    plateCount++;
                }
            }
        }

        PolygonalPlateCount = plateCount;

        highlightGeo.Freeze();
        shadowGeo.Freeze();
        platesGeo.Freeze();
        clipGeo.Freeze();

        HighlightGeometry = highlightGeo;
        ShadowGeometry = shadowGeo;
        PolygonalShardsGeometry = platesGeo;
        CombinedClipGeometry = clipGeo;
    }

    private static void AddDualContourSegment(
        StreamGeometryContext hCtx,
        StreamGeometryContext sCtx,
        Point p0,
        Point p1,
        double offset)
    {
        double dx = p1.X - p0.X;
        double dy = p1.Y - p0.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return;

        // Normal perpendicular to segment
        double nx = -dy / len;
        double ny = dx / len;
        double halfOff = offset * 0.5;

        // Specular highlight line (+Normal offset)
        Point h0 = new Point(p0.X + nx * halfOff, p0.Y + ny * halfOff);
        Point h1 = new Point(p1.X + nx * halfOff, p1.Y + ny * halfOff);
        hCtx.BeginFigure(h0, false, false);
        hCtx.LineTo(h1, true, false);

        // Deep shadow line (-Normal offset)
        Point s0 = new Point(p0.X - nx * halfOff, p0.Y - ny * halfOff);
        Point s1 = new Point(p1.X - nx * halfOff, p1.Y - ny * halfOff);
        sCtx.BeginFigure(s0, false, false);
        sCtx.LineTo(s1, true, false);
    }
}
