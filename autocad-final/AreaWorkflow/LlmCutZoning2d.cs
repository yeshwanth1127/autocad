using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// LLM-supplied straight-cut zoning: the agent proposes N-1 cut segments for N shafts; the
    /// engine applies them sequentially to split the floor polygon and validates that each
    /// resulting piece contains exactly one shaft. Used as a fallback when
    /// <see cref="EqualAreaRecursiveBisection2d"/> cannot find clean straight cuts automatically.
    /// </summary>
    public static class LlmCutZoning2d
    {
        public struct Cut
        {
            public Point2d A;
            public Point2d B;
            public Cut(Point2d a, Point2d b) { A = a; B = b; }
        }

        public static bool TryApplyCuts(
            List<Point2d> floorRing,
            List<Point2d> shaftSites,
            IList<Cut> cuts,
            out List<List<Point2d>> zoneRings,
            out List<int> shaftIndexPerRing,
            out string summary)
        {
            zoneRings = null;
            shaftIndexPerRing = null;
            summary = null;

            if (floorRing == null || floorRing.Count < 3)
            {
                summary = "invalid floor ring";
                return false;
            }
            int n = shaftSites?.Count ?? 0;
            if (n < 1)
            {
                summary = "no shafts";
                return false;
            }
            if (cuts == null) cuts = new List<Cut>();
            if (cuts.Count != n - 1)
            {
                summary = "expected " + (n - 1) + " cuts for " + n + " shafts, got " + cuts.Count;
                return false;
            }

            GetExtents(floorRing, out double minX, out double minY, out double maxX, out double maxY);
            double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            double eps = 1e-6 * Math.Max(diag, 1.0);

            var pieces = new List<List<Point2d>> { new List<Point2d>(floorRing) };

            for (int ci = 0; ci < cuts.Count; ci++)
            {
                var cut = cuts[ci];
                double abx = cut.B.X - cut.A.X, aby = cut.B.Y - cut.A.Y;
                double len = Math.Sqrt(abx * abx + aby * aby);
                if (len < eps)
                {
                    summary = "cut " + ci + " has zero length";
                    return false;
                }
                double nx = -aby / len, ny = abx / len;
                double d = nx * cut.A.X + ny * cut.A.Y;

                int pi = -1;
                for (int p = 0; p < pieces.Count; p++)
                {
                    if (CountBoundaryCrossings(pieces[p], nx, ny, d, eps) == 2)
                    {
                        pi = p;
                        break;
                    }
                }
                if (pi < 0)
                {
                    summary = "cut " + ci + " does not cleanly bisect any remaining piece (needs exactly 2 boundary crossings)";
                    return false;
                }

                var target = pieces[pi];
                var sideA = ClipKeepSide(target, nx, ny, d, +1, eps);
                var sideB = ClipKeepSide(target, nx, ny, d, -1, eps);
                if (sideA.Count < 3 || sideB.Count < 3)
                {
                    summary = "cut " + ci + " produced a degenerate piece";
                    return false;
                }

                pieces.RemoveAt(pi);
                pieces.Add(sideA);
                pieces.Add(sideB);
            }

            if (pieces.Count != n)
            {
                summary = "cuts produced " + pieces.Count + " pieces for " + n + " shafts";
                return false;
            }

            var owners = new int[pieces.Count];
            for (int p = 0; p < pieces.Count; p++) owners[p] = -1;

            for (int s = 0; s < n; s++)
            {
                int hits = 0, match = -1;
                for (int p = 0; p < pieces.Count; p++)
                {
                    if (PointInPolygon(pieces[p], shaftSites[s]))
                    {
                        hits++;
                        match = p;
                    }
                }
                if (hits != 1)
                {
                    summary = "shaft " + s + " matches " + hits + " pieces (expected exactly 1)";
                    return false;
                }
                if (owners[match] != -1)
                {
                    summary = "piece contains multiple shafts (shaft " + s + " + shaft " + owners[match] + ")";
                    return false;
                }
                owners[match] = s;
            }

            for (int p = 0; p < pieces.Count; p++)
                if (owners[p] < 0)
                {
                    summary = "piece " + p + " has no shaft inside";
                    return false;
                }

            var ordered = new List<Point2d>[n];
            for (int p = 0; p < pieces.Count; p++) ordered[owners[p]] = pieces[p];

            zoneRings = new List<List<Point2d>>(n);
            shaftIndexPerRing = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                zoneRings.Add(ordered[i]);
                shaftIndexPerRing.Add(i);
            }

            summary = "llm_cuts: applied " + cuts.Count + " cuts → " + n + " zones";
            return true;
        }

        private static int CountBoundaryCrossings(IList<Point2d> polygon, double nx, double ny, double d, double eps)
        {
            int cnt = 0;
            int n = polygon.Count;
            for (int k = 0; k < n; k++)
            {
                var s = polygon[k];
                var e = polygon[(k + 1) % n];
                double sv = nx * s.X + ny * s.Y - d;
                double ev = nx * e.X + ny * e.Y - d;
                if (Math.Abs(sv) <= eps && Math.Abs(ev) <= eps) continue;
                if ((sv > eps && ev < -eps) || (sv < -eps && ev > eps)) cnt++;
                else if (Math.Abs(sv) <= eps && Math.Abs(ev) > eps) cnt++;
            }
            return cnt;
        }

        private static List<Point2d> ClipKeepSide(
            IList<Point2d> vertices, double nx, double ny, double d, int sign, double eps)
        {
            int n = vertices.Count;
            if (n < 3) return new List<Point2d>();

            var output = new List<Point2d>();
            for (int k = 0; k < n; k++)
            {
                Point2d s = vertices[k];
                Point2d e = vertices[(k + 1) % n];
                double sv = sign * (nx * s.X + ny * s.Y - d);
                double ev = sign * (nx * e.X + ny * e.Y - d);
                bool sIn = sv >= -eps;
                bool eIn = ev >= -eps;

                if (sIn && eIn) output.Add(e);
                else if (sIn && !eIn)
                {
                    if (TryIntersect(s, e, nx, ny, d, out Point2d hit)) output.Add(hit);
                }
                else if (!sIn && eIn)
                {
                    if (TryIntersect(s, e, nx, ny, d, out Point2d hit)) output.Add(hit);
                    output.Add(e);
                }
            }

            // collapse consecutive coincident vertices
            var result = new List<Point2d>(output.Count);
            for (int i = 0; i < output.Count; i++)
            {
                var p = output[i];
                if (result.Count > 0)
                {
                    var q = result[result.Count - 1];
                    if (Math.Abs(p.X - q.X) <= eps && Math.Abs(p.Y - q.Y) <= eps) continue;
                }
                result.Add(p);
            }
            if (result.Count >= 2)
            {
                var first = result[0];
                var last = result[result.Count - 1];
                if (Math.Abs(first.X - last.X) <= eps && Math.Abs(first.Y - last.Y) <= eps)
                    result.RemoveAt(result.Count - 1);
            }
            return result;
        }

        private static bool TryIntersect(Point2d a, Point2d b, double nx, double ny, double d, out Point2d hit)
        {
            hit = default;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double denom = nx * dx + ny * dy;
            if (Math.Abs(denom) < 1e-18) return false;
            double t = (d - nx * a.X - ny * a.Y) / denom;
            if (t < -1e-9 || t > 1.0 + 1e-9) return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            hit = new Point2d(a.X + t * dx, a.Y + t * dy);
            return true;
        }

        private static bool PointInPolygon(IList<Point2d> ring, Point2d p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                bool intersect =
                    ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static void GetExtents(IList<Point2d> ring, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.PositiveInfinity; minY = double.PositiveInfinity;
            maxX = double.NegativeInfinity; maxY = double.NegativeInfinity;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
    }
}
