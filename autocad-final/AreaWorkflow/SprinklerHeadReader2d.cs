using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class SprinklerHeadReader2d
    {
        public static bool TryReadSprinklerHeadPoints(
            Database db,
            List<Point2d> zoneRing,
            double dedupeTol,
            out List<Point2d> sprinklers,
            out string errorMessage)
        {
            sprinklers = new List<Point2d>();
            errorMessage = null;

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }
            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone boundary ring is invalid.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;

                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                        continue;

                    if (ent is Circle c)
                    {
                        var p = new Point2d(c.Center.X, c.Center.Y);
                        if (!PointInPolygon(zoneRing, p))
                            continue;
                        AddDedupe(sprinklers, p, dedupeTol);
                    }
                    else if (ent is BlockReference br)
                    {
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;

                        var pos = br.Position;
                        var p = new Point2d(pos.X, pos.Y);
                        if (!PointInPolygon(zoneRing, p))
                            continue;
                        AddDedupe(sprinklers, p, dedupeTol);
                    }
                }

                tr.Commit();
            }

            return true;
        }

        /// <summary>
        /// Every sprinkler head entity inside the zone ring (no coordinate deduplication). Used for per-head highlighting.
        /// </summary>
        public static bool TryEnumerateSprinklerHeadEntitiesInZone(
            Database db,
            List<Point2d> zoneRing,
            out List<(Point2d pt, ObjectId id)> heads,
            out string errorMessage)
        {
            heads = new List<(Point2d, ObjectId)>();
            errorMessage = null;

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }
            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Zone boundary ring is invalid.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;

                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                        continue;

                    if (ent is Circle c)
                    {
                        var p = new Point2d(c.Center.X, c.Center.Y);
                        if (!PointInPolygon(zoneRing, p))
                            continue;
                        heads.Add((p, id));
                    }
                    else if (ent is BlockReference br)
                    {
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;

                        var pos = br.Position;
                        var p = new Point2d(pos.X, pos.Y);
                        if (!PointInPolygon(zoneRing, p))
                            continue;
                        heads.Add((p, id));
                    }
                }

                tr.Commit();
            }

            return true;
        }

        private static void AddDedupe(List<Point2d> pts, Point2d p, double tol)
        {
            double t = tol > 0 ? tol : 1e-6;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].GetDistanceTo(p) <= t)
                    return;
            }
            pts.Add(p);
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

