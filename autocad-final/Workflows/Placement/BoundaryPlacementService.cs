using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Placement
{
    /// <summary>Sprinkler points sampled along the inward offset boundary ring.</summary>
    public static class BoundaryPlacementService
    {
        public static List<Point2d> GenerateAlongRing(List<Point2d> ring, double gridSpacing)
        {
            var points = new List<Point2d>();
            if (ring == null || ring.Count < 2 || gridSpacing <= 0)
                return points;

            int startIndex = PolygonUtils.FindBottomLeftVertexIndex(ring);
            var ordered = new List<Point2d>(ring.Count + 1);
            for (int i = 0; i < ring.Count; i++)
                ordered.Add(ring[(startIndex + i) % ring.Count]);
            ordered.Add(ordered[0]);

            double eps = Math.Max(1.0, gridSpacing * 1e-4);
            double perimeter = 0.0;
            for (int i = 0; i < ordered.Count - 1; i++)
                perimeter += ordered[i].GetDistanceTo(ordered[i + 1]);
            if (perimeter <= eps)
                return points;

            for (double d = 0.0; d < perimeter - eps; d += gridSpacing)
            {
                var p = PointOnPathAtDistance(ordered, d);
                if (!PolygonUtils.ContainsPoint(points, p, eps))
                    points.Add(p);
            }

            for (int i = 0; i < ordered.Count - 1; i++)
            {
                if (!PolygonUtils.ContainsPoint(points, ordered[i], eps))
                    points.Add(ordered[i]);
            }

            return points;
        }

        private static Point2d PointOnPathAtDistance(List<Point2d> orderedPath, double distance)
        {
            double remain = Math.Max(0.0, distance);
            for (int i = 0; i < orderedPath.Count - 1; i++)
            {
                var a = orderedPath[i];
                var b = orderedPath[i + 1];
                double segLen = a.GetDistanceTo(b);
                if (segLen <= 1e-12)
                    continue;
                if (remain <= segLen)
                {
                    double t = remain / segLen;
                    return new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                }
                remain -= segLen;
            }
            return orderedPath[orderedPath.Count - 1];
        }
    }
}
