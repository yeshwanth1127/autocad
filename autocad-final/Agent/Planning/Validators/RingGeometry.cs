using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Agent.Planning.Validators
{
    /// <summary>
    /// Pure 2D helpers over a closed polygon ring: point-in-polygon,
    /// segment-ring clipping, and nearest-edge distance.
    /// Kept separate from AttachBranchesCommand so validators can share it.
    /// </summary>
    internal static class RingGeometry
    {
        public static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
            if (ring == null || ring.Count < 3) return false;
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                double dy = b.Y - a.Y;
                if (dy == 0) dy = 1e-12;
                bool cross = ((a.Y > p.Y) != (b.Y > p.Y)) &&
                             (p.X < (b.X - a.X) * (p.Y - a.Y) / dy + a.X);
                if (cross) inside = !inside;
            }
            return inside;
        }

        /// <summary>Shortest distance from p to any edge of the ring.</summary>
        public static double DistanceToRing(IList<Point2d> ring, Point2d p)
        {
            if (ring == null || ring.Count < 2) return double.MaxValue;
            double best = double.MaxValue;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double d = DistancePointToSegment(p, a, b);
                if (d < best) best = d;
            }
            return best;
        }

        public static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-18)
                return Distance(p, a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = a.X + t * dx;
            double cy = a.Y + t * dy;
            double ex = p.X - cx;
            double ey = p.Y - cy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        public static double Distance(Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Clips segment a→b against a closed polygon ring, returning the inside
        /// portions as a list of (t0,t1) parameter intervals in [0,1] along a→b.
        /// Concave rings may produce multiple intervals.
        /// </summary>
        public static List<(double t0, double t1)> ClipSegmentToRing(Point2d a, Point2d b, IList<Point2d> ring, double eps = 1e-7)
        {
            var result = new List<(double t0, double t1)>();
            if (ring == null || ring.Count < 3) return result;

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < eps)
            {
                if (PointInPolygon(ring, a)) result.Add((0, 1));
                return result;
            }

            var ts = new List<double> { 0, 1 };
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = ring[i];
                var p1 = ring[(i + 1) % n];
                if (TrySegmentSegmentIntersect(a, b, p0, p1, out double tAB, eps))
                {
                    if (tAB > eps && tAB < 1 - eps)
                        ts.Add(tAB);
                }
            }
            ts.Sort();

            var uniq = new List<double>();
            for (int i = 0; i < ts.Count; i++)
            {
                if (uniq.Count == 0 || Math.Abs(ts[i] - uniq[uniq.Count - 1]) > eps)
                    uniq.Add(ts[i]);
            }

            for (int i = 0; i + 1 < uniq.Count; i++)
            {
                double t0 = uniq[i];
                double t1 = uniq[i + 1];
                if (t1 - t0 < eps) continue;
                double tm = 0.5 * (t0 + t1);
                var mid = new Point2d(a.X + tm * dx, a.Y + tm * dy);
                if (PointInPolygon(ring, mid))
                    result.Add((t0, t1));
            }
            return result;
        }

        public static bool TrySegmentSegmentIntersect(Point2d a, Point2d b, Point2d c, Point2d d, out double tAB, double eps = 1e-9)
        {
            tAB = 0;
            double rx = b.X - a.X;
            double ry = b.Y - a.Y;
            double sx = d.X - c.X;
            double sy = d.Y - c.Y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) < eps) return false;
            double qmpx = c.X - a.X;
            double qmpy = c.Y - a.Y;
            double t = (qmpx * sy - qmpy * sx) / denom;
            double u = (qmpx * ry - qmpy * rx) / denom;
            if (t < -eps || t > 1 + eps) return false;
            if (u < -eps || u > 1 + eps) return false;
            tAB = t;
            return true;
        }

        /// <summary>Reads a closed Polyline entity as a flat ring of Point2d samples.</summary>
        public static List<Point2d> FromPolyline(Polyline pl)
        {
            var ring = new List<Point2d>();
            if (pl == null) return ring;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                ring.Add(p);
            }
            return ring;
        }
    }
}
