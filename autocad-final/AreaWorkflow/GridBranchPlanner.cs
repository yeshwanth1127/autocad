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
    /// sub-main "spine" that starts at the main end nearest them and threads through one entry head
    /// per line - so the spine may be slanted/curved and stays inside round or slanted zones. Every
    /// emitted segment is validated against the zone ring; a branch is never drawn without a feeder.
    /// </summary>
    public static class GridBranchPlanner
    {
        public sealed class Result
        {
            public List<List<Point2d>> Branches { get; } = new List<List<Point2d>>();
            public List<List<Point2d>> Spines { get; } = new List<List<Point2d>>();

            /// <summary>
            /// Structured laterals matching the drawn branches: one per side of each tap. Used by
            /// reducer placement so reducers sit on, and align with, the actual branch segments.
            /// </summary>
            public List<PlannedLateral> Laterals { get; } = new List<PlannedLateral>();
        }

        /// <summary>One run of a branch: a feeder tap and the heads chained outward from it.</summary>
        public sealed class PlannedLateral
        {
            public Point2d Attach;                       // tap on the main or spine
            public List<Point2d> Heads = new List<Point2d>(); // ordered nearest-tap -> tip
            public Vector2d Axis;                        // unit progression direction along the run
            public Vector2d FeedAxis;                    // direction of the feeder (main/spine) at the tap
        }

        private sealed class BranchLine
        {
            public double Perp;            // gridline coordinate (X for vertical lines, Y for horizontal)
            public List<double> Alongs;    // head positions along the line, ascending
        }

        public static Result Plan(List<Point2d> zoneRing, List<Point2d> sprinklers, List<(Point2d A, Point2d B)> mainSegments)
        {
            var result = new Result();
            if (zoneRing == null || zoneRing.Count < 3) return result;
            if (sprinklers == null || sprinklers.Count == 0) return result;
            if (mainSegments == null || mainSegments.Count == 0) return result;

            // Flat list of every main endpoint, used for orientation and spine anchoring.
            var mainPoints = new List<Point2d>();
            foreach (var s in mainSegments) { mainPoints.Add(s.A); mainPoints.Add(s.B); }

            // 1. Branch axis. Horizontal main -> vertical branch lines (run along Y, share X).
            bool branchAlongY = MainIsHorizontal(mainPoints);

            Func<Point2d, double> perp = p => branchAlongY ? p.X : p.Y;   // gridline coordinate
            Func<Point2d, double> along = p => branchAlongY ? p.Y : p.X;  // position on the line
            Func<double, double, Point2d> make = (perpC, alongC) =>
                branchAlongY ? new Point2d(perpC, alongC) : new Point2d(alongC, perpC);

            // 2. Cluster heads into branch lines by their gridline coordinate.
            var lines = ClusterLines(sprinklers, perp, along);
            if (lines.Count == 0) return result;

            var unreached = new List<BranchLine>();

            // 3-4. Lines a main run crosses inside the zone are drawn directly. When more than one
            // main run crosses a line (e.g. the flat trunk low and a slanted extension high), tap
            // the crossing nearest that line's heads so the branch is short and stays in-zone.
            foreach (var line in lines)
            {
                double headCenter = line.Alongs.Count > 0 ? line.Alongs.Average() : 0.0;
                bool hasTap = false;
                double tapAlong = 0.0;
                Vector2d feedAxis = default;
                double bestDist = double.MaxValue;

                foreach (var cross in MainCrossingsAtPerp(mainSegments, line.Perp, branchAlongY))
                {
                    if (!PolygonUtils.PointInPolygon(zoneRing, cross.Hit)) continue;
                    double d = Math.Abs(along(cross.Hit) - headCenter);
                    if (d < bestDist) { bestDist = d; tapAlong = along(cross.Hit); feedAxis = cross.Dir; hasTap = true; }
                }

                if (hasTap)
                {
                    var branch = BuildStraightLine(zoneRing, line.Perp, line.Alongs, tapAlong, make, out List<double> leftover);
                    AddChainAsSegments(result.Branches, branch);
                    EmitLaterals(result, branch, tapAlong, branchAlongY, feedAxis);
                    if (leftover != null && leftover.Count > 0)
                        unreached.Add(new BranchLine { Perp = line.Perp, Alongs = leftover });
                }
                else
                {
                    unreached.Add(line);
                }
            }

            // 5. Serve unreached lines with spines - one per spatial cluster of lines, each anchored
            // at the main end nearest it - iterating so heads clipped off a spine branch get re-served.
            int guard = 0;
            while (unreached.Count > 0 && guard++ < 12)
            {
                var still = new List<BranchLine>();
                foreach (var cluster in ClusterLinesByPerpGap(unreached))
                    BuildSpineForSide(result, zoneRing, mainPoints, cluster, perp, along, make, branchAlongY, still);
                if (still.Count >= unreached.Count) { unreached = still; break; } // no progress
                unreached = still;
            }

            // 6. Final mop-up: attach any head still unserved to the nearest in-zone point already on
            // the network with a short (possibly slanted) connector, so every reachable head is fed.
            if (unreached.Count > 0)
                MopUpUnserved(result, zoneRing, unreached, make);

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
        /// All points where the gridline (perp == c) crosses any main segment. A line may be
        /// crossed by several main runs (trunk + extension); the caller picks the nearest.
        /// </summary>
        private static List<(Point2d Hit, Vector2d Dir)> MainCrossingsAtPerp(List<(Point2d A, Point2d B)> segs, double c, bool branchAlongY)
        {
            var hits = new List<(Point2d, Vector2d)>();
            foreach (var s in segs)
            {
                double ca = branchAlongY ? s.A.X : s.A.Y;
                double cb = branchAlongY ? s.B.X : s.B.Y;
                double lo = Math.Min(ca, cb);
                double hi = Math.Max(ca, cb);
                if (c < lo - 1e-9 || c > hi + 1e-9) continue;

                double denom = cb - ca;
                double t = Math.Abs(denom) < 1e-12 ? 0.0 : (c - ca) / denom;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                var hit = new Point2d(s.A.X + t * (s.B.X - s.A.X), s.A.Y + t * (s.B.Y - s.A.Y));
                var dir = new Vector2d(s.B.X - s.A.X, s.B.Y - s.A.Y);
                if (dir.Length > 1e-9) dir = dir.GetNormal();
                hits.Add((hit, dir));
            }
            return hits;
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

        /// <summary>
        /// Serves a group of unreached branch lines with ONE spine that starts at the end of the
        /// main nearest them (the end that can no longer serve) and threads through one entry head
        /// per line. The spine follows the heads, so it can be slanted/curved and stays inside the
        /// zone for any boundary shape (round, slanted). Each line then chains its heads off its
        /// entry head, which lies on the spine. A line is only served if it can be reached by an
        /// in-zone spine segment - branches are never emitted without a feeder.
        /// </summary>
        private static void BuildSpineForSide(Result result, List<Point2d> zoneRing, List<Point2d> mainPoints, List<BranchLine> sideLines, Func<Point2d, double> perp, Func<Point2d, double> along, Func<double, double, Point2d> make, bool branchAlongY, List<BranchLine> stillUnserved)
        {
            if (sideLines == null || sideLines.Count == 0 || mainPoints == null || mainPoints.Count == 0) return;

            // Centroid of the unreached heads on this side.
            double sx = 0, sy = 0; int cnt = 0;
            foreach (var l in sideLines)
                foreach (var a in l.Alongs) { var p = make(l.Perp, a); sx += p.X; sy += p.Y; cnt++; }
            if (cnt == 0) return;
            var centroid = new Point2d(sx / cnt, sy / cnt);

            // E = the main endpoint nearest the unreached cluster (where the main stops serving).
            Point2d e = mainPoints[0];
            double best = double.MaxValue;
            foreach (var p in mainPoints)
            {
                double d = p.GetDistanceTo(centroid);
                if (d < best) { best = d; e = p; }
            }

            // Grow the spine away from E: order lines by gridline coordinate, starting nearest E.
            var cols = sideLines.OrderBy(l => l.Perp).ToList();
            double ePerp = perp(e);
            if (Math.Abs(cols[0].Perp - ePerp) > Math.Abs(cols[cols.Count - 1].Perp - ePerp))
                cols.Reverse();

            var spinePts = new List<Point2d> { e };
            Point2d prev = e;
            foreach (var col in cols)
            {
                if (col.Alongs == null || col.Alongs.Count == 0) continue;

                // Entry head of this column = the head nearest the current spine end.
                double entryAlong = col.Alongs[0];
                double bd = double.MaxValue;
                foreach (var a in col.Alongs)
                {
                    double d = make(col.Perp, a).GetDistanceTo(prev);
                    if (d < bd) { bd = d; entryAlong = a; }
                }
                var entry = make(col.Perp, entryAlong);

                // Only extend the spine if the connecting run stays inside the zone; otherwise this
                // column can't be reached by this spine - defer it (the mop-up pass will handle it).
                if (!SegmentInside(zoneRing, prev, entry)) { stillUnserved.Add(col); continue; }

                // Spine direction at this tap (feeder direction for the lateral), captured before
                // advancing the spine cursor.
                var spineDir = new Vector2d(entry.X - prev.X, entry.Y - prev.Y);
                if (spineDir.Length > 1e-9) spineDir = spineDir.GetNormal();

                spinePts.Add(entry);
                prev = entry;

                // Chain the column's heads off the entry head (which sits on the spine). Any head the
                // straight branch can't reach in-zone is deferred rather than dropped.
                var branch = BuildStraightLine(zoneRing, col.Perp, col.Alongs, entryAlong, make, out List<double> leftover);
                AddChainAsSegments(result.Branches, branch);
                EmitLaterals(result, branch, entryAlong, branchAlongY, spineDir);
                if (leftover != null && leftover.Count > 0)
                    stillUnserved.Add(new BranchLine { Perp = col.Perp, Alongs = leftover });
            }

            if (spinePts.Count >= 2)
                AddChainAsSegments(result.Spines, spinePts);
        }

        /// <summary>
        /// Groups unreached lines into spatially contiguous clusters by gaps in their gridline
        /// coordinate, so lines on opposite sides of the served region (or in separate pockets)
        /// each get their own spine instead of being split by an arbitrary midpoint.
        /// </summary>
        private static List<List<BranchLine>> ClusterLinesByPerpGap(List<BranchLine> lines)
        {
            var clusters = new List<List<BranchLine>>();
            if (lines == null || lines.Count == 0) return clusters;

            var sorted = lines.OrderBy(l => l.Perp).ToList();
            if (sorted.Count == 1) { clusters.Add(new List<BranchLine> { sorted[0] }); return clusters; }

            var gaps = new List<double>();
            for (int i = 0; i + 1 < sorted.Count; i++)
            {
                double g = sorted[i + 1].Perp - sorted[i].Perp;
                if (g > 1e-9) gaps.Add(g);
            }
            double median = 1.0;
            if (gaps.Count > 0)
            {
                var gs = gaps.OrderBy(x => x).ToList();
                median = gs[gs.Count / 2];
            }
            double threshold = Math.Max(median * 3.0, 1e-6);

            var current = new List<BranchLine> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Perp - sorted[i - 1].Perp > threshold)
                {
                    clusters.Add(current);
                    current = new List<BranchLine>();
                }
                current.Add(sorted[i]);
            }
            if (current.Count > 0) clusters.Add(current);
            return clusters;
        }

        /// <summary>
        /// Connects any head that no spine could reach to the nearest point already on the network
        /// (branch or spine) with a single in-zone connector. Guarantees coverage wherever a direct
        /// in-zone link exists; heads with no in-zone link are left unserved rather than orphaned.
        /// </summary>
        private static void MopUpUnserved(Result result, List<Point2d> zoneRing, List<BranchLine> lines, Func<double, double, Point2d> make)
        {
            var net = new List<Point2d>();
            foreach (var seg in result.Branches) net.AddRange(seg);
            foreach (var seg in result.Spines) net.AddRange(seg);
            if (net.Count == 0) return;

            foreach (var line in lines)
            {
                if (line.Alongs == null) continue;
                foreach (double a in line.Alongs)
                {
                    var head = make(line.Perp, a);
                    foreach (var p in net.OrderBy(q => q.GetDistanceTo(head)))
                    {
                        if (p.GetDistanceTo(head) < 1e-7) break; // already on the network
                        if (SegmentInside(zoneRing, p, head))
                        {
                            result.Branches.Add(new List<Point2d> { p, head });
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Records the structured laterals for a drawn branch chain: the tap splits the chain into
        /// (up to) two runs, each emitted as a lateral whose heads are ordered outward from the tap.
        /// Reducer placement consumes these so reducers sit on the real drawn segments.
        /// </summary>
        private static void EmitLaterals(Result result, List<Point2d> chain, double tapAlong, bool branchAlongY, Vector2d feedAxis)
        {
            if (chain == null || chain.Count < 2) return;
            Func<Point2d, double> alongOf = p => branchAlongY ? p.Y : p.X;

            // Tap vertex on the chain (the point at the feeder).
            Point2d tap = chain[0];
            double bd = double.MaxValue;
            foreach (var v in chain)
            {
                double d = Math.Abs(alongOf(v) - tapAlong);
                if (d < bd) { bd = d; tap = v; }
            }

            var low = new List<Point2d>();
            var high = new List<Point2d>();
            foreach (var v in chain)
            {
                double a = alongOf(v);
                if (Math.Abs(a - tapAlong) <= 1e-6) continue; // skip the tap (and any head on it)
                if (a < tapAlong) low.Add(v); else high.Add(v);
            }
            low.Sort((p, q) => alongOf(q).CompareTo(alongOf(p)));  // nearest the tap first
            high.Sort((p, q) => alongOf(p).CompareTo(alongOf(q)));

            if (low.Count > 0)
                result.Laterals.Add(new PlannedLateral { Attach = tap, Heads = low, Axis = branchAlongY ? new Vector2d(0, -1) : new Vector2d(-1, 0), FeedAxis = feedAxis });
            if (high.Count > 0)
                result.Laterals.Add(new PlannedLateral { Attach = tap, Heads = high, Axis = branchAlongY ? new Vector2d(0, 1) : new Vector2d(1, 0), FeedAxis = feedAxis });
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
