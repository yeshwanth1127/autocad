using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Placement
{
    public static class BoundaryCoverageService
    {
        /// <summary>Prevents pathological runtimes when offset/spacing are tiny in drawing units.</summary>
        private const int MaxSamplesPerEdge = 400;

        public static List<Point2d> EnsureBoundaryCoverage(
            List<Point2d> points,
            List<Point2d> ring,
            List<Point2d> gridPoints,
            double spacing,
            double maxDistToBoundary,
            double originX,
            double originY)
        {
            var result = new List<Point2d>(points ?? new List<Point2d>());
            if (ring == null || ring.Count < 3 || gridPoints == null || gridPoints.Count == 0)
                return result;

            AgentLog.Write("BoundaryCoverage",
                "enter ringN=" + ring.Count.ToString(CultureInfo.InvariantCulture) +
                " gridN=" + gridPoints.Count.ToString(CultureInfo.InvariantCulture) +
                " spacing=" + spacing.ToString("G6", CultureInfo.InvariantCulture) +
                " maxDist=" + maxDistToBoundary.ToString("G6", CultureInfo.InvariantCulture));

            // Every correction point MUST be a grid intersection; do not create free points.
            // originX/originY are kept for clarity/debugging: gridPoints are already aligned to this origin.
            double eps = Math.Max(1.0, spacing * 1e-4);
            double dedupeEps = spacing * 0.5;

            // Step 2 — boundary-near grid points (chosen FROM the grid)
            var boundaryCandidates = new List<Point2d>();
            for (int gi = 0; gi < gridPoints.Count; gi++)
            {
                var g = gridPoints[gi];
                if (DistanceToPolygonEdges(g, ring) <= maxDistToBoundary)
                    boundaryCandidates.Add(g);
            }
            for (int i = 0; i < boundaryCandidates.Count; i++)
            {
                if (!PolygonUtils.ContainsPoint(result, boundaryCandidates[i], dedupeEps))
                    result.Add(boundaryCandidates[i]);
            }

            AgentLog.Write("BoundaryCoverage", "after boundaryCandidates resultN=" + result.Count.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];

                double edgeLength = a.GetDistanceTo(b);
                // Never let sample count explode (e.g. maxDist tiny due to bad scale): cap per edge.
                double stepDist = Math.Max(maxDistToBoundary, spacing * 0.15);
                int samples = Math.Max(1, (int)Math.Ceiling(edgeLength / stepDist));
                if (samples > MaxSamplesPerEdge)
                    samples = MaxSamplesPerEdge;

                AgentLog.Write("BoundaryCoverage", "edge " + i.ToString(CultureInfo.InvariantCulture) + "/" + ring.Count.ToString(CultureInfo.InvariantCulture) + " len=" + edgeLength.ToString("G6", CultureInfo.InvariantCulture) + " samples=" + samples.ToString(CultureInfo.InvariantCulture));

                for (int s = 0; s <= samples; s++)
                {
                    if ((s & 127) == 0 && s > 0)
                        AgentLog.Write("BoundaryCoverage", "edge " + i.ToString(CultureInfo.InvariantCulture) + " sample " + s.ToString(CultureInfo.InvariantCulture) + "/" + samples.ToString(CultureInfo.InvariantCulture) + " resultN=" + result.Count.ToString(CultureInfo.InvariantCulture));

                    double t = (double)s / samples;

                    var sample = new Point2d(
                        a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t);

                    double minDist = double.MaxValue;
                    for (int pi = 0; pi < result.Count; pi++)
                    {
                        // FIX: coverage must use perpendicular distance to this boundary EDGE,
                        // not point-to-point distance to the sample location.
                        double d = PolygonUtils.DistancePointToSegment(result[pi], a, b);
                        if (d < minDist) minDist = d;
                        if (minDist <= maxDistToBoundary) break;
                    }

                    if (minDist > maxDistToBoundary)
                    {
                        // Always pick from the FULL grid (never row-locked)
                        Point2d best = FindBestGridPointForEdgeSample(sample, a, b, gridPoints, spacing);
                        if (!PolygonUtils.ContainsPoint(result, best, dedupeEps))
                            result.Add(best);
                    }
                }
            }

            AgentLog.Write("BoundaryCoverage", "exit resultN=" + result.Count.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        private static double DistanceToPolygonEdges(Point2d p, List<Point2d> ring)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                double d = PolygonUtils.DistancePointToSegment(p, a, b);
                if (d < best) best = d;
            }
            return best;
        }

        private static Point2d FindBestGridPointForEdgeSample(Point2d sample, Point2d edgeA, Point2d edgeB, List<Point2d> grid, double spacing)
        {
            if (grid == null || grid.Count == 0)
                return sample;

            // Large grids: scan only candidates near the edge sample (full scan is O(N) per sample × many samples).
            const int fullScanLimit = 6000;
            List<Point2d> scan = grid;
            if (grid.Count > fullScanLimit)
            {
                double r = Math.Max(spacing * 25.0, sample.GetDistanceTo(edgeA));
                r = Math.Max(r, sample.GetDistanceTo(edgeB));
                r = Math.Max(r, spacing * 8.0);

                var near = new List<Point2d>();
                for (int i = 0; i < grid.Count; i++)
                {
                    var g = grid[i];
                    if (Math.Abs(g.X - sample.X) <= r && Math.Abs(g.Y - sample.Y) <= r)
                        near.Add(g);
                }
                if (near.Count > 0)
                    scan = near;
            }

            // Prefer grid points that reduce perpendicular distance to the edge,
            // with a secondary preference for being close to the sample location.
            double bestEdgeDist = double.MaxValue;
            double bestSampleDist = double.MaxValue;
            Point2d best = scan[0];
            for (int i = 0; i < scan.Count; i++)
            {
                var g = scan[i];
                double edgeDist = PolygonUtils.DistancePointToSegment(g, edgeA, edgeB);

                // Optional alignment preference: same row/column neighborhood of sample
                // (helps when grid is slightly misaligned vs boundary).
                if (!(Math.Abs(g.X - sample.X) < spacing || Math.Abs(g.Y - sample.Y) < spacing))
                    edgeDist += spacing * 0.05;

                double sampleDist = g.GetDistanceTo(sample);

                if (edgeDist < bestEdgeDist - 1e-9 ||
                    (Math.Abs(edgeDist - bestEdgeDist) <= 1e-9 && sampleDist < bestSampleDist))
                {
                    bestEdgeDist = edgeDist;
                    bestSampleDist = sampleDist;
                    best = g;
                }
            }
            return best;
        }
    }
}

