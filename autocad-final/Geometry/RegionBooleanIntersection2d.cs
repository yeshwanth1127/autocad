using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.Geometry
{
    public static class RegionBooleanIntersection2d
    {
        public static bool TryCreateBoundaryRegion(
            Polyline boundary,
            double tolerance,
            out Region boundaryRegion,
            out string errorMessage)
        {
            boundaryRegion = null;
            errorMessage = null;

            if (boundary == null)
            {
                errorMessage = "Boundary is null.";
                return false;
            }

            Polyline pl = null;
            DBObjectCollection regs = null;
            try
            {
                pl = BoundaryEntityToClosedLwPolyline.TryCloseCoincidentVertices(boundary, tolerance);
                if (pl == null || pl.NumberOfVertices < 3 || !pl.Closed)
                {
                    errorMessage = "Boundary polyline must be closed.";
                    return false;
                }

                if (HasNoBulges(pl))
                    pl = RemoveConsecutiveDuplicates(pl, tolerance);

                pl.Elevation = boundary.Elevation;
                pl.Normal = boundary.Normal;

                regs = Region.CreateFromCurves(new DBObjectCollection { pl });
                boundaryRegion = FirstRegionOrNull(regs);
                if (boundaryRegion == null)
                {
                    errorMessage = "Could not create Region from boundary (self-intersection / non-planar / invalid).";
                    return false;
                }

                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                try { pl?.Dispose(); } catch { /* ignore */ }
                DisposeAllExcept(regs, boundaryRegion);
            }
        }

        public static bool TryIntersectBoundaryRegionWithSlabToRings(
            Region boundaryRegion,
            Polyline slab,
            double tolerance,
            out List<List<Point2d>> rings,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            errorMessage = null;

            if (boundaryRegion == null || slab == null)
            {
                errorMessage = "Boundary region or slab is null.";
                return false;
            }

            Region slabRegion = null;
            Region clipped = null;
            DBObjectCollection exploded = null;
            DBObjectCollection slabRegs = null;

            try
            {
                slabRegs = Region.CreateFromCurves(new DBObjectCollection { slab });
                slabRegion = FirstRegionOrNull(slabRegs);
                if (slabRegion == null)
                {
                    errorMessage = "Could not create Region from slab.";
                    return false;
                }

                clipped = (Region)boundaryRegion.Clone();
                clipped.BooleanOperation(BooleanOperationType.BoolIntersect, slabRegion);

                exploded = new DBObjectCollection();
                clipped.Explode(exploded);

                var segments = ExtractSegments(exploded, tolerance, out string segErr);
                if (segments == null)
                {
                    errorMessage = segErr ?? "Could not extract segments from Region explode.";
                    return false;
                }

                var loops = ChainSegmentsToClosedLoops(segments, tolerance, out string loopErr);
                if (loops == null)
                {
                    errorMessage = loopErr ?? "Could not chain Region segments into loops.";
                    return false;
                }

                foreach (var loop in loops)
                {
                    if (loop.Count >= 3)
                        rings.Add(loop);
                }

                if (rings.Count == 0)
                {
                    errorMessage = "Intersection produced no area (empty strip).";
                    return false;
                }

                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                try { slabRegion?.Dispose(); } catch { /* ignore */ }
                try { clipped?.Dispose(); } catch { /* ignore */ }
                DisposeAll(exploded);
                DisposeAllExcept(slabRegs, slabRegion);
            }
        }

        public static Polyline MakeRectangleSlabOnBoundaryPlane(Polyline boundary, double x0, double y0, double x1, double y1)
        {
            double minX = Math.Min(x0, x1);
            double maxX = Math.Max(x0, x1);
            double minY = Math.Min(y0, y1);
            double maxY = Math.Max(y0, y1);

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
            pl.Closed = true;

            pl.Elevation = boundary.Elevation;
            pl.Normal = boundary.Normal;
            return pl;
        }

        /// <summary>
        /// Extracts closed rings (loops) from a Region by exploding into segments and chaining them.
        /// Useful for turning a BoolUnite/BoolIntersect result back into polygon rings.
        /// </summary>
        public static bool TryRegionToRings(
            Region region,
            double tolerance,
            out List<List<Point2d>> rings,
            out string errorMessage)
        {
            rings = new List<List<Point2d>>();
            errorMessage = null;
            if (region == null)
            {
                errorMessage = "Region is null.";
                return false;
            }

            DBObjectCollection exploded = null;
            try
            {
                exploded = new DBObjectCollection();
                region.Explode(exploded);

                var segments = ExtractSegments(exploded, tolerance, out string segErr);
                if (segments == null)
                {
                    errorMessage = segErr ?? "Could not extract segments from Region explode.";
                    return false;
                }

                var loops = ChainSegmentsToClosedLoops(segments, tolerance, out string loopErr);
                if (loops == null)
                {
                    errorMessage = loopErr ?? "Could not chain Region segments into loops.";
                    return false;
                }

                for (int i = 0; i < loops.Count; i++)
                {
                    if (loops[i] != null && loops[i].Count >= 3)
                        rings.Add(loops[i]);
                }

                if (rings.Count == 0)
                {
                    errorMessage = "Region produced no closed rings.";
                    return false;
                }

                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (System.Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                DisposeAll(exploded);
            }
        }

        private static bool HasNoBulges(Polyline pl)
        {
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(pl.GetBulgeAt(i)) > 1e-12)
                    return false;
            }
            return true;
        }

        private static Polyline RemoveConsecutiveDuplicates(Polyline src, double tol)
        {
            int n = src.NumberOfVertices;
            if (n < 3)
                return (Polyline)src.Clone();

            var pts = new List<Point2d>(n);
            for (int i = 0; i < n; i++)
            {
                var p = src.GetPoint2dAt(i);
                if (pts.Count == 0 || pts[pts.Count - 1].GetDistanceTo(p) > tol)
                    pts.Add(p);
            }

            if (pts.Count >= 2 && pts[0].GetDistanceTo(pts[pts.Count - 1]) <= tol)
                pts.RemoveAt(pts.Count - 1);

            if (pts.Count < 3)
                return (Polyline)src.Clone();

            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, pts[i], 0, 0, 0);
            pl.Closed = true;
            return pl;
        }

        private static Region FirstRegionOrNull(DBObjectCollection objs)
        {
            if (objs == null || objs.Count == 0)
                return null;
            foreach (DBObject o in objs)
            {
                if (o is Region r)
                    return r;
            }
            return null;
        }

        private static void DisposeAll(DBObjectCollection objs)
        {
            if (objs == null) return;
            foreach (DBObject o in objs)
            {
                try { o.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void DisposeAllExcept(DBObjectCollection objs, DBObject keep)
        {
            if (objs == null) return;
            foreach (DBObject o in objs)
            {
                if (ReferenceEquals(o, keep))
                    continue;
                try { o.Dispose(); } catch { /* ignore */ }
            }
        }

        private static List<(Point3d Start, Point3d End)> ExtractSegments(DBObjectCollection exploded, double tol, out string errorMessage)
        {
            errorMessage = null;
            var segs = new List<(Point3d Start, Point3d End)>();
            if (exploded == null || exploded.Count == 0)
                return segs;

            foreach (DBObject obj in exploded)
            {
                if (obj is Line ln)
                {
                    if (ln.StartPoint.DistanceTo(ln.EndPoint) > 1e-12)
                        segs.Add((ln.StartPoint, ln.EndPoint));
                }
                else if (obj is Polyline pl)
                {
                    int n = pl.NumberOfVertices;
                    if (n < 2) continue;
                    int limit = pl.Closed ? n : n - 1;
                    for (int i = 0; i < limit; i++)
                    {
                        int j = (i + 1) % n;
                        var a = pl.GetPoint3dAt(i);
                        var b = pl.GetPoint3dAt(j);
                        if (a.DistanceTo(b) > 1e-12)
                            segs.Add((a, b));
                    }
                }
                else if (obj is Arc arc)
                {
                    double sweep = arc.EndAngle - arc.StartAngle;
                    int steps = Math.Max(12, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 24.0)));
                    Point3d prev = arc.StartPoint;
                    for (int s = 1; s <= steps; s++)
                    {
                        double a = arc.StartAngle + sweep * s / steps;
                        var p = new Point3d(
                            arc.Center.X + arc.Radius * Math.Cos(a),
                            arc.Center.Y + arc.Radius * Math.Sin(a),
                            arc.Center.Z);
                        if (prev.DistanceTo(p) > tol * 1e-6)
                            segs.Add((prev, p));
                        prev = p;
                    }
                }
                else if (obj is Curve c)
                {
                    errorMessage = "Unsupported curve type in Region explode: " + c.GetType().Name;
                    return null;
                }
            }

            return segs;
        }

        private static List<List<Point2d>> ChainSegmentsToClosedLoops(
            List<(Point3d Start, Point3d End)> segments,
            double tolerance,
            out string errorMessage)
        {
            errorMessage = null;
            var loops = new List<List<Point2d>>();
            if (segments == null)
                return loops;

            var remaining = new List<(Point3d Start, Point3d End)>(segments);
            int guard = remaining.Count * 4 + 32;
            while (remaining.Count > 0 && guard-- > 0)
            {
                var (s0, e0) = remaining[0];
                remaining.RemoveAt(0);

                var ring3 = new List<Point3d> { s0, e0 };
                Point3d cur = e0;

                int localGuard = remaining.Count + 16;
                while (localGuard-- > 0)
                {
                    if (PointsEqual(cur, ring3[0], tolerance) && ring3.Count >= 4)
                        break;

                    int idx = FindNext(remaining, cur, tolerance, out bool reverse);
                    if (idx < 0)
                    {
                        errorMessage = "Could not chain Region segments into closed loops (gaps or tolerance too tight).";
                        return null;
                    }

                    var (a, b) = remaining[idx];
                    remaining.RemoveAt(idx);
                    Point3d next = reverse ? a : b;
                    ring3.Add(next);
                    cur = next;
                }

                if (!PointsEqual(ring3[0], ring3[ring3.Count - 1], tolerance))
                {
                    errorMessage = "Region loop did not close (gaps or tolerance too tight).";
                    return null;
                }

                ring3.RemoveAt(ring3.Count - 1);
                if (ring3.Count >= 3)
                {
                    var ring2 = new List<Point2d>(ring3.Count);
                    for (int i = 0; i < ring3.Count; i++)
                        ring2.Add(new Point2d(ring3[i].X, ring3[i].Y));
                    loops.Add(ring2);
                }
            }

            if (guard <= 0)
            {
                errorMessage = "Region loop chaining exceeded guard limit (unexpected segment topology).";
                return null;
            }

            return loops;
        }

        private static int FindNext(List<(Point3d Start, Point3d End)> remaining, Point3d at, double tol, out bool reverse)
        {
            reverse = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                var (s, e) = remaining[i];
                if (PointsEqual(at, s, tol))
                {
                    reverse = false;
                    return i;
                }
                if (PointsEqual(at, e, tol))
                {
                    reverse = true;
                    return i;
                }
            }
            return -1;
        }

        private static bool PointsEqual(Point3d a, Point3d b, double tol) => a.DistanceTo(b) <= tol;
    }
}

