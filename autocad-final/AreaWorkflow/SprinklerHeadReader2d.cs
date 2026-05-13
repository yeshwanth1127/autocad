using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

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
            return TryReadSprinklerHeadPointsForZoneRouting(db, zoneRing, null, dedupeTol, out sprinklers, out errorMessage);
        }

        /// <summary>
        /// Sprinkler insertion points for branch routing: heads inside the zone ring, plus heads that lie in
        /// an <see cref="SprinklerLayers.McdFloorBoundaryLayer"/> room whose <strong>majority parent zone</strong>
        /// matches <paramref name="zoneBoundaryHandleHex"/> (handles straddling rooms).
        /// </summary>
        public static bool TryReadSprinklerHeadPointsForZoneRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
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

            if (!string.IsNullOrWhiteSpace(zoneBoundaryHandleHex))
                AppendSprinklerPointsFromRoomsParentedToZone(db, zoneRing, zoneBoundaryHandleHex.Trim(), dedupeTol, sprinklers);

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
            return TryEnumerateSprinklerHeadEntitiesInZoneForRouting(db, zoneRing, null, out heads, out errorMessage);
        }

        /// <inheritdoc cref="TryEnumerateSprinklerHeadEntitiesInZone"/>
        /// <remarks>
        /// When <paramref name="zoneBoundaryHandleHex"/> is set, also includes heads tagged to that zone that sit
        /// inside floor-boundary rooms whose majority parent is this zone (outside the zone polygon).
        /// </remarks>
        public static bool TryEnumerateSprinklerHeadEntitiesInZoneForRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
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

            if (!string.IsNullOrWhiteSpace(zoneBoundaryHandleHex))
                AppendHeadEntitiesFromRoomsParentedToZone(db, zoneRing, zoneBoundaryHandleHex.Trim(), heads);

            return true;
        }

        private static void AppendSprinklerPointsFromRoomsParentedToZone(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            double dedupeTol,
            List<Point2d> sprinklers)
        {
            if (!TryGetFloorRoomRingsParentedToZone(db, zoneRing, zoneHex, out var roomRings))
                return;

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
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) || string.IsNullOrWhiteSpace(h))
                        continue;
                    if (!string.Equals(h.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Point2d p = default;
                    if (ent is Circle c)
                        p = new Point2d(c.Center.X, c.Center.Y);
                    else if (ent is BlockReference br)
                    {
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;
                        p = new Point2d(br.Position.X, br.Position.Y);
                    }
                    else
                        continue;

                    if (PointInPolygon(zoneRing, p))
                        continue;

                    if (!PointInsideAnyRing(roomRings, p))
                        continue;

                    AddDedupe(sprinklers, p, dedupeTol);
                }

                tr.Commit();
            }
        }

        private static void AppendHeadEntitiesFromRoomsParentedToZone(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            List<(Point2d pt, ObjectId id)> heads)
        {
            if (!TryGetFloorRoomRingsParentedToZone(db, zoneRing, zoneHex, out var roomRings))
                return;

            var seen = new HashSet<ObjectId>();
            for (int i = 0; i < heads.Count; i++)
                seen.Add(heads[i].id);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (seen.Contains(id)) continue;

                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;

                    if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                        continue;
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) || string.IsNullOrWhiteSpace(h))
                        continue;
                    if (!string.Equals(h.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Point2d p = default;
                    if (ent is Circle c)
                        p = new Point2d(c.Center.X, c.Center.Y);
                    else if (ent is BlockReference br)
                    {
                        if (!SprinklerLayers.IsPendentSprinklerBlock(tr, br))
                            continue;
                        p = new Point2d(br.Position.X, br.Position.Y);
                    }
                    else
                        continue;

                    if (PointInPolygon(zoneRing, p))
                        continue;

                    if (!PointInsideAnyRing(roomRings, p))
                        continue;

                    heads.Add((p, id));
                    seen.Add(id);
                }

                tr.Commit();
            }
        }

        /// <summary>
        /// Floor-boundary room rings whose majority parent zone handle matches <paramref name="zoneHex"/>
        /// and whose bbox overlaps the given <paramref name="zoneRing"/> bbox.
        /// </summary>
        public static bool TryGetFloorRoomRingsParentedToZone(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            out List<List<Point2d>> roomRings)
        {
            roomRings = new List<List<Point2d>>();
            PolygonUtils.GetBoundingBox(zoneRing, out double zminX, out double zminY, out double zmaxX, out double zmaxY);

            var floorRoomIds = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId rid in ms)
                {
                    if (rid.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(rid, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null || !pl.Closed || pl.NumberOfVertices < 3) continue;

                    string lay = pl.Layer ?? string.Empty;
                    if (!string.Equals(lay.Trim(), SprinklerLayers.McdFloorBoundaryLayer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var rr = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                    if (rr == null || rr.Count < 3) continue;

                    PolygonUtils.GetBoundingBox(rr, out double rminX, out double rminY, out double rmaxX, out double rmaxY);
                    if (rmaxX < zminX || rminX > zmaxX || rmaxY < zminY || rminY > zmaxY)
                        continue;

                    floorRoomIds.Add(rid);
                }

                tr.Commit();
            }

            foreach (ObjectId rid in floorRoomIds)
            {
                Polyline plClone = null;
                try
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var pl = tr.GetObject(rid, OpenMode.ForRead, false) as Polyline;
                        if (pl == null)
                        {
                            tr.Commit();
                            continue;
                        }

                        plClone = (Polyline)pl.Clone();
                        tr.Commit();
                    }

                    if (!RoomParentZoneResolver.TryGetMajorityParentZoneForRoomOutline(db, plClone, out _, out string phex, out _))
                        continue;
                    if (string.IsNullOrWhiteSpace(phex) ||
                        !string.Equals(phex.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(plClone);
                    if (ring != null && ring.Count >= 3)
                        roomRings.Add(ring);
                }
                finally
                {
                    try { plClone?.Dispose(); } catch { /* ignore */ }
                }
            }

            return roomRings.Count > 0;
        }

        private static bool PointInsideAnyRing(List<List<Point2d>> rings, Point2d p)
        {
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] != null && FindShaftsInsideBoundary.IsPointInPolygonRing(rings[i], p))
                    return true;
            }

            return false;
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
