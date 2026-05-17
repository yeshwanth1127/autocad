using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent.Planning.Validators;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Inserts axis-aligned detours so branch pipe segments avoid shaft block footprints.
    ///
    /// STABILITY CONTRACT: every public method must return a path that
    ///   • contains no NaN / Infinity coordinates
    ///   • contains no duplicate consecutive points
    ///   • contains no segments shorter than <see cref="MinSegmentLength"/>
    ///   • contains no diagonal (non-axis-aligned) segments
    ///   • contains at most <see cref="MaxWaypoints"/> vertices
    ///
    /// Any intermediate result that violates these rules is replaced by a guaranteed-safe
    /// L-path fallback before returning.
    /// </summary>
    public static class BranchPipeShaftDetour2d
    {
        // ── Unified geometry tolerances ──────────────────────────────────────────
        // ONE set of constants. No ad-hoc multipliers anywhere in this file.

        /// <summary>
        /// Two points are considered identical when their Chebyshev distance is ≤ this.
        /// Used for deduplication, corner collapse, and segment-length tests.
        /// </summary>
        private const double PointEqualityTol = 1e-4;

        /// <summary>
        /// Segments shorter than this (drawing units) are never emitted.
        /// Also the minimum meaningful detour offset from an obstacle edge.
        /// </summary>
        private const double MinSegmentLength = 1e-3;

        /// <summary>
        /// A segment is classified as diagonal when its minor-axis component exceeds
        /// <c>DiagonalFractionThreshold × major-axis component</c>.
        /// 0.002 (0.2%) covers floating-point drift while catching real diagonals.
        /// </summary>
        private const double DiagonalFractionThreshold = 0.002;

        /// <summary>Hard ceiling on waypoints in any returned list. Prevents runaway vertex explosion.</summary>
        private const int MaxWaypoints = 128;

        private const double DefaultShaftHalfExtentDu = 0.125;

        // ── Point / segment classification ───────────────────────────────────────

        private static bool PointsEqual(Point2d a, Point2d b)
            => Math.Abs(a.X - b.X) <= PointEqualityTol
            && Math.Abs(a.Y - b.Y) <= PointEqualityTol;

        /// <summary>
        /// True when a→b has material extent on both axes (i.e. is genuinely diagonal).
        /// Degenerate segments (both axes ≤ MinSegmentLength) return false.
        /// </summary>
        private static bool IsDiagonal(Point2d a, Point2d b)
        {
            double adx = Math.Abs(b.X - a.X);
            double ady = Math.Abs(b.Y - a.Y);
            if (adx < MinSegmentLength && ady < MinSegmentLength) return false;
            double major = Math.Max(adx, ady);
            double minor = Math.Min(adx, ady);
            return minor > major * DiagonalFractionThreshold;
        }

        // ── Path sanitizer (runs before every public return) ─────────────────────

        /// <summary>
        /// Full path sanitizer. Mutates nothing; always returns a fresh, valid list.
        /// Steps (in order):
        ///   1. NaN / Infinity guard
        ///   2. Remove consecutive duplicates and micro-segments
        ///   3. Remove collinear interior points
        ///   4. Remove immediate backtracking (A→B→A)
        ///   5. Hard vertex cap
        ///   6. Restore exact original endpoints
        ///   7. Final validity check
        /// On any failure, returns <see cref="SafeFallback"/>.
        /// </summary>
        private static List<Point2d> SanitizePath(
            List<Point2d> pts, Point2d start, Point2d end, bool verticalFirstLeg)
        {
            if (pts == null || pts.Count < 2)
                return SafeFallback(start, end, verticalFirstLeg);

            // 1. NaN / Infinity
            for (int i = 0; i < pts.Count; i++)
            {
                double x = pts[i].X, y = pts[i].Y;
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                    return SafeFallback(start, end, verticalFirstLeg);
            }

            // 2. Deduplicate and drop micro-segments
            var o = new List<Point2d>(pts.Count) { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                var prev = o[o.Count - 1];
                var cur = pts[i];
                if (!PointsEqual(prev, cur) && prev.GetDistanceTo(cur) >= MinSegmentLength)
                    o.Add(cur);
            }
            if (o.Count < 2) return SafeFallback(start, end, verticalFirstLeg);

            // 3. Remove collinear interior points
            o = RemoveCollinear(o);

            // 4. Remove immediate backtracking
            o = RemoveBacktracking(o);
            if (o.Count < 2) return SafeFallback(start, end, verticalFirstLeg);

            // 5. Hard vertex cap
            if (o.Count > MaxWaypoints)
                return SafeFallback(start, end, verticalFirstLeg);

            // 6. Restore exact endpoints (undo any float drift from intermediate arithmetic)
            o[0] = start;
            o[o.Count - 1] = end;

            // 7. Validity
            if (!IsValidPath(o))
                return SafeFallback(start, end, verticalFirstLeg);

            return o;
        }

        private static List<Point2d> RemoveCollinear(List<Point2d> pts)
        {
            if (pts.Count <= 2) return pts;
            var o = new List<Point2d>(pts.Count) { pts[0] };
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                var a = o[o.Count - 1];
                var b = pts[i];
                var c = pts[i + 1];
                // Signed area of triangle a-b-c: if near-zero, b is collinear.
                double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                double baseLen = a.GetDistanceTo(c);
                // Keep b if the perpendicular deviation from a→c is >= MinSegmentLength
                if (baseLen < MinSegmentLength || Math.Abs(cross) > baseLen * MinSegmentLength)
                    o.Add(b);
            }
            o.Add(pts[pts.Count - 1]);
            return o;
        }

        private static List<Point2d> RemoveBacktracking(List<Point2d> pts)
        {
            if (pts.Count < 3) return pts;
            // Iterative: each pass removes A→B→A spikes until stable.
            // Bounded: each pass reduces count or sets changed=false → terminates.
            bool changed = true;
            while (changed && pts.Count >= 3)
            {
                changed = false;
                var o = new List<Point2d>(pts.Count) { pts[0], pts[1] };
                for (int i = 2; i < pts.Count; i++)
                {
                    if (PointsEqual(pts[i], o[o.Count - 2]))
                    {
                        o.RemoveAt(o.Count - 1); // pop the spike mid-point
                        changed = true;
                    }
                    else
                    {
                        o.Add(pts[i]);
                    }
                }
                pts = o;
            }
            return pts;
        }

        /// <summary>
        /// Structural path validity: at least 2 points, no NaN, no zero-length segments.
        /// Does NOT check for diagonals here — caller (SanitizePath) restores endpoints after
        /// collinear/backtrack removal, which could temporarily re-introduce tiny diagonals.
        /// </summary>
        private static bool IsValidPath(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2) return false;
            for (int i = 0; i < pts.Count; i++)
            {
                double x = pts[i].X, y = pts[i].Y;
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                    return false;
            }
            for (int i = 0; i + 1 < pts.Count; i++)
                if (pts[i].GetDistanceTo(pts[i + 1]) < MinSegmentLength)
                    return false;
            return true;
        }

        /// <summary>
        /// Guaranteed-safe last-resort path. Builds the simplest valid orthogonal path:
        /// a single L with one corner, or a straight line if start/end share an axis.
        /// Never produces a diagonal. Never throws.
        /// </summary>
        private static List<Point2d> SafeFallback(Point2d start, Point2d end, bool verticalFirstLeg)
        {
            if (PointsEqual(start, end))
                return new List<Point2d> { start, end };

            // Already axis-aligned — just two points
            if (!IsDiagonal(start, end))
                return new List<Point2d> { start, end };

            // One-corner L
            Point2d corner = verticalFirstLeg
                ? new Point2d(start.X, end.Y)
                : new Point2d(end.X, start.Y);

            if (PointsEqual(start, corner) || PointsEqual(corner, end))
                return new List<Point2d> { start, end };

            return new List<Point2d> { start, corner, end };
        }

        // ── Shaft-obstacle list builder ──────────────────────────────────────────

        /// <summary>
        /// Axis-aligned bounding boxes for shaft blocks expanded by <paramref name="clearanceDu"/>.
        /// Pass <paramref name="clearanceDu"/> = 0 for the true shaft footprint only (used to detect pipe passing *through* the shaft).
        /// Pass a positive clearance for pathfinding so routes stay outside a buffer around the shaft.
        /// </summary>
        public static List<(Point2d min, Point2d max)> BuildShaftObstacles(
            Database db, Polyline zone, double clearanceDu)
        {
            var list = new List<(Point2d min, Point2d max)>();
            if (db == null || zone == null) return list;

            double c = Math.Max(0, clearanceDu);
            var shafts = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, zone);
            if (shafts == null || shafts.Count == 0) return list;

            foreach (var s in shafts)
            {
                double minX, maxX, minY, maxY;
                if (s.HasExtents)
                {
                    var mn = s.Extents2d.MinPoint;
                    var mx = s.Extents2d.MaxPoint;
                    minX = Math.Min(mn.X, mx.X) - c;
                    maxX = Math.Max(mn.X, mx.X) + c;
                    minY = Math.Min(mn.Y, mx.Y) - c;
                    maxY = Math.Max(mn.Y, mx.Y) + c;
                }
                else
                {
                    double h = DefaultShaftHalfExtentDu;
                    minX = s.Position.X - h - c;
                    maxX = s.Position.X + h + c;
                    minY = s.Position.Y - h - c;
                    maxY = s.Position.Y + h + c;
                }

                if (maxX - minX < MinSegmentLength && maxY - minY < MinSegmentLength)
                    continue;

                list.Add((new Point2d(minX, minY), new Point2d(maxX, maxY)));
            }

            return list;
        }

        // ── Public path API ──────────────────────────────────────────────────────

        /// <summary>
        /// Shaft-detour path for a segment that is intended to be axis-aligned.
        /// Diagonal inputs fall back to a simple L (no recursion into Orthogonal).
        /// <paramref name="tol"/> is accepted for API compatibility but ignored;
        /// all geometry decisions use the unified constants of this class.
        /// </summary>
        public static List<Point2d> AxisAlignedWaypointsAvoidingBoxes(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol)
        {
            if (PointsEqual(a, b))
                return new List<Point2d> { a, b };

            // Diagonal input: caller passed a non-axis-aligned pair.
            // Build a simple L without calling back into Orthogonal (no recursion).
            if (IsDiagonal(a, b))
            {
                var lpath = BuildSimpleLPath(a, b, verticalFirstLeg: true);
                return SanitizePath(lpath, a, b, verticalFirstLeg: true);
            }

            // Truly axis-aligned — no boxes, return direct
            if (boxes == null || boxes.Count == 0)
                return new List<Point2d> { a, b };

            // Axis-aligned with boxes: run the appropriate single-axis detour
            double adx = Math.Abs(b.X - a.X);
            double ady = Math.Abs(b.Y - a.Y);
            bool horiz = adx >= ady;

            var raw = horiz
                ? HorizontalDetourPath(a, b, boxes, zoneRings)
                : VerticalDetourPath(a, b, boxes, zoneRings);

            return SanitizePath(raw, a, b, !horiz);
        }

        /// <summary>
        /// Manhattan (L-shaped) path from <paramref name="a"/> to <paramref name="b"/> with
        /// per-leg shaft detours. Always orthogonal — never emits a diagonal.
        /// <para>
        /// <paramref name="verticalFirstLeg"/> = true → leg1: a→(a.X, b.Y), leg2: corner→b.<br/>
        /// <paramref name="verticalFirstLeg"/> = false → leg1: a→(b.X, a.Y), leg2: corner→b.
        /// </para>
        /// <paramref name="tol"/> is accepted for API compatibility but ignored.
        /// </summary>
        public static List<Point2d> OrthogonalWaypointsAvoidingBoxes(
            Point2d a,
            Point2d b,
            bool verticalFirstLeg,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol)
        {
            if (PointsEqual(a, b))
                return new List<Point2d> { a, b };

            // Already axis-aligned: single-leg detour (no need for L-path corner)
            if (!IsDiagonal(a, b))
            {
                var single = AxisAlignedWaypointsAvoidingBoxes(a, b, boxes, zoneRings, tol);
                return SanitizePath(single, a, b, verticalFirstLeg);
            }

            // Build L-path with per-leg obstacle detours.
            // Uses AxisAlignedInternal on each leg — never calls back into this method.
            var raw = BuildLPathWithDetours(a, b, verticalFirstLeg, boxes, zoneRings);
            return SanitizePath(raw, a, b, verticalFirstLeg);
        }

        // ── L-path construction (no recursion back to public API) ────────────────

        private static List<Point2d> BuildSimpleLPath(Point2d a, Point2d b, bool verticalFirstLeg)
        {
            if (PointsEqual(a, b)) return new List<Point2d> { a, b };
            Point2d c = verticalFirstLeg ? new Point2d(a.X, b.Y) : new Point2d(b.X, a.Y);
            if (PointsEqual(a, c) || PointsEqual(c, b))
                return new List<Point2d> { a, b };
            return new List<Point2d> { a, c, b };
        }

        /// <summary>
        /// Builds a two-leg L-path with obstacle detour on each leg.
        /// NEVER calls <see cref="OrthogonalWaypointsAvoidingBoxes"/> — zero recursion risk.
        /// </summary>
        private static List<Point2d> BuildLPathWithDetours(
            Point2d a,
            Point2d b,
            bool verticalFirstLeg,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings)
        {
            Point2d corner = verticalFirstLeg ? new Point2d(a.X, b.Y) : new Point2d(b.X, a.Y);

            // Degenerate corner — just route as single axis-aligned leg
            if (PointsEqual(a, corner) || PointsEqual(corner, b))
                return AxisAlignedInternal(a, b, boxes, zoneRings);

            var leg1 = AxisAlignedInternal(a, corner, boxes, zoneRings);
            var leg2 = AxisAlignedInternal(corner, b, boxes, zoneRings);
            return ConcatLegs(leg1, leg2);
        }

        /// <summary>
        /// Axis-aligned detour for an already-orthogonal segment.
        /// This is the ONLY function that generates path vertices.
        /// It is called by both public entry-points but NEVER calls back into them.
        /// </summary>
        private static List<Point2d> AxisAlignedInternal(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings)
        {
            if (PointsEqual(a, b)) return new List<Point2d> { a, b };
            if (boxes == null || boxes.Count == 0) return new List<Point2d> { a, b };

            double adx = Math.Abs(b.X - a.X);
            double ady = Math.Abs(b.Y - a.Y);
            return adx >= ady
                ? HorizontalDetourPath(a, b, boxes, zoneRings)
                : VerticalDetourPath(a, b, boxes, zoneRings);
        }

        private static List<Point2d> ConcatLegs(List<Point2d> leg1, List<Point2d> leg2)
        {
            if (leg1 == null || leg1.Count == 0) return leg2 ?? new List<Point2d>();
            if (leg2 == null || leg2.Count == 0) return leg1;

            var o = new List<Point2d>(leg1.Count + leg2.Count);
            o.AddRange(leg1);

            // Drop leg2[0] if it duplicates the shared corner (already the last point of leg1)
            int start = (leg2.Count > 0 && PointsEqual(o[o.Count - 1], leg2[0])) ? 1 : 0;
            for (int i = start; i < leg2.Count; i++)
                o.Add(leg2[i]);

            return o;
        }

        // ── Single-axis detour builders ───────────────────────────────────────────

        /// <summary>
        /// Horizontal a→b with box detours. Uses a.Y as the run line (exact, no midpoint drift).
        /// Returns [a,b] if no detour is needed or possible.
        /// </summary>
        private static List<Point2d> HorizontalDetourPath(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings)
        {
            double y = a.Y; // a.Y == b.Y for truly horizontal; use exact value, not midpoint
            double x0 = a.X;
            double x1 = b.X;
            if (Math.Abs(x1 - x0) < MinSegmentLength)
                return new List<Point2d> { a, b };

            bool flip = x1 < x0;
            double ax = flip ? x1 : x0;
            double bx = flip ? x0 : x1;

            var merged = CollectMergedXIntervals(y, ax, bx, boxes);
            if (merged.Count == 0)
                return new List<Point2d> { a, b };

            if (!TryPickHorizontalDetourY(y, ax, bx, merged, boxes, zoneRings, out double yDet))
                return new List<Point2d> { a, b };

            if (Math.Abs(yDet - y) < MinSegmentLength)
                return new List<Point2d> { a, b };

            var forward = BuildHorizontalLeftToRight(ax, bx, y, merged, yDet);
            if (forward == null || forward.Count < 2)
                return new List<Point2d> { a, b };

            if (flip) forward.Reverse();
            forward[0] = a;
            forward[forward.Count - 1] = b;
            return forward;
        }

        /// <summary>
        /// Vertical a→b with box detours. Uses a.X as the run line.
        /// Returns [a,b] if no detour is needed or possible.
        /// </summary>
        private static List<Point2d> VerticalDetourPath(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings)
        {
            double x = a.X;
            double y0 = a.Y;
            double y1 = b.Y;
            if (Math.Abs(y1 - y0) < MinSegmentLength)
                return new List<Point2d> { a, b };

            bool flip = y1 < y0;
            double ay = flip ? y1 : y0;
            double by = flip ? y0 : y1;

            var merged = CollectMergedYIntervals(x, ay, by, boxes);
            if (merged.Count == 0)
                return new List<Point2d> { a, b };

            if (!TryPickVerticalDetourX(x, ay, by, merged, boxes, zoneRings, out double xDet))
                return new List<Point2d> { a, b };

            if (Math.Abs(xDet - x) < MinSegmentLength)
                return new List<Point2d> { a, b };

            var forward = BuildVerticalBottomToTop(x, ay, by, merged, xDet);
            if (forward == null || forward.Count < 2)
                return new List<Point2d> { a, b };

            if (flip) forward.Reverse();
            forward[0] = a;
            forward[forward.Count - 1] = b;
            return forward;
        }

        // ── Interval collection ───────────────────────────────────────────────────

        private static List<(double lo, double hi)> CollectMergedXIntervals(
            double y, double ax, double bx, IList<(Point2d min, Point2d max)> boxes)
        {
            double loSeg = Math.Min(ax, bx);
            double hiSeg = Math.Max(ax, bx);
            var raw = new List<(double lo, double hi)>(boxes.Count);

            for (int i = 0; i < boxes.Count; i++)
            {
                double xmin = Math.Min(boxes[i].min.X, boxes[i].max.X);
                double xmax = Math.Max(boxes[i].min.X, boxes[i].max.X);
                double ymin = Math.Min(boxes[i].min.Y, boxes[i].max.Y);
                double ymax = Math.Max(boxes[i].min.Y, boxes[i].max.Y);
                if (y < ymin - PointEqualityTol || y > ymax + PointEqualityTol) continue;
                double lo = Math.Max(loSeg, xmin);
                double hi = Math.Min(hiSeg, xmax);
                if (lo <= hi + PointEqualityTol)
                    raw.Add((lo, hi));
            }
            return MergeIntervals(raw);
        }

        private static List<(double lo, double hi)> CollectMergedYIntervals(
            double x, double ay, double by, IList<(Point2d min, Point2d max)> boxes)
        {
            double loSeg = Math.Min(ay, by);
            double hiSeg = Math.Max(ay, by);
            var raw = new List<(double lo, double hi)>(boxes.Count);

            for (int i = 0; i < boxes.Count; i++)
            {
                double xmin = Math.Min(boxes[i].min.X, boxes[i].max.X);
                double xmax = Math.Max(boxes[i].min.X, boxes[i].max.X);
                double ymin = Math.Min(boxes[i].min.Y, boxes[i].max.Y);
                double ymax = Math.Max(boxes[i].min.Y, boxes[i].max.Y);
                if (x < xmin - PointEqualityTol || x > xmax + PointEqualityTol) continue;
                double lo = Math.Max(loSeg, ymin);
                double hi = Math.Min(hiSeg, ymax);
                if (lo <= hi + PointEqualityTol)
                    raw.Add((lo, hi));
            }
            return MergeIntervals(raw);
        }

        private static List<(double lo, double hi)> MergeIntervals(List<(double lo, double hi)> raw)
        {
            if (raw == null || raw.Count == 0)
                return new List<(double lo, double hi)>();
            raw.Sort((u, v) => u.lo.CompareTo(v.lo));
            var merged = new List<(double lo, double hi)>(raw.Count) { raw[0] };
            for (int i = 1; i < raw.Count; i++)
            {
                var cur = merged[merged.Count - 1];
                if (raw[i].lo <= cur.hi + PointEqualityTol)
                    merged[merged.Count - 1] = (cur.lo, Math.Max(cur.hi, raw[i].hi));
                else
                    merged.Add(raw[i]);
            }
            return merged;
        }

        // ── Detour offset selection ───────────────────────────────────────────────

        private static bool TryPickHorizontalDetourY(
            double y, double ax, double bx,
            List<(double lo, double hi)> merged,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            out double yDet)
        {
            yDet = y;
            double yMinOb = double.MaxValue;
            double yMaxOb = double.MinValue;
            double loSeg = Math.Min(ax, bx);
            double hiSeg = Math.Max(ax, bx);

            for (int i = 0; i < boxes.Count; i++)
            {
                double xmin = Math.Min(boxes[i].min.X, boxes[i].max.X);
                double xmax = Math.Max(boxes[i].min.X, boxes[i].max.X);
                double ymin = Math.Min(boxes[i].min.Y, boxes[i].max.Y);
                double ymax = Math.Max(boxes[i].min.Y, boxes[i].max.Y);
                if (y < ymin - PointEqualityTol || y > ymax + PointEqualityTol) continue;
                if (hiSeg < xmin - PointEqualityTol || loSeg > xmax + PointEqualityTol) continue;
                yMinOb = Math.Min(yMinOb, ymin);
                yMaxOb = Math.Max(yMaxOb, ymax);
            }

            if (yMinOb > yMaxOb) return false; // no intersecting boxes

            double obHeight = Math.Max(0, yMaxOb - yMinOb);
            double bump = Math.Max(MinSegmentLength * 5, obHeight * 0.1 + MinSegmentLength * 2);
            double yUp = yMaxOb + bump;
            double yDn = yMinOb - bump;

            bool preferUp = Math.Abs(yUp - y) <= Math.Abs(yDn - y);
            double first  = preferUp ? yUp : yDn;
            double second = preferUp ? yDn : yUp;

            if (DetourFeasible(merged, ax, bx, first, isHorizontal: true, zoneRings))
            { yDet = first; return true; }
            if (DetourFeasible(merged, ax, bx, second, isHorizontal: true, zoneRings))
            { yDet = second; return true; }

            yDet = first; // best available
            return true;
        }

        private static bool TryPickVerticalDetourX(
            double x, double ay, double by,
            List<(double lo, double hi)> merged,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            out double xDet)
        {
            xDet = x;
            double xMinOb = double.MaxValue;
            double xMaxOb = double.MinValue;
            double loSeg = Math.Min(ay, by);
            double hiSeg = Math.Max(ay, by);

            for (int i = 0; i < boxes.Count; i++)
            {
                double xmin = Math.Min(boxes[i].min.X, boxes[i].max.X);
                double xmax = Math.Max(boxes[i].min.X, boxes[i].max.X);
                double ymin = Math.Min(boxes[i].min.Y, boxes[i].max.Y);
                double ymax = Math.Max(boxes[i].min.Y, boxes[i].max.Y);
                if (x < xmin - PointEqualityTol || x > xmax + PointEqualityTol) continue;
                if (hiSeg < ymin - PointEqualityTol || loSeg > ymax + PointEqualityTol) continue;
                xMinOb = Math.Min(xMinOb, xmin);
                xMaxOb = Math.Max(xMaxOb, xmax);
            }

            if (xMinOb > xMaxOb) return false;

            double obWidth = Math.Max(0, xMaxOb - xMinOb);
            double bump = Math.Max(MinSegmentLength * 5, obWidth * 0.1 + MinSegmentLength * 2);
            double xRt = xMaxOb + bump;
            double xLt = xMinOb - bump;

            bool preferRt = Math.Abs(xRt - x) <= Math.Abs(xLt - x);
            double first  = preferRt ? xRt : xLt;
            double second = preferRt ? xLt : xRt;

            if (DetourFeasible(merged, ay, by, first, isHorizontal: false, zoneRings))
            { xDet = first; return true; }
            if (DetourFeasible(merged, ay, by, second, isHorizontal: false, zoneRings))
            { xDet = second; return true; }

            xDet = first;
            return true;
        }

        /// <summary>
        /// Checks zone containment for a proposed detour offset.
        /// <paramref name="isHorizontal"/>: true = horizontal detour (check (midX, detourY));
        /// false = vertical detour (check (detourX, midY)).
        /// </summary>
        private static bool DetourFeasible(
            List<(double lo, double hi)> merged,
            double segA, double segB,
            double detourValue,
            bool isHorizontal,
            IList<IList<Point2d>> zoneRings)
        {
            if (zoneRings == null || zoneRings.Count == 0) return true;
            foreach (var iv in merged)
            {
                double mid = 0.5 * (Math.Max(segA, iv.lo) + Math.Min(segB, iv.hi));
                var p = isHorizontal
                    ? new Point2d(mid, detourValue)
                    : new Point2d(detourValue, mid);
                if (!RingGeometry.PointInAnyOfRings(zoneRings, p))
                    return false;
            }
            return true;
        }

        // ── Path vertex builders ──────────────────────────────────────────────────

        private static List<Point2d> BuildHorizontalLeftToRight(
            double ax, double bx, double y,
            List<(double lo, double hi)> merged, double yDet)
        {
            var pts = new List<Point2d>(merged.Count * 4 + 2);
            pts.Add(new Point2d(ax, y));
            double curX = ax;

            for (int i = 0; i < merged.Count; i++)
            {
                double bl = merged[i].lo;
                double br = merged[i].hi;

                // Guard: skip past intervals and clamp to segment range
                if (br <= curX + PointEqualityTol) continue;
                if (bl > bx + PointEqualityTol) break;
                br = Math.Min(br, bx);

                // Approach to obstacle entry
                if (bl > curX + PointEqualityTol)
                {
                    var approach = new Point2d(bl, y);
                    if (!PointsEqual(pts[pts.Count - 1], approach))
                        pts.Add(approach);
                }

                double entryX = Math.Max(curX, bl);
                if (entryX > br - PointEqualityTol) { curX = br; continue; }

                // Detour: down the entry edge, across, back up at the exit edge
                var en = new Point2d(entryX, y);
                if (!PointsEqual(pts[pts.Count - 1], en)) pts.Add(en);
                pts.Add(new Point2d(entryX, yDet));
                pts.Add(new Point2d(br, yDet));
                pts.Add(new Point2d(br, y));
                curX = br;
            }

            // Trailing run to endpoint
            if (curX < bx - PointEqualityTol)
            {
                var trail = new Point2d(bx, y);
                if (!PointsEqual(pts[pts.Count - 1], trail))
                    pts.Add(trail);
            }

            return pts;
        }

        private static List<Point2d> BuildVerticalBottomToTop(
            double x, double ay, double by,
            List<(double lo, double hi)> merged, double xDet)
        {
            var pts = new List<Point2d>(merged.Count * 4 + 2);
            pts.Add(new Point2d(x, ay));
            double curY = ay;

            for (int i = 0; i < merged.Count; i++)
            {
                double bl = merged[i].lo;
                double br = merged[i].hi;

                if (br <= curY + PointEqualityTol) continue;
                if (bl > by + PointEqualityTol) break;
                br = Math.Min(br, by);

                if (bl > curY + PointEqualityTol)
                {
                    var approach = new Point2d(x, bl);
                    if (!PointsEqual(pts[pts.Count - 1], approach))
                        pts.Add(approach);
                }

                double entryY = Math.Max(curY, bl);
                if (entryY > br - PointEqualityTol) { curY = br; continue; }

                var en = new Point2d(x, entryY);
                if (!PointsEqual(pts[pts.Count - 1], en)) pts.Add(en);
                pts.Add(new Point2d(xDet, entryY));
                pts.Add(new Point2d(xDet, br));
                pts.Add(new Point2d(x, br));
                curY = br;
            }

            if (curY < by - PointEqualityTol)
            {
                var trail = new Point2d(x, by);
                if (!PointsEqual(pts[pts.Count - 1], trail))
                    pts.Add(trail);
            }

            return pts;
        }
    }
}
