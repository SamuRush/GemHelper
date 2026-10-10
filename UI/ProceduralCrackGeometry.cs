using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Gem.UI;

/// <summary>
/// Layer 2: Volumetric crack facets (Volumetric Facets).
/// Generates continuous dual-contour fracture bevels: bright specular highlight (#FFFFFF, 1.5 px)
/// and deep shadow (#05050A, 2.5 px) simulating 6-8 mm thick cut glass,
/// along with concentric impact rings forming distinct polygonal glass plates.
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

        int rayCount = 7 + _rand.Next(2); // 7 to 8 main radial cracks
        var rayNodeLists = new List<List<Point>>(rayCount);

        var highlightGeo = new StreamGeometry();
        var shadowGeo = new StreamGeometry();
        var platesGeo = new StreamGeometry();
        var clipGeo = new StreamGeometry();

        using var hCtx = highlightGeo.Open();
        using var sCtx = shadowGeo.Open();
        using var pCtx = platesGeo.Open();
        using var cCtx = clipGeo.Open();

        // 1. Generate main radial rays as continuous polylines
        for (int r = 0; r < rayCount; r++)
        {
            var nodes = new List<Point>(48);
            double baseAngle = r * (2.0 * Math.PI / rayCount) + (_rand.NextDouble() - 0.5) * 0.25;
            Point curPt = new Point(
                epicenter.X + 18.0 * Math.Cos(baseAngle),
                epicenter.Y + 18.0 * Math.Sin(baseAngle));

            nodes.Add(curPt);
            double travelDist = 18.0;

            while (travelDist < maxRadius)
            {
                double segLen = 22.0 + _rand.NextDouble() * 22.0;
                double angleDev = (_rand.NextDouble() - 0.5) * (32.0 * Math.PI / 180.0);
                double segAngle = baseAngle + angleDev;

                Point nextPt = new Point(
                    curPt.X + segLen * Math.Cos(segAngle),
                    curPt.Y + segLen * Math.Sin(segAngle));

                nodes.Add(nextPt);
                curPt = nextPt;
                travelDist += segLen;
            }

            rayNodeLists.Add(nodes);

            // Add continuous dual-contour polyline for the ray
            AddContinuousDualContourPolyline(hCtx, sCtx, nodes, GlassThicknessOffset);

            // 2. Generate secondary branching cracks
            int branchCount = 2 + _rand.Next(2);
            for (int b = 0; b < branchCount; b++)
            {
                if (nodes.Count < 6) continue;
                int startIdx = 2 + _rand.Next(nodes.Count - 4);
                Point branchPt = nodes[startIdx];

                double branchAngle = baseAngle + ((_rand.Next(2) == 0 ? 1.0 : -1.0) * (0.55 + _rand.NextDouble() * 0.35));
                int steps = 4 + _rand.Next(3);
                var branchNodes = new List<Point>(steps + 1) { branchPt };

                for (int s = 0; s < steps; s++)
                {
                    double bLen = 22.0 + _rand.NextDouble() * 20.0;
                    double bDev = (_rand.NextDouble() - 0.5) * 0.28;
                    branchPt = new Point(
                        branchPt.X + bLen * Math.Cos(branchAngle + bDev),
                        branchPt.Y + bLen * Math.Sin(branchAngle + bDev));
                    branchNodes.Add(branchPt);
                }

                AddContinuousDualContourPolyline(hCtx, sCtx, branchNodes, GlassThicknessOffset * 0.85);
            }
        }

        // 3. Concentric impact rings & closed polygonal glass plates (Shards)
        int ringCount = 3; // 3 concentric rings (creates ~21-24 polygonal plates)
        int plateCount = 0;

        for (int ring = 1; ring <= ringCount; ring++)
        {
            int nodeIdx = Math.Min(ring * 5, 24);
            int prevNodeIdx = Math.Max(1, (ring - 1) * 5);

            for (int r = 0; r < rayCount; r++)
            {
                var rayA = rayNodeLists[r];
                var rayB = rayNodeLists[(r + 1) % rayCount];

                if (nodeIdx >= rayA.Count || nodeIdx >= rayB.Count) continue;

                Point pA = rayA[nodeIdx];
                Point pB = rayB[nodeIdx];

                Point mid = new Point(
                    (pA.X + pB.X) * 0.5 + (_rand.NextDouble() - 0.5) * 10.0,
                    (pA.Y + pB.Y) * 0.5 + (_rand.NextDouble() - 0.5) * 10.0);

                var arcNodes = new List<Point>(3) { pA, mid, pB };
                AddContinuousDualContourPolyline(hCtx, sCtx, arcNodes, GlassThicknessOffset * 0.90);

                // Closed polygonal shard plate between concentric ring levels
                if (prevNodeIdx < rayA.Count && prevNodeIdx < rayB.Count)
                {
                    Point pA0 = rayA[prevNodeIdx];
                    Point pB0 = rayB[prevNodeIdx];

                    pCtx.BeginFigure(pA0, true, true);
                    pCtx.LineTo(pA, true, false);
                    pCtx.LineTo(mid, true, false);
                    pCtx.LineTo(pB, true, false);
                    pCtx.LineTo(pB0, true, false);

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

    private static void AddContinuousDualContourPolyline(
        StreamGeometryContext hCtx,
        StreamGeometryContext sCtx,
        IReadOnlyList<Point> nodes,
        double offset)
    {
        if (nodes.Count < 2) return;

        double halfOff = offset * 0.5;

        // Calculate vertex normals
        Point p0 = nodes[0];
        Point p1 = nodes[1];
        double dx0 = p1.X - p0.X;
        double dy0 = p1.Y - p0.Y;
        double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
        if (len0 < 0.001) len0 = 1.0;

        double nx0 = -dy0 / len0;
        double ny0 = dx0 / len0;

        Point hStart = new Point(p0.X + nx0 * halfOff, p0.Y + ny0 * halfOff);
        Point sStart = new Point(p0.X - nx0 * halfOff, p0.Y - ny0 * halfOff);

        hCtx.BeginFigure(hStart, false, false);
        sCtx.BeginFigure(sStart, false, false);

        for (int i = 1; i < nodes.Count; i++)
        {
            Point prev = nodes[i - 1];
            Point curr = nodes[i];
            double dx = curr.X - prev.X;
            double dy = curr.Y - prev.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) len = 1.0;

            double nx = -dy / len;
            double ny = dx / len;

            // Smooth normal if interior vertex
            if (i < nodes.Count - 1)
            {
                Point next = nodes[i + 1];
                double dxNext = next.X - curr.X;
                double dyNext = next.Y - curr.Y;
                double lenNext = Math.Sqrt(dxNext * dxNext + dyNext * dyNext);
                if (lenNext > 0.001)
                {
                    double nxNext = -dyNext / lenNext;
                    double nyNext = dxNext / lenNext;
                    nx = (nx + nxNext) * 0.5;
                    ny = (ny + nyNext) * 0.5;
                    double nLen = Math.Sqrt(nx * nx + ny * ny);
                    if (nLen > 0.001) { nx /= nLen; ny /= nLen; }
                }
            }

            Point hPt = new Point(curr.X + nx * halfOff, curr.Y + ny * halfOff);
            Point sPt = new Point(curr.X - nx * halfOff, curr.Y - ny * halfOff);

            hCtx.LineTo(hPt, true, false);
            sCtx.LineTo(sPt, true, false);
        }
    }
}
