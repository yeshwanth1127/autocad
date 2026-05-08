using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Finds the tagged main trunk polyline in a zone for trunk-anchored sprinkler placement.
    /// </summary>
    public static class SprinklerTrunkLocator
    {
        public static bool TryFindTaggedTrunkInZone(Database db, List<Point2d> zoneRing, out ObjectId trunkId, out string errorMessage)
        {
            trunkId = ObjectId.Null;
            errorMessage = null;

            var trunks = new List<ObjectId>();
            var candidates = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;
                    if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                        continue;
                    if (!(ent is Polyline pl))
                        continue;
                    if (!PolylineHasSampleInsideZone(pl, zoneRing))
                        continue;

                    candidates.Add(id);
                    if (SprinklerXData.IsTaggedTrunkCap(pl) || SprinklerXData.IsTaggedConnector(pl))
                        continue;
                    if (!SprinklerXData.IsTaggedTrunk(pl))
                        continue;
                    trunks.Add(id);
                }

                if (trunks.Count == 0 && candidates.Count > 0)
                {
                    ObjectId bestAutoId = ObjectId.Null;
                    double bestLen = -1;

                    double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                    if (tol <= 0) tol = 1e-6;

                    foreach (var id in candidates)
                    {
                        Polyline pl = null;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased) { continue; }
                        if (pl == null) continue;

                        double len = 0;
                        try { len = pl.Length; } catch { len = 0; }
                        if (len > bestLen)
                        {
                            bestLen = len;
                            bestAutoId = id;
                        }
                    }

                    if (!bestAutoId.IsNull)
                    {
                        Polyline trunkPl = null;
                        try { trunkPl = tr.GetObject(bestAutoId, OpenMode.ForWrite, false) as Polyline; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased) { trunkPl = null; }
                        if (trunkPl != null)
                        {
                            SprinklerXData.EnsureRegApp(tr, db);
                            SprinklerXData.TagAsTrunk(trunkPl);
                            trunks.Add(bestAutoId);
                        }
                    }
                }

                tr.Commit();
            }

            if (trunks.Count == 0)
            {
                errorMessage =
                    "No trunk found for this zone. Ensure there is a main pipe polyline inside the selected zone " +
                    "(layer \"" + SprinklerLayers.ZoneLayer + "\" / legacy \"" + SprinklerLayers.MainPipeLayer + "\").";
                return false;
            }

            double best = -1;
            ObjectId bestId = trunks[0];
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in trunks)
                {
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    double len = 0;
                    try { len = pl.Length; } catch { len = 0; }
                    if (len > best)
                    {
                        best = len;
                        bestId = id;
                    }
                }
                tr.Commit();
            }

            trunkId = bestId;
            return true;
        }

        public static bool TryReadStraightAxisAlignedTrunkInfo(Database db, ObjectId trunkId, out bool trunkHorizontal, out double trunkAxis, out string errorMessage)
        {
            trunkHorizontal = true;
            trunkAxis = 0;
            errorMessage = null;
            if (trunkId.IsNull || trunkId.IsErased || !trunkId.IsValid)
            {
                errorMessage = "Main trunk reference is no longer valid. Re-select the trunk and retry.";
                return false;
            }

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!DbObjectSafeAccess.TryGetObject(tr, trunkId, OpenMode.ForRead, out Polyline pl))
                {
                    errorMessage = "Main trunk was erased by Undo. Re-select the trunk and retry.";
                    return false;
                }

                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                int nv = pl.NumberOfVertices;
                for (int i = 0; i < nv; i++)
                {
                    var p = pl.GetPoint3dAt(i);
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
                double spanX = maxX - minX;
                double spanY = maxY - minY;
                bool isVertical = spanX <= tol * 10.0 && spanY > tol * 10.0;
                bool isHorizontal = spanY <= tol * 10.0 && spanX > tol * 10.0;
                if (!isVertical && !isHorizontal)
                {
                    errorMessage = "Trunk must be a straight axis-aligned polyline (horizontal or vertical).";
                    return false;
                }

                trunkHorizontal = isHorizontal;
                trunkAxis = trunkHorizontal ? 0.5 * (minY + maxY) : 0.5 * (minX + maxX);

                tr.Commit();
                return true;
            }
        }

        private static bool PolylineHasSampleInsideZone(Polyline pl, List<Point2d> zoneRing)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;
            try
            {
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    var v = pl.GetPoint3dAt(i);
                    if (PointInPolygon(zoneRing, new Point2d(v.X, v.Y)))
                        return true;
                    int j = (i + 1) % n;
                    if (!pl.Closed && i == n - 1) break;
                    if (j < n)
                    {
                        var v2 = pl.GetPoint3dAt(j);
                        var mid = new Point2d((v.X + v2.X) * 0.5, (v.Y + v2.Y) * 0.5);
                        if (PointInPolygon(zoneRing, mid))
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
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
    }
}
