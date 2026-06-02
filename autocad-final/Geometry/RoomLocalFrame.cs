using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// A 2D rotation frame aligned to a room outline: rotation about the room centroid by the room's dominant
    /// edge orientation. Used so a tilted room can be laid out and routed in its own axis-aligned coordinates,
    /// then transformed back to world. An (near) axis-aligned room produces an identity frame (no transform),
    /// so existing world-aligned behavior is preserved.
    /// </summary>
    public sealed class RoomLocalFrame
    {
        private const double AngleEpsilon = 1e-4; // radians (~0.006°): treat as axis-aligned

        private readonly Point2d _pivot;
        private readonly double _cos;
        private readonly double _sin;

        public bool IsIdentity { get; }

        /// <summary>Local-axis orientation (radians, in [0, π/2)). Zero when the frame is identity.</summary>
        public double Angle { get; }

        private RoomLocalFrame(Point2d pivot, double angle, bool identity)
        {
            _pivot = pivot;
            Angle = identity ? 0.0 : angle;
            IsIdentity = identity;
            _cos = Math.Cos(Angle);
            _sin = Math.Sin(Angle);
        }

        /// <summary>
        /// Builds the frame from a closed ring: pivot at the vertex centroid, angle from the longest edge,
        /// normalized into [0, π/2) (a grid is symmetric under 90° rotation). Degenerate or near-axis rings
        /// yield an identity frame.
        /// </summary>
        public static RoomLocalFrame FromRing(IList<Point2d> ring)
        {
            if (ring == null || ring.Count < 3)
                return new RoomLocalFrame(default, 0.0, identity: true);

            Point2d pivot = Centroid(ring);

            // Longest edge orientation.
            double bestLenSq = -1.0;
            double bestAngle = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d a = ring[i];
                Point2d b = ring[(i + 1) % n];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq > bestLenSq)
                {
                    bestLenSq = lenSq;
                    bestAngle = Math.Atan2(dy, dx);
                }
            }

            double angle = NormalizeToFirstQuadrant(bestAngle);
            bool identity = Math.Abs(angle) <= AngleEpsilon || bestLenSq <= 0.0;
            return new RoomLocalFrame(pivot, angle, identity);
        }

        /// <summary>World → local (rotate about pivot by −Angle).</summary>
        public Point2d ToLocal(Point2d p)
        {
            if (IsIdentity) return p;
            double dx = p.X - _pivot.X;
            double dy = p.Y - _pivot.Y;
            // Rotate by -Angle.
            double rx = dx * _cos + dy * _sin;
            double ry = -dx * _sin + dy * _cos;
            return new Point2d(_pivot.X + rx, _pivot.Y + ry);
        }

        /// <summary>Local → world (rotate about pivot by +Angle).</summary>
        public Point2d ToWorld(Point2d p)
        {
            if (IsIdentity) return p;
            double dx = p.X - _pivot.X;
            double dy = p.Y - _pivot.Y;
            // Rotate by +Angle.
            double rx = dx * _cos - dy * _sin;
            double ry = dx * _sin + dy * _cos;
            return new Point2d(_pivot.X + rx, _pivot.Y + ry);
        }

        public List<Point2d> ToLocal(IList<Point2d> pts)
        {
            var r = new List<Point2d>(pts?.Count ?? 0);
            if (pts == null) return r;
            for (int i = 0; i < pts.Count; i++) r.Add(ToLocal(pts[i]));
            return r;
        }

        public List<Point2d> ToWorld(IList<Point2d> pts)
        {
            var r = new List<Point2d>(pts?.Count ?? 0);
            if (pts == null) return r;
            for (int i = 0; i < pts.Count; i++) r.Add(ToWorld(pts[i]));
            return r;
        }

        private static Point2d Centroid(IList<Point2d> ring)
        {
            double sx = 0, sy = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++) { sx += ring[i].X; sy += ring[i].Y; }
            double d = Math.Max(1, n);
            return new Point2d(sx / d, sy / d);
        }

        /// <summary>Reduce an angle to [0, π/2): a grid looks identical under 90° rotation and axis flips.</summary>
        private static double NormalizeToFirstQuadrant(double angle)
        {
            double half = Math.PI / 2.0;
            double a = angle % half;
            if (a < 0) a += half;
            // Snap angles within epsilon of 0 or π/2 to 0 (axis-aligned).
            if (a <= AngleEpsilon || a >= half - AngleEpsilon)
                return 0.0;
            return a;
        }
    }
}
