using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent.Planning.Validators;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Inserts axis-aligned detours so branch pipe segments do not pass through shaft block footprints
    /// (expanded rectangles in plan).
    /// </summary>
    public static class BranchPipeShaftDetour2d
    {
        private const double DefaultShaftHalfExtentDu = 0.125;

        /// <summary>
        /// Axis-aligned obstacle rectangles for shafts in <paramref name="zone"/>; coordinates are expanded by <paramref name="clearanceDu"/>.
        /// </summary>
        public static List<(Point2d min, Point2d max)> BuildShaftObstacles(Database db, Polyline zone, double clearanceDu)
        {
            var list = new List<(Point2d min, Point2d max)>();
            if (db == null || zone == null)
                return list;

            double c = Math.Max(0, clearanceDu);
            var shafts = FindShaftsInsideBoundary.GetShaftBlocksInsideBoundary(db, zone);
            if (shafts == null || shafts.Count == 0)
                return list;

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

                if (maxX - minX < 1e-12 && maxY - minY < 1e-12)
                    continue;

                list.Add((new Point2d(minX, minY), new Point2d(maxX, maxY)));
            }

            return list;
        }

        /// <summary>
        /// Waypoints from <paramref name="a"/> to <paramref name="b"/> along an orthogonal path that avoids obstacle boxes.
        /// If the segment is not axis-aligned or there are no obstacles, returns [a, b].
        /// </summary>
        public static List<Point2d> AxisAlignedWaypointsAvoidingBoxes(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol)
        {
            var two = new List<Point2d> { a, b };
            if (boxes == null || boxes.Count == 0)
                return DedupeConsecutive(two, tol);

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double span = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double axisEps = Math.Max(tol > 0 ? tol * 10.0 : 1e-6, span * 1e-9);
            bool horiz = Math.Abs(dy) <= axisEps && Math.Abs(dx) > axisEps;
            bool vert = Math.Abs(dx) <= axisEps && Math.Abs(dy) > axisEps;
            if (!horiz && !vert)
                return DedupeConsecutive(two, tol);

            if (horiz)
                return DedupeConsecutive(HorizontalPath(a, b, boxes, zoneRings, tol), tol);
            return DedupeConsecutive(VerticalPath(a, b, boxes, zoneRings, tol), tol);
        }

        private static List<Point2d> HorizontalPath(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol)
        {
            double y = 0.5 * (a.Y + b.Y);
            double x0 = a.X;
            double x1 = b.X;
            if (Math.Abs(x1 - x0) < tol * 0.01)
                return new List<Point2d> { a, b };

            bool flip = x1 < x0;
            double ax = flip ? x1 : x0;
            double bx = flip ? x0 : x1;
            var merged = CollectMergedXIntervalsOnHorizontalLine(y, ax, bx, boxes, tol);
            if (merged.Count == 0)
                return new List<Point2d> { a, b };

            if (!TryPickHorizontalDetourY(y, ax, bx, merged, boxes, zoneRings, tol, out double yDet))
                return new List<Point2d> { a, b };

            var forward = BuildHorizontalLeftToRight(ax, bx, y, merged, yDet, tol);
            if (flip)
                forward.Reverse();
            // Restore exact endpoints
            if (forward.Count > 0)
                forward[0] = a;
            if (forward.Count > 0)
                forward[forward.Count - 1] = b;
            return forward;
        }

        private static List<Point2d> VerticalPath(
            Point2d a,
            Point2d b,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol)
        {
            double x = 0.5 * (a.X + b.X);
            double y0 = a.Y;
            double y1 = b.Y;
            if (Math.Abs(y1 - y0) < tol * 0.01)
                return new List<Point2d> { a, b };

            bool flip = y1 < y0;
            double ay = flip ? y1 : y0;
            double by = flip ? y0 : y1;
            var merged = CollectMergedYIntervalsOnVerticalLine(x, ay, by, boxes, tol);
            if (merged.Count == 0)
                return new List<Point2d> { a, b };

            if (!TryPickVerticalDetourX(x, ay, by, merged, boxes, zoneRings, tol, out double xDet))
                return new List<Point2d> { a, b };

            var forward = BuildVerticalBottomToTop(x, ay, by, merged, xDet, tol);
            if (flip)
                forward.Reverse();
            if (forward.Count > 0)
                forward[0] = a;
            if (forward.Count > 0)
                forward[forward.Count - 1] = b;
            return forward;
        }

        private static List<(double lo, double hi)> CollectMergedXIntervalsOnHorizontalLine(
            double y,
            double ax,
            double bx,
            IList<(Point2d min, Point2d max)> boxes,
            double tol)
        {
            double loSeg = Math.Min(ax, bx);
            double hiSeg = Math.Max(ax, bx);
            var raw = new List<(double lo, double hi)>();
            double te = tol > 0 ? tol : 1e-6;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (y < ymin - te || y > ymax + te)
                    continue;

                double bl = Math.Max(loSeg, xmin);
                double br = Math.Min(hiSeg, xmax);
                if (bl <= br + te)
                    raw.Add((bl, br));
            }

            return MergeIntervals(raw, te);
        }

        private static List<(double lo, double hi)> CollectMergedYIntervalsOnVerticalLine(
            double x,
            double ay,
            double by,
            IList<(Point2d min, Point2d max)> boxes,
            double tol)
        {
            double loSeg = Math.Min(ay, by);
            double hiSeg = Math.Max(ay, by);
            var raw = new List<(double lo, double hi)>();
            double te = tol > 0 ? tol : 1e-6;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (x < xmin - te || x > xmax + te)
                    continue;

                double bl = Math.Max(loSeg, ymin);
                double br = Math.Min(hiSeg, ymax);
                if (bl <= br + te)
                    raw.Add((bl, br));
            }

            return MergeIntervals(raw, te);
        }

        private static List<(double lo, double hi)> MergeIntervals(List<(double lo, double hi)> raw, double te)
        {
            if (raw == null || raw.Count == 0)
                return new List<(double lo, double hi)>();
            raw.Sort((u, v) => u.lo.CompareTo(v.lo));
            var merged = new List<(double lo, double hi)>();
            var cur = raw[0];
            for (int i = 1; i < raw.Count; i++)
            {
                var n = raw[i];
                if (n.lo <= cur.hi + te)
                    cur = (cur.lo, Math.Max(cur.hi, n.hi));
                else
                {
                    merged.Add(cur);
                    cur = n;
                }
            }
            merged.Add(cur);
            return merged;
        }

        private static bool TryPickHorizontalDetourY(
            double y,
            double ax,
            double bx,
            List<(double lo, double hi)> merged,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol,
            out double yDet)
        {
            yDet = y;
            double yMinOb = double.MaxValue;
            double yMaxOb = double.MinValue;
            double te = tol > 0 ? tol : 1e-6;
            double loSeg = Math.Min(ax, bx);
            double hiSeg = Math.Max(ax, bx);

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (y < ymin - te || y > ymax + te)
                    continue;
                if (hiSeg < xmin - te || loSeg > xmax + te)
                    continue;
                yMinOb = Math.Min(yMinOb, ymin);
                yMaxOb = Math.Max(yMaxOb, ymax);
            }

            if (!(yMinOb <= yMaxOb + te))
                return false;

            double bump = Math.Max(te * 50, (yMaxOb - yMinOb) * 0.02 + te * 10);
            double yUp = yMaxOb + bump;
            double yDn = yMinOb - bump;

            double costUp = 2.0 * Math.Abs(yUp - y);
            double costDn = 2.0 * Math.Abs(yDn - y);

            bool preferUp = costUp <= costDn;
            double first = preferUp ? yUp : yDn;
            double second = preferUp ? yDn : yUp;

            if (HorizontalDetourFeasibleInZone(ax, bx, merged, first, zoneRings, te))
            {
                yDet = first;
                return true;
            }
            if (HorizontalDetourFeasibleInZone(ax, bx, merged, second, zoneRings, te))
            {
                yDet = second;
                return true;
            }

            yDet = preferUp ? yUp : yDn;
            return true;
        }

        private static bool HorizontalDetourFeasibleInZone(
            double ax,
            double bx,
            List<(double lo, double hi)> merged,
            double yDet,
            IList<IList<Point2d>> zoneRings,
            double te)
        {
            if (zoneRings == null || zoneRings.Count == 0)
                return true;
            foreach (var iv in merged)
            {
                double mx = 0.5 * (Math.Max(ax, iv.lo) + Math.Min(bx, iv.hi));
                var p = new Point2d(mx, yDet);
                if (!RingGeometry.PointInAnyOfRings(zoneRings, p))
                    return false;
            }
            return true;
        }

        private static bool TryPickVerticalDetourX(
            double x,
            double ay,
            double by,
            List<(double lo, double hi)> merged,
            IList<(Point2d min, Point2d max)> boxes,
            IList<IList<Point2d>> zoneRings,
            double tol,
            out double xDet)
        {
            xDet = x;
            double xMinOb = double.MaxValue;
            double xMaxOb = double.MinValue;
            double te = tol > 0 ? tol : 1e-6;
            double loSeg = Math.Min(ay, by);
            double hiSeg = Math.Max(ay, by);

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                double xmin = Math.Min(box.min.X, box.max.X);
                double xmax = Math.Max(box.min.X, box.max.X);
                double ymin = Math.Min(box.min.Y, box.max.Y);
                double ymax = Math.Max(box.min.Y, box.max.Y);
                if (x < xmin - te || x > xmax + te)
                    continue;
                if (hiSeg < ymin - te || loSeg > ymax + te)
                    continue;
                xMinOb = Math.Min(xMinOb, xmin);
                xMaxOb = Math.Max(xMaxOb, xmax);
            }

            if (!(xMinOb <= xMaxOb + te))
                return false;

            double bump = Math.Max(te * 50, (xMaxOb - xMinOb) * 0.02 + te * 10);
            double xRt = xMaxOb + bump;
            double xLt = xMinOb - bump;

            double costRt = 2.0 * Math.Abs(xRt - x);
            double costLt = 2.0 * Math.Abs(xLt - x);
            bool preferRt = costRt <= costLt;
            double first = preferRt ? xRt : xLt;
            double second = preferRt ? xLt : xRt;

            if (VerticalDetourFeasibleInZone(ay, by, merged, first, zoneRings, te))
            {
                xDet = first;
                return true;
            }
            if (VerticalDetourFeasibleInZone(ay, by, merged, second, zoneRings, te))
            {
                xDet = second;
                return true;
            }

            xDet = preferRt ? xRt : xLt;
            return true;
        }

        private static bool VerticalDetourFeasibleInZone(
            double ay,
            double by,
            List<(double lo, double hi)> merged,
            double xDet,
            IList<IList<Point2d>> zoneRings,
            double te)
        {
            if (zoneRings == null || zoneRings.Count == 0)
                return true;
            foreach (var iv in merged)
            {
                double my = 0.5 * (Math.Max(ay, iv.lo) + Math.Min(by, iv.hi));
                var p = new Point2d(xDet, my);
                if (!RingGeometry.PointInAnyOfRings(zoneRings, p))
                    return false;
            }
            return true;
        }

        private static List<Point2d> BuildHorizontalLeftToRight(
            double ax,
            double bx,
            double y,
            List<(double lo, double hi)> merged,
            double yDet,
            double tol)
        {
            var pts = new List<Point2d>();
            double te = tol > 0 ? tol : 1e-6;
            pts.Add(new Point2d(ax, y));
            double curX = ax;

            for (int i = 0; i < merged.Count; i++)
            {
                double bl = merged[i].lo;
                double br = merged[i].hi;
                if (curX < bl - te)
                    pts.Add(new Point2d(bl, y));

                double entryX = Math.Max(curX, bl);
                if (entryX <= br + te)
                {
                    if (pts.Count == 0 || Math.Abs(pts[pts.Count - 1].X - entryX) > te || Math.Abs(pts[pts.Count - 1].Y - y) > te)
                        pts.Add(new Point2d(entryX, y));
                    pts.Add(new Point2d(entryX, yDet));
                    pts.Add(new Point2d(br, yDet));
                    pts.Add(new Point2d(br, y));
                    curX = br;
                }
            }

            if (curX < bx - te)
                pts.Add(new Point2d(bx, y));

            return pts;
        }

        private static List<Point2d> BuildVerticalBottomToTop(
            double x,
            double ay,
            double by,
            List<(double lo, double hi)> merged,
            double xDet,
            double tol)
        {
            var pts = new List<Point2d>();
            double te = tol > 0 ? tol : 1e-6;
            pts.Add(new Point2d(x, ay));
            double curY = ay;

            for (int i = 0; i < merged.Count; i++)
            {
                double bl = merged[i].lo;
                double br = merged[i].hi;
                if (curY < bl - te)
                    pts.Add(new Point2d(x, bl));

                double entryY = Math.Max(curY, bl);
                if (entryY <= br + te)
                {
                    if (pts.Count == 0 || Math.Abs(pts[pts.Count - 1].Y - entryY) > te || Math.Abs(pts[pts.Count - 1].X - x) > te)
                        pts.Add(new Point2d(x, entryY));
                    pts.Add(new Point2d(xDet, entryY));
                    pts.Add(new Point2d(xDet, br));
                    pts.Add(new Point2d(x, br));
                    curY = br;
                }
            }

            if (curY < by - te)
                pts.Add(new Point2d(x, by));

            return pts;
        }

        private static List<Point2d> DedupeConsecutive(List<Point2d> pts, double tol)
        {
            if (pts == null || pts.Count == 0)
                return pts ?? new List<Point2d>();
            double te = tol > 0 ? tol : 1e-6;
            var o = new List<Point2d> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = o[o.Count - 1];
                if (Math.Abs(p.X - q.X) > te || Math.Abs(p.Y - q.Y) > te)
                    o.Add(p);
            }
            return o;
        }
    }
}
