using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    /// <summary>
    /// Post-processes Euclidean Voronoi (or similar) zone rings into strictly axis-aligned boundaries:
    /// each edge becomes horizontal or vertical using |dx| vs |dy|, with supporting lines taken through edge midpoints.
    /// Corners are reconstructed as intersections of consecutive supporting lines (midpoint projection, unbiased).
    /// Includes vertex merging, collinear collapse, optional clip-to-floor via Region booleans, and basic overlap warnings.
    /// </summary>
    public static class OrthogonalVoronoiPostProcessor2d
    {
        /// <summary>
        /// Orthogonalizes then clips the ring to the floor boundary (largest fragment if split).
        /// </summary>
        public static bool TryOrthogonalizeClipAndValidate(
            List<Point2d> ringIn,
            Polyline floorBoundary,
            double tol,
            out List<Point2d> ringOut,
            out string errorMessage)
        {
            ringOut = null;
            errorMessage = null;
            if (ringIn == null || ringIn.Count < 3 || floorBoundary == null)
            {
                errorMessage = "Invalid ring or floor.";
                return false;
            }

            double orthoTol = tol;
            try
            {
                var extFb = floorBoundary.GeometricExtents;
                double dxE = Math.Abs(extFb.MaxPoint.X - extFb.MinPoint.X);
                double dyE = Math.Abs(extFb.MaxPoint.Y - extFb.MinPoint.Y);
                double extentFb = Math.Max(dxE, dyE);
                if (extentFb > 0)
                    orthoTol = Math.Max(tol, extentFb * 1e-7);
            }
            catch { /* ignore */ }

            if (!TryOrthogonalizeRing(ringIn, orthoTol, out var ortho, out string orthoErr))
            {
                errorMessage = orthoErr ?? "Orthogonalize failed.";
                return false;
            }

            Region boundaryRegion = null;
            try
            {
                double regionTol = orthoTol;
                if (!RegionBooleanIntersection2d.TryCreateBoundaryRegion(floorBoundary, regionTol, out boundaryRegion, out string regErr))
                {
                    errorMessage = regErr ?? "Could not create floor Region.";
                    return false;
                }

                using (var slab = RingToClosedPolyline(ortho, floorBoundary))
                {
                    if (!RegionBooleanIntersection2d.TryIntersectBoundaryRegionWithSlabToRings(
                            boundaryRegion,
                            slab,
                            regionTol,
                            out var clippedRings,
                            out string clipErr))
                    {
                        errorMessage = clipErr ?? "Clip to floor failed.";
                        return false;
                    }

                    if (clippedRings == null || clippedRings.Count == 0)
                    {
                        errorMessage = "Clip produced no area.";
                        return false;
                    }

                    List<Point2d> best = null;
                    double bestA = -1;
                    for (int i = 0; i < clippedRings.Count; i++)
                    {
                        var rr = CleanRingVertices(clippedRings[i], regionTol);
                        if (rr == null || rr.Count < 3) continue;
                        double a = PolygonVerticalHalfPlaneClip2d.AbsArea(rr);
                        if (a > bestA)
                        {
                            bestA = a;
                            best = rr;
                        }
                    }

                    if (best == null || best.Count < 3)
                    {
                        errorMessage = "After clip, no valid ring.";
                        return false;
                    }

                    ringOut = best;
                    return true;
                }
            }
            finally
            {
                try { boundaryRegion?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Dominant-direction snap + intersection reconstruction + merge/collinear cleanup.
        /// </summary>
        public static bool TryOrthogonalizeRing(List<Point2d> ringIn, double tol, out List<Point2d> ringOut, out string errorMessage)
        {
            ringOut = null;
            errorMessage = null;
            if (ringIn == null || ringIn.Count < 3)
            {
                errorMessage = "Ring needs at least 3 vertices.";
                return false;
            }

            double eps = Math.Max(tol, 1e-9);

            int n = ringIn.Count;
            var edgeHorizontal = new bool[n];
            var edgeC = new double[n];

            for (int i = 0; i < n; i++)
            {
                var a = ringIn[i];
                var b = ringIn[(i + 1) % n];
                double mx = (a.X + b.X) * 0.5;
                double my = (a.Y + b.Y) * 0.5;
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                if (Math.Abs(dx) >= Math.Abs(dy))
                {
                    edgeHorizontal[i] = true;
                    edgeC[i] = my;
                }
                else
                {
                    edgeHorizontal[i] = false;
                    edgeC[i] = mx;
                }
            }

            var verts = new List<Point2d>(n);
            for (int k = 0; k < n; k++)
            {
                int prev = (k - 1 + n) % n;
                var vk = IntersectSupportingLines(
                    edgeHorizontal[prev],
                    edgeC[prev],
                    edgeHorizontal[k],
                    edgeC[k],
                    ringIn[k],
                    eps);
                verts.Add(vk);
            }

            verts = MergeCloseVertices(verts, eps);
            verts = RemoveAxisCollinearMiddleVertices(verts, eps);
            if (verts.Count < 3)
            {
                errorMessage = "Degenerate polygon after orthogonal snap.";
                return false;
            }

            double area;
            try { area = PolygonVerticalHalfPlaneClip2d.AbsArea(verts); }
            catch { area = 0; }
            if (area <= 1e-12 * Math.Max(1.0, PolygonBBoxMetric(verts)))
            {
                errorMessage = "Zero or negligible area after orthogonal snap.";
                return false;
            }

            ringOut = verts;
            return true;
        }

        /// <summary>Returns true if any zone centroid falls strictly inside another zone (possible overlap).</summary>
        public static bool TryDetectPossibleOverlaps(IList<List<Point2d>> rings, out List<(int I, int J)> pairs)
        {
            pairs = new List<(int, int)>();
            if (rings == null) return false;
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] == null || rings[i].Count < 3) continue;
                var ci = PolygonUtils.ApproxCentroidAreaWeighted(rings[i]);
                for (int j = 0; j < rings.Count; j++)
                {
                    if (i == j || rings[j] == null || rings[j].Count < 3) continue;
                    if (PolygonUtils.PointInPolygon(rings[j], ci))
                        pairs.Add((i, j));
                }
            }

            return pairs.Count > 0;
        }

        private static Point2d IntersectSupportingLines(
            bool prevH,
            double prevC,
            bool curH,
            double curC,
            Point2d originalVertex,
            double eps)
        {
            if (prevH && curH)
            {
                if (Math.Abs(prevC - curC) <= eps)
                    return new Point2d(originalVertex.X, prevC);
                double yAvg = (prevC + curC) * 0.5;
                return new Point2d(originalVertex.X, yAvg);
            }

            if (!prevH && !curH)
            {
                if (Math.Abs(prevC - curC) <= eps)
                    return new Point2d(prevC, originalVertex.Y);
                double xAvg = (prevC + curC) * 0.5;
                return new Point2d(xAvg, originalVertex.Y);
            }

            if (prevH && !curH)
                return new Point2d(curC, prevC);
            return new Point2d(prevC, curC);
        }

        private static List<Point2d> MergeCloseVertices(List<Point2d> verts, double eps)
        {
            if (verts == null || verts.Count < 3)
                return verts;

            var cur = new List<Point2d>(verts);
            bool changed = true;
            int guard = cur.Count * 4 + 24;
            while (changed && guard-- > 0 && cur.Count >= 3)
            {
                changed = false;
                int n = cur.Count;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (cur[i].GetDistanceTo(cur[j]) <= eps)
                    {
                        cur[i] = Midpoint(cur[i], cur[j]);
                        cur.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }

            while (cur.Count >= 2 && cur[0].GetDistanceTo(cur[cur.Count - 1]) <= eps)
            {
                cur[0] = Midpoint(cur[0], cur[cur.Count - 1]);
                cur.RemoveAt(cur.Count - 1);
            }

            return cur.Count >= 3 ? cur : verts;
        }

        private static Point2d Midpoint(Point2d a, Point2d b)
            => new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

        private static List<Point2d> RemoveAxisCollinearMiddleVertices(List<Point2d> verts, double eps)
        {
            if (verts == null || verts.Count < 4)
                return verts;

            bool changed = true;
            var cur = verts;
            int guard = verts.Count + 16;
            while (changed && guard-- > 0)
            {
                changed = false;
                int n = cur.Count;
                var next = new List<Point2d>();
                for (int i = 0; i < n; i++)
                {
                    var prev = cur[(i - 1 + n) % n];
                    var p = cur[i];
                    var nxt = cur[(i + 1) % n];
                    if (IsAxisCollinear(prev, p, nxt, eps))
                    {
                        changed = true;
                        continue;
                    }

                    next.Add(p);
                }

                cur = next;
                if (cur.Count < 3)
                    break;
            }

            return cur;
        }

        private static bool IsAxisCollinear(Point2d a, Point2d b, Point2d c, double eps)
        {
            bool h1 = Math.Abs(a.Y - b.Y) <= eps;
            bool h2 = Math.Abs(b.Y - c.Y) <= eps;
            bool v1 = Math.Abs(a.X - b.X) <= eps;
            bool v2 = Math.Abs(b.X - c.X) <= eps;
            return (h1 && h2) || (v1 && v2);
        }

        private static List<Point2d> CleanRingVertices(List<Point2d> ring, double tol)
        {
            if (ring == null || ring.Count < 3)
                return null;
            double eps = Math.Max(tol, 1e-9);
            var merged = MergeCloseVertices(new List<Point2d>(ring), eps);
            merged = RemoveAxisCollinearMiddleVertices(merged, eps);
            return merged != null && merged.Count >= 3 ? merged : null;
        }

        private static double PolygonBBoxMetric(List<Point2d> ring)
        {
            PolygonUtils.GetBoundingBox(ring, out double minX, out double minY, out double maxX, out double maxY);
            return Math.Max(Math.Abs(maxX - minX), Math.Abs(maxY - minY));
        }

        private static Polyline RingToClosedPolyline(List<Point2d> ring, Polyline plane)
        {
            var pl = new Polyline();
            for (int i = 0; i < ring.Count; i++)
                pl.AddVertexAt(i, ring[i], 0, 0, 0);
            pl.Closed = true;
            if (plane != null)
            {
                pl.Elevation = plane.Elevation;
                pl.Normal = plane.Normal;
            }

            return pl;
        }
    }
}
