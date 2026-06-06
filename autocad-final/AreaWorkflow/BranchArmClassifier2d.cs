using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Separates branch-layer pipes into sprinkler columns vs end-arm sub-mains.
    /// Arms are the distribution runs that leave a main-pipe terminal and run parallel to the
    /// main flow (perpendicular to the dominant column direction). They may have sprinkler heads
    /// connected via T-junction columns, so head presence on the pipe is not a reliable signal.
    /// </summary>
    public static class BranchArmClassifier2d
    {
        public static void Classify(
            List<List<Point2d>> spinePolys,
            List<List<Point2d>> branchPolys,
            double snapTol,
            out List<List<Point2d>> armPolys,
            out List<List<Point2d>> columnPolys,
            out bool columnsVertical)
        {
            armPolys = new List<List<Point2d>>();
            columnPolys = new List<List<Point2d>>();
            columnsVertical = true;

            if (branchPolys == null || branchPolys.Count == 0)
                return;

            var branchList = new List<List<Point2d>>();
            foreach (var poly in branchPolys)
            {
                if (poly != null && poly.Count >= 2)
                    branchList.Add(new List<Point2d>(poly));
            }
            if (branchList.Count == 0)
                return;

            columnsVertical = InferColumnsVertical(branchList);
            var spineTerminals = CollectSpineTerminals(spinePolys, snapTol);

            foreach (var poly in branchList)
            {
                bool columnOriented = IsColumnOriented(poly, columnsVertical);
                bool touchesSpineTerminal = EndpointTouchesAny(poly, spineTerminals, snapTol);

                // Arm: leaves a main terminal and runs along the distribution axis (not a column).
                if (touchesSpineTerminal && !columnOriented)
                    armPolys.Add(poly);
                else if (columnOriented)
                    columnPolys.Add(poly);
                else if (touchesSpineTerminal)
                    armPolys.Add(poly);
                else
                    columnPolys.Add(poly);
            }

            // Arms may chain off other arms (arm endpoint → next arm segment). Pick those up too.
            bool added = true;
            while (added)
            {
                added = false;
                for (int i = columnPolys.Count - 1; i >= 0; i--)
                {
                    var poly = columnPolys[i];
                    if (!IsColumnOriented(poly, columnsVertical) &&
                        EndpointTouchesAny(poly, CollectArmEndpoints(armPolys, snapTol), snapTol))
                    {
                        armPolys.Add(poly);
                        columnPolys.RemoveAt(i);
                        added = true;
                    }
                }
            }
        }

        public static bool IsArmPolyline(IList<Point2d> poly, List<List<Point2d>> spinePolys, double snapTol)
        {
            if (poly == null || poly.Count < 2) return false;
            bool columnsVertical = InferColumnsVertical(new List<List<Point2d>> { new List<Point2d>(poly) });
            var terminals = CollectSpineTerminals(spinePolys, snapTol);
            return EndpointTouchesAny(poly, terminals, snapTol) && !IsColumnOriented(poly, columnsVertical);
        }

        private static bool InferColumnsVertical(IList<List<Point2d>> branchPolys)
        {
            double sumDx = 0, sumDy = 0;
            foreach (var poly in branchPolys)
            {
                PipeExtents(poly, out double dx, out double dy);
                sumDx += dx;
                sumDy += dy;
            }
            return sumDy >= sumDx;
        }

        private static bool IsColumnOriented(IList<Point2d> poly, bool columnsVertical)
        {
            PipeExtents(poly, out double dx, out double dy);
            return columnsVertical ? dy >= dx : dx >= dy;
        }

        private static void PipeExtents(IList<Point2d> poly, out double dx, out double dy)
        {
            dx = 0;
            dy = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                dx += Math.Abs(poly[i + 1].X - poly[i].X);
                dy += Math.Abs(poly[i + 1].Y - poly[i].Y);
            }
        }

        /// <summary>Degree-1 vertices of the spine graph (true main-pipe ends).</summary>
        private static List<Point2d> CollectSpineTerminals(List<List<Point2d>> spinePolys, double snapTol)
        {
            var nodes = new List<Point2d>();
            var degree = new List<int>();

            int NodeIndex(Point2d p)
            {
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].GetDistanceTo(p) <= snapTol) return i;
                nodes.Add(p);
                degree.Add(0);
                return nodes.Count - 1;
            }

            void AddEdge(Point2d a, Point2d b)
            {
                if (a.GetDistanceTo(b) <= snapTol) return;
                int ia = NodeIndex(a);
                int ib = NodeIndex(b);
                if (ia == ib) return;
                degree[ia]++;
                degree[ib]++;
            }

            if (spinePolys != null)
            {
                foreach (var poly in spinePolys)
                {
                    if (poly == null || poly.Count < 2) continue;
                    for (int i = 0; i + 1 < poly.Count; i++)
                        AddEdge(poly[i], poly[i + 1]);
                }
            }

            var terminals = new List<Point2d>();
            for (int i = 0; i < nodes.Count; i++)
                if (degree[i] == 1) terminals.Add(nodes[i]);
            return terminals;
        }

        private static List<Point2d> CollectArmEndpoints(IList<List<Point2d>> armPolys, double snapTol)
        {
            var pts = new List<Point2d>();
            if (armPolys == null) return pts;
            foreach (var poly in armPolys)
            {
                if (poly == null || poly.Count < 2) continue;
                MergePoint(pts, poly[0], snapTol);
                MergePoint(pts, poly[poly.Count - 1], snapTol);
            }
            return pts;
        }

        private static void MergePoint(List<Point2d> pts, Point2d p, double snapTol)
        {
            foreach (var q in pts)
                if (q.GetDistanceTo(p) <= snapTol) return;
            pts.Add(p);
        }

        private static bool EndpointTouchesAny(IList<Point2d> poly, IList<Point2d> targets, double snapTol)
        {
            if (targets == null || targets.Count == 0 || poly == null || poly.Count < 2)
                return false;
            var a = poly[0];
            var b = poly[poly.Count - 1];
            foreach (var t in targets)
            {
                if (a.GetDistanceTo(t) <= snapTol || b.GetDistanceTo(t) <= snapTol)
                    return true;
            }
            return false;
        }

        /// <summary>Closest point on any segment of the main network (spine + arms).</summary>
        public static Point2d ClosestOnMainNetwork(
            List<List<Point2d>> spinePolys,
            List<List<Point2d>> armPolys,
            Point2d p)
        {
            double bestD = double.MaxValue;
            Point2d best = p;

            void VisitPoly(IList<Point2d> poly)
            {
                if (poly == null || poly.Count < 2) return;
                for (int i = 0; i + 1 < poly.Count; i++)
                {
                    var cp = ClosestOnSegment(poly[i], poly[i + 1], p);
                    double d = cp.GetDistanceTo(p);
                    if (d < bestD) { bestD = d; best = cp; }
                }
            }

            if (spinePolys != null)
                foreach (var poly in spinePolys) VisitPoly(poly);
            if (armPolys != null)
                foreach (var poly in armPolys) VisitPoly(poly);

            return best;
        }

        private static Point2d ClosestOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }
    }
}
