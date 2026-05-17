using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Ensures branch polylines form a single hydraulic component rooted at the main pipe network:
    /// every branch segment must connect to main (or to another branch that ultimately connects to main)
    /// within a planar tolerance.
    /// </summary>
    public static class BranchNetworkConnectivity
    {
        /// <summary>Removes branch-layer pipe curves that are not in the same connected component as any seed trunk/main.</summary>
        /// <param name="reachableCurveObjectIds">All curve ObjectIds in that component (main + branch geometry); safe for proximity checks to heads.</param>
        public static int PruneDisconnectedBranchGeometry(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            IReadOnlyList<ObjectId> seedMainPolylineIds,
            double clusterTol,
            out HashSet<ObjectId> reachableCurveObjectIds)
        {
            reachableCurveObjectIds = new HashSet<ObjectId>();
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3
                || seedMainPolylineIds == null || seedMainPolylineIds.Count == 0)
            {
                return 0;
            }

            double connectTol = Math.Max(clusterTol * 2.0, 1e-9);

            var candidates = new List<Curve>();
            var candidateIds = new List<ObjectId>();

            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(oid, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null || ent.IsErased) continue;
                if (!EntityMatchesZoneScope(ent, zoneRing, zoneBoundaryHandleHex)) continue;

                string layer = ent.Layer ?? string.Empty;
                // Do not treat unified zone outline layers as hydraulic main pipe geometry.
                bool isMain = IsHydraulicMainPipeLayer(layer);
                bool isBranchPipe = IsBranchOnlyPolylineLayer(layer);
                if (!isMain && !isBranchPipe) continue;

                if (!(ent is Curve cv)) continue;
                if (cv is Polyline pl && pl.NumberOfVertices < 2) continue;
                if (cv is Line ln)
                {
                    var a = ln.StartPoint;
                    var b = ln.EndPoint;
                    if (a.DistanceTo(b) <= 1e-12) continue;
                }

                candidates.Add(cv);
                candidateIds.Add(oid);
            }

            if (candidates.Count == 0)
                return 0;

            var visited = new HashSet<int>();
            var queue = new Queue<int>();

            for (int s = 0; s < seedMainPolylineIds.Count; s++)
            {
                ObjectId sid = seedMainPolylineIds[s];
                if (sid.IsErased) continue;
                int idx = candidateIds.IndexOf(sid);
                if (idx < 0) continue;
                if (visited.Contains(idx)) continue;
                visited.Add(idx);
                queue.Enqueue(idx);
            }

            if (queue.Count == 0)
                return 0;

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                var a = candidates[i];
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (visited.Contains(j)) continue;
                    if (MinPlanarCurveCurveDistance(a, candidates[j]) <= connectTol)
                    {
                        visited.Add(j);
                        queue.Enqueue(j);
                    }
                }
            }

            for (int k = 0; k < candidateIds.Count; k++)
            {
                if (visited.Contains(k))
                    reachableCurveObjectIds.Add(candidateIds[k]);
            }

            int erased = 0;
            for (int k = 0; k < candidateIds.Count; k++)
            {
                if (visited.Contains(k)) continue;
                Entity ent = null;
                try { ent = tr.GetObject(candidateIds[k], OpenMode.ForWrite, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null || ent.IsErased) continue;
                string layer = ent.Layer ?? string.Empty;
                if (!IsBranchOnlyPolylineLayer(layer)) continue;

                try { ent.Erase(); } catch { /* ignore */ }
                erased++;
            }

            return erased;
        }

        /// <summary>Erases Ø MText placed by branch routing when no surviving branch curve is nearby.</summary>
        public static int EraseOrphanBranchDiameterLabels(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            HashSet<ObjectId> reachableCurveObjectIds,
            double clusterTol)
        {
            if (tr == null || ms == null) return 0;
            double labelTol = Math.Max(clusterTol * 5.0, 1e-6);
            var toErase = new List<ObjectId>();

            foreach (ObjectId oid in ms)
            {
                if (oid.IsErased) continue;
                MText mt = null;
                try { mt = tr.GetObject(oid, OpenMode.ForRead, false) as MText; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (mt == null) continue;
                if (!string.Equals(mt.Layer, SprinklerLayers.BranchLabelLayer, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!SprinklerXData.IsTaggedBranchPipeScheduleLabel(mt)) continue;
                if (!EntityMatchesZoneScope(mt, zoneRing, zoneBoundaryHandleHex)) continue;

                var ins = new Point2d(mt.Location.X, mt.Location.Y);
                if (reachableCurveObjectIds == null || reachableCurveObjectIds.Count == 0)
                {
                    toErase.Add(oid);
                    continue;
                }

                bool near = false;
                foreach (var bid in reachableCurveObjectIds)
                {
                    if (bid.IsErased) continue;
                    Curve c = null;
                    try { c = tr.GetObject(bid, OpenMode.ForRead, false) as Curve; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (c == null) continue;
                    string lyr = c.Layer ?? string.Empty;
                    if (!IsBranchOnlyPolylineLayer(lyr)) continue;
                    try
                    {
                        double z = c.StartPoint.Z;
                        var p3 = new Point3d(ins.X, ins.Y, z);
                        var cp = c.GetClosestPointTo(p3, false);
                        if (p3.DistanceTo(cp) <= labelTol) { near = true; break; }
                    }
                    catch { /* ignore */ }
                }

                if (!near)
                    toErase.Add(oid);
            }

            int n = 0;
            foreach (var eid in toErase)
            {
                if (eid.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(eid, OpenMode.ForWrite, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                try { ent.Erase(); n++; } catch { /* ignore */ }
            }

            return n;
        }

        public static bool PointNearAnyCurve(
            Transaction tr,
            Point2d p,
            IEnumerable<ObjectId> curveObjectIds,
            double tol)
        {
            if (tr == null || curveObjectIds == null) return false;
            foreach (var oid in curveObjectIds)
            {
                if (oid.IsErased) continue;
                Curve c = null;
                try { c = tr.GetObject(oid, OpenMode.ForRead, false) as Curve; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (c == null) continue;
                try
                {
                    double z = c.StartPoint.Z;
                    var pz = new Point3d(p.X, p.Y, z);
                    var cp = c.GetClosestPointTo(pz, false);
                    if (pz.DistanceTo(cp) <= tol) return true;
                }
                catch { /* ignore */ }
            }

            return false;
        }

        private static bool IsHydraulicMainPipeLayer(string layerName) =>
            !string.IsNullOrEmpty(layerName)
            && (string.Equals(layerName, SprinklerLayers.MainPipeLayer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layerName, SprinklerLayers.McdMainPipeLayer, StringComparison.OrdinalIgnoreCase));

        private static bool IsBranchOnlyPolylineLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            return string.Equals(layerName, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(layerName, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(layerName, SprinklerLayers.McdConnectorBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EntityMatchesZoneScope(Entity ent, List<Point2d> zoneRing, string boundaryHandleHex)
        {
            if (ent == null) return false;
            if (!string.IsNullOrEmpty(boundaryHandleHex)
                && SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h)
                && string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (zoneRing == null || zoneRing.Count < 3) return false;
            if (ent is Polyline pl) return PolylineHasSampleInsideZone(pl, zoneRing);
            if (ent is Line ln)
            {
                var a = ln.StartPoint; var b = ln.EndPoint;
                return PolygonUtils.PointInPolygon(zoneRing, new Point2d(a.X, a.Y))
                       || PolygonUtils.PointInPolygon(zoneRing, new Point2d(b.X, b.Y));
            }

            if (ent is MText mt)
                return PolygonUtils.PointInPolygon(zoneRing, new Point2d(mt.Location.X, mt.Location.Y));
            return false;
        }

        private static bool PolylineHasSampleInsideZone(Polyline pl, List<Point2d> zoneRing)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3) return false;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var v = pl.GetPoint2dAt(i);
                if (PolygonUtils.PointInPolygon(zoneRing, v)) return true;
            }

            for (int i = 0; i + 1 < n; i++)
            {
                var a = pl.GetPoint2dAt(i);
                var b = pl.GetPoint2dAt(i + 1);
                var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                if (PolygonUtils.PointInPolygon(zoneRing, mid)) return true;
            }

            return false;
        }

        private static double MinPlanarCurveCurveDistance(Curve a, Curve b)
        {
            if (a == null || b == null) return double.PositiveInfinity;
            double d = double.PositiveInfinity;
            d = Math.Min(d, MinDistanceCurveToCurveOneWay(a, b));
            d = Math.Min(d, MinDistanceCurveToCurveOneWay(b, a));
            return d;
        }

        private static double MinDistanceCurveToCurveOneWay(Curve from, Curve to)
        {
            double d = double.PositiveInfinity;
            if (from is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    var v = pl.GetPoint3dAt(i);
                    d = Math.Min(d, PointToCurveDist(v, to));
                    if (i + 1 < n)
                    {
                        var v0 = pl.GetPoint2dAt(i);
                        var v1 = pl.GetPoint2dAt(i + 1);
                        if ((v0 - v1).Length > 1e-6)
                        {
                            var mid = new Point3d((v0.X + v1.X) * 0.5, (v0.Y + v1.Y) * 0.5, pl.Elevation);
                            d = Math.Min(d, PointToCurveDist(mid, to));
                        }
                    }
                }
            }
            else if (from is Line ln)
            {
                d = Math.Min(d, PointToCurveDist(ln.StartPoint, to));
                d = Math.Min(d, PointToCurveDist(ln.EndPoint, to));
                var mid = new Point3d(
                    (ln.StartPoint.X + ln.EndPoint.X) * 0.5,
                    (ln.StartPoint.Y + ln.EndPoint.Y) * 0.5,
                    (ln.StartPoint.Z + ln.EndPoint.Z) * 0.5);
                d = Math.Min(d, PointToCurveDist(mid, to));
            }
            else
            {
                try
                {
                    d = Math.Min(d, PointToCurveDist(from.StartPoint, to));
                    d = Math.Min(d, PointToCurveDist(from.EndPoint, to));
                }
                catch { /* ignore */ }
            }

            return d;
        }

        private static double PointToCurveDist(Point3d p, Curve to)
        {
            try
            {
                var cp = to.GetClosestPointTo(p, false);
                return p.DistanceTo(cp);
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }
    }
}
