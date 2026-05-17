using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>Shared 2D polygon / ring helpers for placement, offset, and fix workflows.</summary>
    public static class PolygonUtils
    {
        public static int FindBottomLeftVertexIndex(List<Point2d> ring)
        {
            if (ring == null || ring.Count == 0) return 0;
            int best = 0;
            for (int i = 1; i < ring.Count; i++)
            {
                if (ring[i].Y < ring[best].Y - 1e-9 ||
                    (Math.Abs(ring[i].Y - ring[best].Y) <= 1e-9 && ring[i].X < ring[best].X))
                    best = i;
            }
            return best;
        }

        public static void GetBoundingBox(List<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.PositiveInfinity;
            maxX = maxY = double.NegativeInfinity;
            if (ring == null) return;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        public static bool PointInPolygon(List<Point2d> ring, Point2d p)
        {
            if (ring == null || ring.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double xi = ring[i].X, yi = ring[i].Y;
                double xj = ring[j].X, yj = ring[j].Y;
                bool intersect = ((yi > p.Y) != (yj > p.Y)) &&
                                 (p.X < (xj - xi) * (p.Y - yi) / ((yj - yi) == 0 ? 1e-12 : (yj - yi)) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        public static double SignedArea(List<Point2d> ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            double a = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                a += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
            return 0.5 * a;
        }

        /// <summary>Area-weighted polygon centroid (falls back to bbox center if degenerate).</summary>
        public static Point2d ApproxCentroidAreaWeighted(List<Point2d> ring)
        {
            if (ring == null || ring.Count < 3) return new Point2d(0, 0);
            double a = 0, cx = 0, cy = 0;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double x0 = ring[j].X, y0 = ring[j].Y;
                double x1 = ring[i].X, y1 = ring[i].Y;
                double cross = x0 * y1 - x1 * y0;
                a += cross;
                cx += (x0 + x1) * cross;
                cy += (y0 + y1) * cross;
            }

            if (Math.Abs(a) <= 1e-18)
            {
                GetBoundingBox(ring, out double minX, out double minY, out double maxX, out double maxY);
                return new Point2d(0.5 * (minX + maxX), 0.5 * (minY + maxY));
            }

            double inv = 1.0 / (3.0 * a);
            return new Point2d(cx * inv, cy * inv);
        }

        public static bool IsRingInside(List<Point2d> outer, List<Point2d> inner)
        {
            if (outer == null || inner == null || outer.Count < 3 || inner.Count < 3)
                return false;

            int step = Math.Max(1, inner.Count / 8);
            for (int i = 0; i < inner.Count; i += step)
            {
                if (!PointInPolygon(outer, inner[i]))
                    return false;
            }

            return true;
        }

        public static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return p.GetDistanceTo(a);
            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) return p.GetDistanceTo(b);
            double t = c1 / c2;
            var proj = new Point2d(a.X + t * vx, a.Y + t * vy);
            return p.GetDistanceTo(proj);
        }

        public static bool IsPointOnRingEdge(List<Point2d> ring, Point2d p, double eps)
        {
            if (ring == null) return false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                if (DistancePointToSegment(p, ring[j], ring[i]) <= eps)
                    return true;
            }
            return false;
        }

        /// <summary>Shortest perpendicular distance from <paramref name="p"/> to the closed ring segments.</summary>
        public static double MinDistancePointToPolygonBoundary(List<Point2d> ring, Point2d p)
        {
            if (ring == null || ring.Count < 2)
                return double.PositiveInfinity;
            double best = double.MaxValue;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double d = DistancePointToSegment(p, ring[j], ring[i]);
                if (d < best)
                    best = d;
            }
            return best == double.MaxValue ? double.PositiveInfinity : best;
        }

        public static bool ContainsPoint(List<Point2d> points, Point2d p, double eps)
        {
            if (points == null) return false;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].GetDistanceTo(p) <= eps)
                    return true;
            }
            return false;
        }
    }
}
