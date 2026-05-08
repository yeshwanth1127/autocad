using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    using Seg = MainPipeDetector.MainPipeSegment;

    /// <summary>
    /// Routes branch pipes from sprinkler heads perpendicularly to a trunk that can be at
    /// any angle (not just horizontal or vertical). Also routes the connector from a shaft
    /// to the nearest point on the trunk set, handling external shafts via boundary crossing.
    /// </summary>
    public static class AngleTrunkRouting2d
    {
        public sealed class BranchPath
        {
            public Point2d SprinklerPoint;
            public Point2d FootOnTrunk;
            public Seg OwningSegment;
        }

        public sealed class AngleRouteResult
        {
            public List<BranchPath> Branches;
            public List<Point2d> ConnectorPath;
            /// <summary>Dominant angle of the trunk in degrees [0–180).</summary>
            public double TrunkAngleDeg;
            public string Summary;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// For each sprinkler, finds the closest foot on any trunk segment and records
        /// the branch (perpendicular drop). Works for any trunk angle.
        /// </summary>
        public static List<BranchPath> ComputeBranches(
            List<Point2d> sprinklers,
            List<Seg> trunk)
        {
            var branches = new List<BranchPath>(sprinklers?.Count ?? 0);
            if (sprinklers == null || trunk == null || trunk.Count == 0)
                return branches;

            foreach (var s in sprinklers)
            {
                Seg bestSeg = default;
                Point2d bestFoot = default;
                double bestD2 = double.MaxValue;
                bool found = false;

                foreach (var seg in trunk)
                {
                    var foot = ClosestPointOnSegment(s, seg.Start, seg.End);
                    double dx = s.X - foot.X, dy = s.Y - foot.Y;
                    double d2 = dx * dx + dy * dy;
                    if (!found || d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestFoot = foot;
                        bestSeg = seg;
                        found = true;
                    }
                }

                if (found)
                    branches.Add(new BranchPath { SprinklerPoint = s, FootOnTrunk = bestFoot, OwningSegment = bestSeg });
            }

            return branches;
        }

        /// <summary>
        /// Routes the connector from <paramref name="shaftPt"/> to the nearest point on the trunk.
        /// If the shaft is outside <paramref name="ring"/>, the path crosses the boundary cleanly:
        ///   shaft → boundary-entry → nearest-trunk-foot
        /// The inside portion uses simple L-shaped routing (two axis-aligned segments).
        /// </summary>
        public static List<Point2d> RouteConnector(
            Point2d shaftPt,
            List<Seg> trunk,
            List<Point2d> ring,
            double eps)
        {
            if (trunk == null || trunk.Count == 0 || ring == null || ring.Count < 3)
                return new List<Point2d> { shaftPt };

            // Find closest point on any trunk segment.
            Point2d trunkFoot = default;
            double bestD2 = double.MaxValue;
            foreach (var seg in trunk)
            {
                var foot = ClosestPointOnSegment(shaftPt, seg.Start, seg.End);
                double dx = shaftPt.X - foot.X, dy = shaftPt.Y - foot.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; trunkFoot = foot; }
            }

            bool shaftOutside = !PointInPolygon(ring, shaftPt.X, shaftPt.Y);

            var path = new List<Point2d>();
            path.Add(shaftPt);

            if (shaftOutside)
            {
                // Find boundary crossing on ray shaft → trunkFoot.
                if (TryFindBoundaryCrossing(ring, shaftPt, trunkFoot, eps, out Point2d crossing))
                    path.Add(crossing);
                else
                {
                    // Fallback: snap slightly inside and continue.
                    if (TrySnapInsideRing(ring, shaftPt, trunkFoot, eps, out Point2d snapped))
                        path.Add(snapped);
                }
            }

            // Inside portion: L-shaped route (horizontal then vertical, or vertical then horizontal).
            Point2d inside = path[path.Count - 1];
            if (!PointInPolygon(ring, inside.X, inside.Y))
            {
                // If we're still outside after snapping, just go straight.
                path.Add(trunkFoot);
                return path;
            }

            // Try H then V.
            var mid1 = new Point2d(trunkFoot.X, inside.Y);
            if (PointInPolygon(ring, mid1.X, mid1.Y))
            {
                path.Add(mid1);
                path.Add(trunkFoot);
                return path;
            }

            // Try V then H.
            var mid2 = new Point2d(inside.X, trunkFoot.Y);
            if (PointInPolygon(ring, mid2.X, mid2.Y))
            {
                path.Add(mid2);
                path.Add(trunkFoot);
                return path;
            }

            // Straight line fallback.
            path.Add(trunkFoot);
            return path;
        }

        // ── Geometry helpers ──────────────────────────────────────────────────────

        public static Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double len2 = vx * vx + vy * vy;
            if (len2 < 1e-18) return a;
            double t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            return new Point2d(a.X + t * vx, a.Y + t * vy);
        }

        private static bool TryFindBoundaryCrossing(
            List<Point2d> ring,
            Point2d from,
            Point2d to,
            double eps,
            out Point2d crossing)
        {
            crossing = default;
            double vx = to.X - from.X, vy = to.Y - from.Y;
            double vlen = Math.Sqrt(vx * vx + vy * vy);
            if (vlen < eps) return false;

            double bestT = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double denom = vx * ey - vy * ex;
                if (Math.Abs(denom) < 1e-18) continue;
                double t = ((a.X - from.X) * ey - (a.Y - from.Y) * ex) / denom;
                double u = ((a.X - from.X) * vy - (a.Y - from.Y) * vx) / denom;
                if (t < -eps || u < -eps || u > 1 + eps) continue;
                if (t < bestT) bestT = t;
            }

            if (double.IsPositiveInfinity(bestT)) return false;
            crossing = new Point2d(from.X + bestT * vx, from.Y + bestT * vy);
            return true;
        }

        private static bool TrySnapInsideRing(List<Point2d> ring, Point2d outside, Point2d target, double eps, out Point2d snapped)
        {
            snapped = outside;
            double vx = target.X - outside.X, vy = target.Y - outside.Y;
            double vlen = Math.Sqrt(vx * vx + vy * vy);
            if (vlen < eps) return false;

            double nudge = Math.Max(eps * 20.0, vlen * 0.01);
            double bestT = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double denom = vx * ey - vy * ex;
                if (Math.Abs(denom) < 1e-18) continue;
                double t = ((a.X - outside.X) * ey - (a.Y - outside.Y) * ex) / denom;
                double u = ((a.X - outside.X) * vy - (a.Y - outside.Y) * vx) / denom;
                if (t < -eps || t > 1 + eps || u < -eps || u > 1 + eps) continue;
                if (t < bestT) bestT = t;
            }

            if (double.IsPositiveInfinity(bestT)) return false;
            for (int k = 0; k < 4; k++)
            {
                double tEntry = Math.Min(1.0, bestT + nudge * (1 << k) / vlen);
                var cand = new Point2d(outside.X + tEntry * vx, outside.Y + tEntry * vy);
                if (PointInPolygon(ring, cand.X, cand.Y)) { snapped = cand; return true; }
            }
            return false;
        }

        private static bool PointInPolygon(IList<Point2d> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi))
                    inside = !inside;
            }
            return inside;
        }
    }
}
