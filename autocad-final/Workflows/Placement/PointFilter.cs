using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Placement
{
    /// <summary>Merges boundary and interior sprinkler candidates with deduplication and fallback.</summary>
    public static class PointFilter
    {
        public static List<Point2d> MergeBoundaryAndInteriorLattice(
            List<Point2d> boundarySprinklers,
            List<Point2d> interiorCandidates,
            List<Point2d> offsetRing,
            double gridSpacing)
        {
            var points = new List<Point2d>();
            if (offsetRing == null || offsetRing.Count < 3 || gridSpacing <= 0)
                return points;

            double eps = Math.Max(1.0, gridSpacing * 1e-4);
            if (boundarySprinklers != null)
                points.AddRange(boundarySprinklers);

            PolygonUtils.GetBoundingBox(offsetRing, out double minBoundaryX, out double minBoundaryY, out double maxBoundaryX, out double maxBoundaryY);

            if (interiorCandidates != null)
            {
                for (int i = 0; i < interiorCandidates.Count; i++)
                {
                    var p = interiorCandidates[i];
                    if (p.X < minBoundaryX - eps || p.X > maxBoundaryX + eps || p.Y < minBoundaryY - eps || p.Y > maxBoundaryY + eps)
                        continue;
                    if (!(PolygonUtils.PointInPolygon(offsetRing, p) || PolygonUtils.IsPointOnRingEdge(offsetRing, p, eps)))
                        continue;
                    if (PolygonUtils.ContainsPoint(points, p, eps))
                        continue;
                    points.Add(p);
                }
            }

            if (points.Count == 0 &&
                PointGenerator.TryFindAnyInteriorPoint(offsetRing, minBoundaryX, minBoundaryY, maxBoundaryX, maxBoundaryY, gridSpacing, out Point2d fallback))
                points.Add(fallback);

            return points;
        }

        public static List<Point2d> FilterInsidePolygon(
            List<Point2d> candidates,
            List<Point2d> ring,
            double spacing)
        {
            var points = new List<Point2d>();
            if (candidates == null || ring == null || ring.Count < 3 || spacing <= 0)
                return points;

            // Keep tolerance tight: PointInPolygon is exact, only edge-proximity needs a small epsilon.
            // A large epsilon can incorrectly admit points outside the boundary.
            double eps = Math.Max(1e-6, spacing * 1e-3);

            foreach (var p in candidates)
            {
                if (!(PolygonUtils.PointInPolygon(ring, p) ||
                      PolygonUtils.IsPointOnRingEdge(ring, p, eps)))
                    continue;

                if (PolygonUtils.ContainsPoint(points, p, eps))
                    continue;

                points.Add(p);
            }

            return points;
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
    }
}
