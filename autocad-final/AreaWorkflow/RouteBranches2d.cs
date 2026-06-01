using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Pure geometry planner for Route Branches 2.
    /// Produces strictly axis-aligned (world X/Y) polylines:
    /// - For each row/column group: main->entry sprinkler (Manhattan L) then chain along the group axis.
    /// - If some groups cannot be served directly, builds a single spine from the last served group
    ///   and serves abandoned groups from that spine.
    /// </summary>
    public static class RouteBranches2d
    {
        public readonly struct Segment2d
        {
            public readonly Point2d A;
            public readonly Point2d B;
            public Segment2d(Point2d a, Point2d b) { A = a; B = b; }
            public double Length => A.GetDistanceTo(B);
        }

        public sealed class PlanResult
        {
            public List<List<Point2d>> Paths { get; } = new List<List<Point2d>>();
        }

        private enum GroupFamily { Rows, Cols }

        private sealed class Group
        {
            public double Key; // row Y or col X
            public List<Point2d> Points = new List<Point2d>();

            public bool ServedDirectly;
            public Point2d EntrySprinkler;
            public Point2d AttachPoint;
            public List<Point2d> DirectPath; // full path including main->entry + chain
            public Point2d FarEnd;           // last point reached on chain
            public double DistanceToMain;    // ordering metric
        }

        public static PlanResult Plan(
            List<Point2d> zoneRing,
            List<Point2d> sprinklers,
            List<Segment2d> mains,
            double clusterTol)
        {
            if (zoneRing == null || zoneRing.Count < 3) return new PlanResult();
            if (sprinklers == null || sprinklers.Count == 0) return new PlanResult();
            if (mains == null || mains.Count == 0) return new PlanResult();
            if (!(clusterTol > 0)) clusterTol = 1.0;

            // Cluster.
            var rowGroups = ClusterByCoordinate(sprinklers, p => p.Y, clusterTol)
                .Select(g => new Group { Key = g.Key, Points = g.Points }).ToList();
            var colGroups = ClusterByCoordinate(sprinklers, p => p.X, clusterTol)
                .Select(g => new Group { Key = g.Key, Points = g.Points }).ToList();

            var family = ChooseFamily(rowGroups, colGroups);
            var groups = family == GroupFamily.Rows ? rowGroups : colGroups;
            if (groups.Count == 0) return new PlanResult();

            // Plan direct groups.
            foreach (var g in groups)
            {
                g.Points = family == GroupFamily.Rows
                    ? g.Points.OrderBy(p => p.X).ToList()
                    : g.Points.OrderBy(p => p.Y).ToList();

                // Entry sprinkler must be an endpoint to keep a single chain (no T).
                var candidates = new List<Point2d>();
                if (g.Points.Count >= 1) candidates.Add(g.Points[0]);
                if (g.Points.Count >= 2) candidates.Add(g.Points[g.Points.Count - 1]);

                bool planned = TryPlanDirectGroup(zoneRing, mains, family, g, candidates, out var directPath, out var attach, out var entry);
                if (!planned)
                {
                    g.ServedDirectly = false;
                    g.DirectPath = null;
                    g.DistanceToMain = EstimateGroupDistanceToMain(mains, g.Points);
                    continue;
                }

                g.ServedDirectly = true;
                g.AttachPoint = attach;
                g.EntrySprinkler = entry;
                g.DirectPath = directPath;
                g.FarEnd = directPath[directPath.Count - 1];
                g.DistanceToMain = EstimatePointDistanceToMain(mains, entry);
            }

            // Order groups by proximity to mains.
            groups = groups.OrderBy(g => g.DistanceToMain).ToList();

            // Determine last served group before the first abandoned group.
            int lastServedIdx = -1;
            int firstAbandonedIdx = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].ServedDirectly)
                    lastServedIdx = i;
                else
                {
                    firstAbandonedIdx = i;
                    break;
                }
            }

            var result = new PlanResult();

            // Add direct paths.
            foreach (var g in groups.Where(x => x.ServedDirectly && x.DirectPath != null && x.DirectPath.Count >= 2))
                result.Paths.Add(g.DirectPath);

            // Nothing abandoned -> done.
            var abandoned = groups.Where(g => !g.ServedDirectly).ToList();
            if (abandoned.Count == 0) return result;

            // No served group -> try serving everything from a single best main using the same logic.
            if (lastServedIdx < 0)
            {
                foreach (var g in abandoned)
                {
                    if (TryPlanDirectGroup(zoneRing, mains, family, g, new List<Point2d> { g.Points[0], g.Points[g.Points.Count - 1] },
                            out var directPath, out _, out _))
                    {
                        result.Paths.Add(directPath);
                    }
                }
                return result;
            }

            var pivotGroup = groups[lastServedIdx];

            // Build spine/auxiliary: when the main pipe is slanted, root the auxiliary at
            // the slanted segment's exit tip so it connects naturally to the pipe end.
            // Fall back to the last-served group's FarEnd when the main is axis-aligned.
            List<Point2d> spine = null;
            double spineAnchorKey = pivotGroup.Key;

            if (TryFindSlantedMainTip(mains, abandoned, family, out var slantedTip))
            {
                double tipKey = family == GroupFamily.Rows ? slantedTip.Y : slantedTip.X;
                if (TryBuildSpine(zoneRing, family, slantedTip, tipKey, abandoned.Select(a => a.Key).ToList(), out var slantSpine)
                    && slantSpine != null && slantSpine.Count >= 2)
                {
                    spine = slantSpine;
                    spineAnchorKey = tipKey;
                }
            }

            if (spine == null)
            {
                var pivot = pivotGroup.FarEnd;
                TryBuildSpine(zoneRing, family, pivot, pivotGroup.Key, abandoned.Select(a => a.Key).ToList(), out spine);
            }

            if (spine != null && spine.Count >= 2)
                result.Paths.Add(spine);

            // Serve abandoned groups from spine intersection.
            abandoned = abandoned.OrderBy(a => Math.Abs(a.Key - spineAnchorKey)).ToList();
            foreach (var g in abandoned)
            {
                var entry = ChooseEntryNearestToSpine(g, family, spine, clusterTol);
                if (entry == null) continue;

                Point2d spinePoint = ComputeSpineIntersectionPoint(family, spine, g.Key, entry.Value);
                if (!IsAxisAligned(spinePoint, entry.Value)) continue;
                if (!SegmentInsideRing(zoneRing, spinePoint, entry.Value)) continue;

                var chain = BuildChainFromEntry(zoneRing, family, g.Points, entry.Value);
                if (chain == null || chain.Count < 2) continue;

                var path = new List<Point2d>();
                path.Add(spinePoint);
                path.Add(entry.Value);
                for (int i = 1; i < chain.Count; i++)
                    path.Add(chain[i]);
                path = CollapseCollinear(path);
                if (path.Count >= 2)
                    result.Paths.Add(path);
            }

            return result;
        }

        public static bool IsAxisAlignedPath(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2) return false;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                if (!IsAxisAligned(pts[i], pts[i + 1]))
                    return false;
            }
            return true;
        }

        private static bool TryPlanDirectGroup(
            List<Point2d> zoneRing,
            List<Segment2d> mains,
            GroupFamily family,
            Group group,
            List<Point2d> entryCandidates,
            out List<Point2d> path,
            out Point2d attach,
            out Point2d entry)
        {
            path = null;
            attach = default;
            entry = default;

            double bestLen = double.PositiveInfinity;
            List<Point2d> best = null;
            Point2d bestAttach = default;
            Point2d bestEntry = default;

            foreach (var candidate in entryCandidates.Distinct(new Pt2dEq(1e-9)))
            {
                if (!TryFindBestAttachPath(zoneRing, mains, candidate, out var candidateAttach, out var candidatePath, out var candidateLen))
                    continue;

                // Build chain from this entry to the other end.
                var chain = BuildChainFromEntry(zoneRing, family, group.Points, candidate);
                if (chain == null || chain.Count < 1) continue;

                var combined = new List<Point2d>();
                combined.AddRange(candidatePath);
                // candidatePath ends at entry candidate (maybe via 2 segments).
                // chain starts at entry, so append from index 1.
                for (int i = 1; i < chain.Count; i++)
                    combined.Add(chain[i]);

                combined = CollapseCollinear(combined);
                if (combined.Count < 2) continue;

                double totalLen = candidateLen + PathLength(chain);
                if (totalLen < bestLen)
                {
                    bestLen = totalLen;
                    best = combined;
                    bestAttach = candidateAttach;
                    bestEntry = candidate;
                }
            }

            if (best == null) return false;
            path = best;
            attach = bestAttach;
            entry = bestEntry;
            return true;
        }

        private static List<Point2d> BuildChainFromEntry(List<Point2d> zoneRing, GroupFamily family, List<Point2d> sortedPoints, Point2d entry)
        {
            if (sortedPoints == null || sortedPoints.Count == 0) return null;

            // Ensure sorted as expected.
            var pts = family == GroupFamily.Rows ? sortedPoints.OrderBy(p => p.X).ToList() : sortedPoints.OrderBy(p => p.Y).ToList();

            int entryIdx = IndexOfPoint(pts, entry, 1e-9);
            if (entryIdx < 0) return null;

            var chain = new List<Point2d> { pts[entryIdx] };

            // For endpoint entry, direction is straightforward. If entry is not endpoint (should not happen),
            // choose the direction that keeps the chain inside the zone longer.
            if (entryIdx == 0)
            {
                for (int i = 1; i < pts.Count; i++)
                {
                    if (!IsAxisAligned(chain[chain.Count - 1], pts[i])) break;
                    if (!SegmentInsideRing(zoneRing, chain[chain.Count - 1], pts[i])) break;
                    chain.Add(pts[i]);
                }
                return chain;
            }
            if (entryIdx == pts.Count - 1)
            {
                for (int i = pts.Count - 2; i >= 0; i--)
                {
                    if (!IsAxisAligned(chain[chain.Count - 1], pts[i])) break;
                    if (!SegmentInsideRing(zoneRing, chain[chain.Count - 1], pts[i])) break;
                    chain.Add(pts[i]);
                }
                return chain;
            }

            // Non-endpoint: try forward and backward.
            var forward = new List<Point2d> { pts[entryIdx] };
            for (int i = entryIdx + 1; i < pts.Count; i++)
            {
                if (!IsAxisAligned(forward[forward.Count - 1], pts[i])) break;
                if (!SegmentInsideRing(zoneRing, forward[forward.Count - 1], pts[i])) break;
                forward.Add(pts[i]);
            }

            var backward = new List<Point2d> { pts[entryIdx] };
            for (int i = entryIdx - 1; i >= 0; i--)
            {
                if (!IsAxisAligned(backward[backward.Count - 1], pts[i])) break;
                if (!SegmentInsideRing(zoneRing, backward[backward.Count - 1], pts[i])) break;
                backward.Add(pts[i]);
            }

            return forward.Count >= backward.Count ? forward : backward;
        }

        private static bool TryBuildSpine(
            List<Point2d> zoneRing,
            GroupFamily family,
            Point2d pivot,
            double pivotKey,
            List<double> abandonedKeys,
            out List<Point2d> spine)
        {
            spine = null;
            if (abandonedKeys == null || abandonedKeys.Count == 0) return false;

            double minKey = abandonedKeys.Min();
            double maxKey = abandonedKeys.Max();

            // Extend to the farther extreme away from the pivot group line.
            double targetKey = (Math.Abs(maxKey - pivotKey) >= Math.Abs(minKey - pivotKey)) ? maxKey : minKey;

            Point2d a, b;
            if (family == GroupFamily.Rows)
            {
                // Vertical spine: keep X fixed at pivot.X, move Y.
                a = new Point2d(pivot.X, pivotKey);
                b = new Point2d(pivot.X, targetKey);
            }
            else
            {
                // Horizontal spine: keep Y fixed at pivot.Y, move X.
                a = new Point2d(pivotKey, pivot.Y);
                b = new Point2d(targetKey, pivot.Y);
            }

            if (!SegmentInsideRing(zoneRing, a, b))
                return false;

            spine = CollapseCollinear(new List<Point2d> { a, b });
            return spine.Count >= 2;
        }

        private static bool TryFindSlantedMainTip(
            List<Segment2d> mains,
            List<Group> abandoned,
            GroupFamily family,
            out Point2d tip)
        {
            tip = default;
            if (mains == null || abandoned == null || abandoned.Count == 0)
                return false;

            // Compute centroid of abandoned sprinklers to determine which endpoint of the
            // slanted segment faces the unserved zone (that endpoint is the auxiliary root).
            double sumX = 0, sumY = 0;
            int cnt = 0;
            foreach (var g in abandoned)
                foreach (var p in g.Points)
                { sumX += p.X; sumY += p.Y; cnt++; }
            if (cnt == 0) return false;
            var centroid = new Point2d(sumX / cnt, sumY / cnt);

            const double axisTol = 1e-6;
            double bestLen = 0;
            Point2d bestTip = default;
            bool found = false;

            foreach (var seg in mains)
            {
                double adx = Math.Abs(seg.B.X - seg.A.X);
                double ady = Math.Abs(seg.B.Y - seg.A.Y);
                if (adx <= axisTol || ady <= axisTol) continue; // skip axis-aligned segments
                double len = seg.Length;
                if (!(len > bestLen)) continue;
                // Exit tip = endpoint of slanted segment closest to abandoned centroid.
                double dA = seg.A.GetDistanceTo(centroid);
                double dB = seg.B.GetDistanceTo(centroid);
                bestTip = dA <= dB ? seg.A : seg.B;
                bestLen = len;
                found = true;
            }

            if (!found) return false;
            tip = bestTip;
            return true;
        }

        private static Point2d? ChooseEntryNearestToSpine(Group g, GroupFamily family, List<Point2d> spine, double tol)
        {
            if (g == null || g.Points == null || g.Points.Count == 0) return null;
            var pts = family == GroupFamily.Rows ? g.Points.OrderBy(p => p.X).ToList() : g.Points.OrderBy(p => p.Y).ToList();
            var a = pts[0];
            var b = pts[pts.Count - 1];

            if (spine == null || spine.Count < 2)
                return a;

            // For the spine segment, pick the endpoint closer to the spine's fixed coordinate.
            double spineFixed = family == GroupFamily.Rows ? spine[0].X : spine[0].Y;
            double da = family == GroupFamily.Rows ? Math.Abs(a.X - spineFixed) : Math.Abs(a.Y - spineFixed);
            double db = family == GroupFamily.Rows ? Math.Abs(b.X - spineFixed) : Math.Abs(b.Y - spineFixed);
            if (Math.Abs(da - db) <= tol) return a;
            return da <= db ? a : b;
        }

        private static Point2d ComputeSpineIntersectionPoint(GroupFamily family, List<Point2d> spine, double groupKey, Point2d entry)
        {
            // Spine is axis-aligned: for rows it's vertical (X fixed), for cols it's horizontal (Y fixed).
            if (spine == null || spine.Count < 2)
                return family == GroupFamily.Rows ? new Point2d(entry.X, groupKey) : new Point2d(groupKey, entry.Y);

            if (family == GroupFamily.Rows)
                return new Point2d(spine[0].X, groupKey);

            return new Point2d(groupKey, spine[0].Y);
        }

        private static bool TryFindBestAttachPath(
            List<Point2d> zoneRing,
            List<Segment2d> mains,
            Point2d entry,
            out Point2d attach,
            out List<Point2d> attachPath,
            out double length)
        {
            attach = default;
            attachPath = null;
            length = double.PositiveInfinity;

            double best = double.PositiveInfinity;
            Point2d bestAttach = default;
            List<Point2d> bestPath = null;

            foreach (var seg in mains)
            {
                if (!(seg.Length > 1e-9)) continue;
                var p = ClosestPointOnSegment(seg.A, seg.B, entry);

                // Try both Manhattan corners.
                var corner1 = new Point2d(p.X, entry.Y);
                var path1 = new List<Point2d> { p, corner1, entry };
                path1 = CollapseCollinear(path1);
                if (IsAxisAlignedPath(path1) && PathInside(zoneRing, path1))
                {
                    double len1 = PathLength(path1);
                    if (len1 < best)
                    {
                        best = len1;
                        bestAttach = p;
                        bestPath = path1;
                    }
                }

                var corner2 = new Point2d(entry.X, p.Y);
                var path2 = new List<Point2d> { p, corner2, entry };
                path2 = CollapseCollinear(path2);
                if (IsAxisAlignedPath(path2) && PathInside(zoneRing, path2))
                {
                    double len2 = PathLength(path2);
                    if (len2 < best)
                    {
                        best = len2;
                        bestAttach = p;
                        bestPath = path2;
                    }
                }
            }

            if (bestPath == null) return false;
            attach = bestAttach;
            attachPath = bestPath;
            length = best;
            return true;
        }

        private static double EstimateGroupDistanceToMain(List<Segment2d> mains, List<Point2d> pts)
        {
            if (pts == null || pts.Count == 0) return double.PositiveInfinity;
            double best = double.PositiveInfinity;
            foreach (var p in pts)
                best = Math.Min(best, EstimatePointDistanceToMain(mains, p));
            return best;
        }

        private static double EstimatePointDistanceToMain(List<Segment2d> mains, Point2d p)
        {
            double best = double.PositiveInfinity;
            foreach (var seg in mains)
            {
                var c = ClosestPointOnSegment(seg.A, seg.B, p);
                double d = Math.Abs(c.X - p.X) + Math.Abs(c.Y - p.Y); // Manhattan
                if (d < best) best = d;
            }
            return best;
        }

        private static int IndexOfPoint(List<Point2d> pts, Point2d target, double tol)
        {
            if (pts == null) return -1;
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].GetDistanceTo(target) <= tol)
                    return i;
            return -1;
        }

        private static GroupFamily ChooseFamily(List<Group> rows, List<Group> cols)
        {
            // Prefer fewer groups and larger average group size.
            double rowScore = ScoreGrouping(rows);
            double colScore = ScoreGrouping(cols);
            return colScore < rowScore ? GroupFamily.Cols : GroupFamily.Rows;
        }

        private static double ScoreGrouping(List<Group> groups)
        {
            if (groups == null || groups.Count == 0) return double.PositiveInfinity;
            double avg = groups.Average(g => (double)(g.Points?.Count ?? 0));
            // Lower is better.
            return groups.Count - avg;
        }

        private sealed class ClusterGroup
        {
            public double Key;
            public List<Point2d> Points = new List<Point2d>();
        }

        private static List<ClusterGroup> ClusterByCoordinate(List<Point2d> pts, Func<Point2d, double> key, double tol)
        {
            var result = new List<ClusterGroup>();
            if (pts == null || pts.Count == 0) return result;
            if (!(tol > 0)) tol = 1.0;

            var sorted = pts.OrderBy(p => key(p)).ToList();
            ClusterGroup current = null;
            foreach (var p in sorted)
            {
                double k = key(p);
                if (current == null)
                {
                    current = new ClusterGroup { Key = k };
                    current.Points.Add(p);
                    result.Add(current);
                    continue;
                }

                if (Math.Abs(k - current.Key) <= tol)
                {
                    current.Points.Add(p);
                    // keep key as running average for stability
                    current.Key = current.Points.Average(pp => key(pp));
                }
                else
                {
                    current = new ClusterGroup { Key = k };
                    current.Points.Add(p);
                    result.Add(current);
                }
            }
            return result;
        }

        private static bool PathInside(List<Point2d> zoneRing, List<Point2d> path)
        {
            if (path == null || path.Count < 2) return false;
            for (int i = 0; i + 1 < path.Count; i++)
                if (!SegmentInsideRing(zoneRing, path[i], path[i + 1]))
                    return false;
            return true;
        }

        private static bool SegmentInsideRing(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            if (ring == null || ring.Count < 3) return false;
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

        private static bool IsAxisAligned(Point2d a, Point2d b)
        {
            const double tol = 1e-9;
            return Math.Abs(a.X - b.X) <= tol || Math.Abs(a.Y - b.Y) <= tol;
        }

        private static Point2d ClosestPointOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }

        private static List<Point2d> CollapseCollinear(List<Point2d> verts)
        {
            if (verts == null || verts.Count < 2) return verts ?? new List<Point2d>();
            var res = new List<Point2d>();
            res.Add(verts[0]);
            for (int i = 1; i < verts.Count; i++)
            {
                var prev = res[res.Count - 1];
                if (prev.GetDistanceTo(verts[i]) > 1e-9)
                    res.Add(verts[i]);
            }

            if (res.Count <= 2) return res;

            // Remove redundant middle points where direction doesn't change (axis-aligned only).
            var cleaned = new List<Point2d> { res[0] };
            for (int i = 1; i + 1 < res.Count; i++)
            {
                var a = cleaned[cleaned.Count - 1];
                var b = res[i];
                var c = res[i + 1];
                bool abH = Math.Abs(a.Y - b.Y) <= 1e-9;
                bool bcH = Math.Abs(b.Y - c.Y) <= 1e-9;
                bool abV = Math.Abs(a.X - b.X) <= 1e-9;
                bool bcV = Math.Abs(b.X - c.X) <= 1e-9;
                if ((abH && bcH) || (abV && bcV))
                    continue;
                cleaned.Add(b);
            }
            cleaned.Add(res[res.Count - 1]);
            return cleaned;
        }

        private static double PathLength(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2) return 0;
            double sum = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
                sum += pts[i].GetDistanceTo(pts[i + 1]);
            return sum;
        }

        private sealed class Pt2dEq : IEqualityComparer<Point2d>
        {
            private readonly double _tol;
            public Pt2dEq(double tol) { _tol = tol; }
            public bool Equals(Point2d a, Point2d b) => a.GetDistanceTo(b) <= _tol;
            public int GetHashCode(Point2d obj)
            {
                // Hash by rounding to tolerance grid.
                double t = _tol > 0 ? _tol : 1e-9;
                long xi = (long)Math.Round(obj.X / t);
                long yi = (long)Math.Round(obj.Y / t);
                return unchecked((int)(xi * 397) ^ (int)yi);
            }
        }
    }
}

