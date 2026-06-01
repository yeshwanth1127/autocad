using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Pure-geometry branch router for the "Route branches" button.
    ///
    /// Branch lines follow the world-axis sprinkler grid: a mostly-horizontal main produces
    /// vertical branch lines (heads share X), a mostly-vertical main produces horizontal ones
    /// (heads share Y). Each line is drawn as ONE straight pipe at its gridline coordinate and is
    /// tapped once where the main crosses it. Lines the main never crosses are served by a single
    /// sub-main "spine" that continues from the nearest main end, with branches tapping it on both
    /// sides. Every emitted segment is validated against the (arbitrary-shaped) zone ring.
    /// </summary>
    public static class GridBranchPlanner
    {
        public sealed class Result
        {
            public List<List<Point2d>> Branches { get; } = new List<List<Point2d>>();
            public List<List<Point2d>> Spines { get; } = new List<List<Point2d>>();
        }

        private sealed class BranchLine
        {
            public double Perp;            // gridline coordinate (X for vertical lines, Y for horizontal)
            public List<double> Alongs;    // head positions along the line, ascending
        }

        public static Result Plan(List<Point2d> zoneRing, List<Point2d> sprinklers, List<Point2d> mainPolyline)
        {
            var result = new Result();
            if (zoneRing == null || zoneRing.Count < 3) return result;
            if (sprinklers == null || sprinklers.Count == 0) return result;
            if (mainPolyline == null || mainPolyline.Count < 2) return result;

            // 1. Branch axis. Horizontal main -> vertical branch lines (run along Y, share X).
            bool branchAlongY = MainIsHorizontal(mainPolyline);

            Func<Point2d, double> perp = p => branchAlongY ? p.X : p.Y;   // gridline coordinate
            Func<Point2d, double> along = p => branchAlongY ? p.Y : p.X;  // position on the line
            Func<double, double, Point2d> make = (perpC, alongC) =>
                branchAlongY ? new Point2d(perpC, alongC) : new Point2d(alongC, perpC);

            // 2. Cluster heads into branch lines by their gridline coordinate.
            var lines = ClusterLines(sprinklers, perp, along);
            if (lines.Count == 0) return result;

            double mainPerpMin = mainPolyline.Min(p => perp(p));
            double mainPerpMax = mainPolyline.Max(p => perp(p));

            var unreached = new List<BranchLine>();

            // 3-4. Lines the main crosses inside the zone are drawn directly.
            foreach (var line in lines)
            {
                if (TryMainCrossAtPerp(mainPolyline, line.Perp, branchAlongY, out Point2d tap) &&
                    PolygonUtils.PointInPolygon(zoneRing, tap))
                {
                    double tapAlong = along(tap);
                    var branch = BuildStraightLine(zoneRing, line.Perp, line.Alongs, tapAlong, make, out List<double> leftover);
                    AddChainAsSegments(result.Branches, branch);
                    if (leftover != null && leftover.Count > 0)
                        unreached.Add(new BranchLine { Perp = line.Perp, Alongs = leftover });
                }
                else
                {
                    unreached.Add(line);
                }
            }

            // 5. Spine(s) for unreached lines, grouped by which main end they extend from.
            if (unreached.Count > 0)
            {
                double mid = 0.5 * (mainPerpMin + mainPerpMax);
                var lowSide = unreached.Where(l => l.Perp <= mid).ToList();
                var highSide = unreached.Where(l => l.Perp > mid).ToList();

                BuildSpineForSide(result, zoneRing, mainPolyline, lowSide, perp, along, make, mainPerpMin);
                BuildSpineForSide(result, zoneRing, mainPolyline, highSide, perp, along, make, mainPerpMax);
            }

            return result;
        }

        private static bool MainIsHorizontal(List<Point2d> pts)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return (maxX - minX) >= (maxY - minY);
        }

        private static List<BranchLine> ClusterLines(List<Point2d> heads, Func<Point2d, double> perp, Func<Point2d, double> along)
        {
            var result = new List<BranchLine>();
            var sorted = heads.OrderBy(p => perp(p)).ToList();

            // Natural-break tolerance: small intra-line gaps vs large between-line gaps.
            var posGaps = new List<double>();
            for (int i = 0; i + 1 < sorted.Count; i++)
            {
                double g = perp(sorted[i + 1]) - perp(sorted[i]);
                if (g > 1e-9) posGaps.Add(g);
            }
            posGaps.Sort();

            double tolerance;
            if (posGaps.Count == 0)
            {
                tolerance = 0.5;
            }
            else
            {
                double bestRatio = 1.0;
                int splitIdx = -1;
                for (int i = 1; i < posGaps.Count; i++)
                {
                    double r = posGaps[i] / Math.Max(posGaps[i - 1], 1e-9);
                    if (r > bestRatio) { bestRatio = r; splitIdx = i; }
                }
                tolerance = (splitIdx >= 0 && bestRatio >= 2.0)
                    ? 0.5 * (posGaps[splitIdx - 1] + posGaps[splitIdx])
                    : 0.4 * posGaps[0];
            }

            var current = new List<Point2d> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (perp(sorted[i]) - perp(sorted[i - 1]) <= tolerance)
                    current.Add(sorted[i]);
                else
                {
                    result.Add(MakeLine(current, perp, along));
                    current = new List<Point2d> { sorted[i] };
                }
            }
            if (current.Count > 0)
                result.Add(MakeLine(current, perp, along));

            return result;
        }

        private static BranchLine MakeLine(List<Point2d> cluster, Func<Point2d, double> perp, Func<Point2d, double> along)
        {
            return new BranchLine
            {
                Perp = cluster.Average(p => perp(p)),
                Alongs = cluster.Select(p => along(p)).OrderBy(a => a).ToList()
            };
        }

        /// <summary>
        /// Point on the main where the gridline (perp == c) crosses it. matchX = vertical branch.
        /// </summary>
        private static bool TryMainCrossAtPerp(List<Point2d> main, double c, bool branchAlongY, out Point2d hit)
        {
            hit = default;
            for (int i = 0; i + 1 < main.Count; i++)
            {
                var a = main[i];
                var b = main[i + 1];
                double ca = branchAlongY ? a.X : a.Y;
                double cb = branchAlongY ? b.X : b.Y;
                double lo = Math.Min(ca, cb);
                double hi = Math.Max(ca, cb);
                if (c < lo - 1e-9 || c > hi + 1e-9) continue;

                double denom = cb - ca;
                double t = Math.Abs(denom) < 1e-12 ? 0.0 : (c - ca) / denom;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                hit = new Point2d(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Builds one straight branch pipe at gridline <paramref name="c"/> spanning the heads and
        /// the tap. If part of the span leaves the zone (concave shapes), it is clipped to the
        /// inside run that contains the tap, and heads beyond the gap are returned as leftover.
        /// </summary>
        private static List<Point2d> BuildStraightLine(List<Point2d> zoneRing, double c, List<double> headAlongs, double tapAlong, Func<double, double, Point2d> make, out List<double> leftover)
        {
            leftover = new List<double>();
            if (headAlongs == null || headAlongs.Count == 0) return null;

            double minH = headAlongs[0];
            double maxH = headAlongs[headAlongs.Count - 1];
            double lo = Math.Min(tapAlong, minH);
            double hi = Math.Max(tapAlong, maxH);
            if (hi - lo < 1e-7) return null;

            // Determine the inside run [aAlong, bAlong] that the branch may occupy: the whole
            // span if it stays inside the zone, otherwise the maximal inside run containing the
            // tap (concave zones). Heads outside that run are returned as leftover.
            double aAlong, bAlong;
            if (SegmentInside(zoneRing, make(c, lo), make(c, hi)))
            {
                aAlong = lo;
                bAlong = hi;
            }
            else
            {
                const int n = 200;
                var inside = new bool[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    double a = lo + (hi - lo) * i / n;
                    inside[i] = PolygonUtils.PointInPolygon(zoneRing, make(c, a));
                }

                int tapIdx = (int)Math.Round((tapAlong - lo) / (hi - lo) * n);
                if (tapIdx < 0) tapIdx = 0; else if (tapIdx > n) tapIdx = n;
                if (!inside[tapIdx])
                {
                    // No inside coverage at the tap -> can't serve from here.
                    leftover.AddRange(headAlongs);
                    return null;
                }

                int aIdx = tapIdx;
                while (aIdx - 1 >= 0 && inside[aIdx - 1]) aIdx--;
                int bIdx = tapIdx;
                while (bIdx + 1 <= n && inside[bIdx + 1]) bIdx++;

                aAlong = lo + (hi - lo) * aIdx / n;
                bAlong = lo + (hi - lo) * bIdx / n;
                if (bAlong - aAlong < 1e-7)
                {
                    leftover.AddRange(headAlongs);
                    return null;
                }
            }

            // Build the chain main(tap) -> head -> head -> ... as a single polyline with a vertex
            // at the tap and at every served head (needed for per-segment sizing/reducers/labels),
            // ordered monotonically along the straight line.
            var alongs = new List<double>();
            if (tapAlong >= aAlong - 1e-6 && tapAlong <= bAlong + 1e-6)
                alongs.Add(tapAlong);
            foreach (double h in headAlongs)
            {
                if (h < aAlong - 1e-6 || h > bAlong + 1e-6)
                    leftover.Add(h);
                else
                    alongs.Add(h);
            }
            alongs.Sort();

            var verts = new List<Point2d>();
            double prev = double.NaN;
            foreach (double a in alongs)
            {
                if (!double.IsNaN(prev) && Math.Abs(a - prev) <= 1e-6) continue; // dedupe (head on tap)
                verts.Add(make(c, a));
                prev = a;
            }

            return verts.Count >= 2 ? verts : null;
        }

        private static void BuildSpineForSide(Result result, List<Point2d> zoneRing, List<Point2d> main, List<BranchLine> sideLines, Func<Point2d, double> perp, Func<Point2d, double> along, Func<double, double, Point2d> make, double mainPerpEnd)
        {
            if (sideLines == null || sideLines.Count == 0) return;

            // Main endpoint nearest this perp extreme -> the spine continues from here.
            var mainEnd = main[0];
            double best = double.MaxValue;
            foreach (var p in main)
            {
                double d = Math.Abs(perp(p) - mainPerpEnd);
                if (d < best) { best = d; mainEnd = p; }
            }
            double spineAlong = along(mainEnd);

            double minC = Math.Min(mainPerpEnd, sideLines.Min(l => l.Perp));
            double maxC = Math.Max(mainPerpEnd, sideLines.Max(l => l.Perp));
            if (maxC - minC < 1e-7) return;

            double step = SpineScanStep(sideLines);
            double chosenAlong = spineAlong;
            bool spineOk = false;
            for (int k = 0; k <= 8 && !spineOk; k++)
            {
                foreach (double cand in (k == 0 ? new[] { spineAlong } : new[] { spineAlong + k * step, spineAlong - k * step }))
                {
                    if (SegmentInside(zoneRing, make(minC, cand), make(maxC, cand)))
                    {
                        chosenAlong = cand;
                        spineOk = true;
                        break;
                    }
                }
            }

            if (spineOk)
                result.Spines.Add(new List<Point2d> { make(minC, chosenAlong), make(maxC, chosenAlong) });

            // Tap each unreached line off the spine (or, if no inside spine was found, attempt the
            // straight branch anyway; BuildStraightLine clips it to whatever is inside).
            foreach (var line in sideLines)
            {
                var branch = BuildStraightLine(zoneRing, line.Perp, line.Alongs, chosenAlong, make, out List<double> _);
                AddChainAsSegments(result.Branches, branch);
            }
        }

        private static double SpineScanStep(List<BranchLine> lines)
        {
            // Use the typical head spacing along the lines as the scan step.
            var gaps = new List<double>();
            foreach (var l in lines)
                for (int i = 0; i + 1 < l.Alongs.Count; i++)
                {
                    double g = l.Alongs[i + 1] - l.Alongs[i];
                    if (g > 1e-9) gaps.Add(g);
                }
            if (gaps.Count == 0) return 1.0;
            gaps.Sort();
            return Math.Max(gaps[gaps.Count / 2], 1e-6);
        }

        /// <summary>
        /// Splits a branch chain into one separate two-point polyline per vertex-to-vertex
        /// segment (main/spine -> head, head -> head). Each pipe run is its own entity.
        /// </summary>
        private static void AddChainAsSegments(List<List<Point2d>> target, List<Point2d> chain)
        {
            if (chain == null || chain.Count < 2) return;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                var a = chain[i];
                var b = chain[i + 1];
                if (a.GetDistanceTo(b) > 1e-7)
                    target.Add(new List<Point2d> { a, b });
            }
        }

        private static bool SegmentInside(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            const int samples = 16;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                if (!PolygonUtils.PointInPolygon(ring, p))
                    return false;
            }
            return true;
        }
    }
}
