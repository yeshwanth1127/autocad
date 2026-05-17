using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    public static class SprinklerHeadReader2d
    {
        /// <summary>Cached result of <see cref="BuildFloorRoomOwnerships"/>; pass through hot paths to avoid repeated DB scans.</summary>
        public sealed class FloorRoomOwnership
        {
            public List<Point2d> Ring;
            public string ParentZoneHex;
            public double Area;
        }

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
            => TryReadSprinklerHeadPointsForZoneRouting(
                db, zoneRing, zoneBoundaryHandleHex, dedupeTol, floorRoomOwnerships: null, out sprinklers, out errorMessage);

        /// <param name="floorRoomOwnerships">When non-null, skips rebuilding floor-room ownership (saves a full DB scan).</param>
        public static bool TryReadSprinklerHeadPointsForZoneRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            double dedupeTol,
            List<FloorRoomOwnership> floorRoomOwnerships,
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

            string zoneHex = string.IsNullOrWhiteSpace(zoneBoundaryHandleHex) ? null : zoneBoundaryHandleHex.Trim();
            var roomOwnerships = !string.IsNullOrWhiteSpace(zoneHex)
                ? (floorRoomOwnerships ?? BuildFloorRoomOwnerships(db))
                : null;

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

                    Point2d p;
                    if (ent is Circle c)
                        p = new Point2d(c.Center.X, c.Center.Y);
                    else if (ent is BlockReference br)
                        p = new Point2d(br.Position.X, br.Position.Y);
                    else
                        continue;

                    if (!PointBelongsToZoneForRouting(
                            zoneRing, zoneHex, roomOwnerships, p,
                            Math.Max(dedupeTol, BoundaryProximityTolRouting(db)), ent))
                        continue;
                    AddDedupe(sprinklers, p, dedupeTol);
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
            => TryEnumerateSprinklerHeadEntitiesInZoneForRouting(
                db, zoneRing, zoneBoundaryHandleHex, floorRoomOwnerships: null, out heads, out errorMessage);

        /// <param name="floorRoomOwnerships">When non-null, skips rebuilding floor-room ownership (saves a full DB scan).</param>
        public static bool TryEnumerateSprinklerHeadEntitiesInZoneForRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneBoundaryHandleHex,
            List<FloorRoomOwnership> floorRoomOwnerships,
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

            string zoneHex = string.IsNullOrWhiteSpace(zoneBoundaryHandleHex) ? null : zoneBoundaryHandleHex.Trim();
            var roomOwnerships = !string.IsNullOrWhiteSpace(zoneHex)
                ? (floorRoomOwnerships ?? BuildFloorRoomOwnerships(db))
                : null;

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

                    Point2d p;
                    if (ent is Circle c)
                        p = new Point2d(c.Center.X, c.Center.Y);
                    else if (ent is BlockReference br)
                        p = new Point2d(br.Position.X, br.Position.Y);
                    else
                        continue;

                    if (!PointBelongsToZoneForRouting(
                            zoneRing, zoneHex, roomOwnerships, p, BoundaryProximityTolRouting(db), ent))
                        continue;
                    heads.Add((p, id));
                }

                tr.Commit();
            }

            return true;
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

            var rooms = BuildFloorRoomOwnerships(db);
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                if (room == null || room.Ring == null || room.Ring.Count < 3)
                    continue;
                if (string.IsNullOrWhiteSpace(room.ParentZoneHex) ||
                    !string.Equals(room.ParentZoneHex.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                PolygonUtils.GetBoundingBox(room.Ring, out double rminX, out double rminY, out double rmaxX, out double rmaxY);
                if (rmaxX < zminX || rminX > zmaxX || rmaxY < zminY || rminY > zmaxY)
                    continue;

                roomRings.Add(room.Ring);
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

        /// <summary>
        /// True if <paramref name="p"/> lies inside a <see cref="SprinklerLayers.McdFloorBoundaryLayer"/> room
        /// whose majority parent zone matches <paramref name="zoneHex"/> (same rings as
        /// <see cref="TryGetFloorRoomRingsParentedToZone"/>). Used so redesign cleanup matches routing inclusion.
        /// </summary>
        public static bool IsPointInsideFloorRoomParentedToZone(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            Point2d p,
            List<FloorRoomOwnership> floorRoomOwnerships = null)
        {
            if (db == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrWhiteSpace(zoneHex))
                return false;
            var rooms = floorRoomOwnerships ?? BuildFloorRoomOwnerships(db);
            if (!TryFindContainingRoomOwner(rooms, p, out string ownerHex))
                return false;
            return string.Equals(ownerHex, zoneHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Union of the zone polygon and floor-boundary rooms parented to <paramref name="zoneHex"/> (same footprint
        /// notion as branch clip rings in attach-branches). Used for redesign cleanup so branches to straddling-room heads are not wiped.
        /// </summary>
        public static bool IsPointInZoneOrParentedFloorRoomForRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            Point2d p,
            List<FloorRoomOwnership> floorRoomOwnerships = null)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return false;
            if (db == null || string.IsNullOrWhiteSpace(zoneHex))
                return ZoneContainsPointTolerant(zoneRing, p, BoundaryProximityTolRouting(db));
            return IsPointOwnedByZoneForRouting(db, zoneRing, zoneHex, p, null, floorRoomOwnerships);
        }

        /// <summary>
        /// A point inside a room outline belongs only to that room's majority parent zone. Points outside rooms
        /// still use normal zone polygon containment.
        /// </summary>
        /// <param name="associatedHeadEntity">When set, honors sprinkler XData zone tag before geometric checks.</param>
        /// <param name="floorRoomOwnerships">When non-null, avoids rebuilding floor-room ownership for every call.</param>
        public static bool IsPointOwnedByZoneForRouting(
            Database db,
            List<Point2d> zoneRing,
            string zoneHex,
            Point2d p,
            Entity associatedHeadEntity = null,
            List<FloorRoomOwnership> floorRoomOwnerships = null)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return false;
            if (db == null || string.IsNullOrWhiteSpace(zoneHex))
                return ZoneContainsPointTolerant(zoneRing, p, BoundaryProximityTolRouting(db));
            var rooms = floorRoomOwnerships ?? BuildFloorRoomOwnerships(db);
            return PointBelongsToZoneForRouting(
                zoneRing,
                zoneHex.Trim(),
                rooms,
                p,
                BoundaryProximityTolRouting(db),
                associatedHeadEntity);
        }

        private static double BoundaryProximityTolRouting(Database db)
        {
            if (db == null) return 1e-6;
            try
            {
                double t = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                return Math.Max(t * 50.0, 1e-5);
            }
            catch
            {
                return 1e-6;
            }
        }

        private static bool ZoneContainsPointTolerant(List<Point2d> zoneRing, Point2d p, double boundaryTolDu)
        {
            if (zoneRing == null || zoneRing.Count < 3)
                return false;
            double tol = boundaryTolDu > 0 ? boundaryTolDu : 1e-6;
            if (PolygonUtils.PointInPolygon(zoneRing, p))
                return true;
            double d = PolygonUtils.MinDistancePointToPolygonBoundary(zoneRing, p);
            return !double.IsPositiveInfinity(d) && d <= tol;
        }

        private static bool PointBelongsToZoneForRouting(
            List<Point2d> zoneRing,
            string zoneHex,
            List<FloorRoomOwnership> roomOwnerships,
            Point2d p,
            double boundaryTolDu,
            Entity ent)
        {
            if (!string.IsNullOrWhiteSpace(zoneHex) && ent != null &&
                SprinklerXData.TryGetZoneBoundaryHandle(ent, out string hx) &&
                !string.IsNullOrWhiteSpace(hx) &&
                string.Equals(hx.Trim(), zoneHex.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            // Zone polygon containment (interior, boundary strip, or slight numerical offset) qualifies first.
            if (ZoneContainsPointTolerant(zoneRing, p, boundaryTolDu))
                return true;

            if (!string.IsNullOrWhiteSpace(zoneHex) &&
                TryFindContainingRoomOwner(roomOwnerships, p, out string ownerHex))
                return string.Equals(ownerHex, zoneHex.Trim(), StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static bool TryFindContainingRoomOwner(List<FloorRoomOwnership> rooms, Point2d p, out string ownerHex)
        {
            ownerHex = null;
            if (rooms == null || rooms.Count == 0)
                return false;

            double bestArea = double.PositiveInfinity;
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                if (room == null || room.Ring == null || room.Ring.Count < 3)
                    continue;
                if (string.IsNullOrWhiteSpace(room.ParentZoneHex))
                    continue;
                if (!FindShaftsInsideBoundary.IsPointInPolygonRing(room.Ring, p))
                    continue;
                double area = room.Area;
                if (!(area > 0) || double.IsInfinity(area) || double.IsNaN(area))
                    area = double.PositiveInfinity;
                if (area < bestArea)
                {
                    bestArea = area;
                    ownerHex = room.ParentZoneHex.Trim();
                }
            }

            return !string.IsNullOrWhiteSpace(ownerHex);
        }

        /// <summary>
        /// Scans floor-boundary room outlines and resolves majority parent zones (expensive — cache per command when possible).
        /// </summary>
        public static List<FloorRoomOwnership> BuildFloorRoomOwnerships(Database db)
        {
            var result = new List<FloorRoomOwnership>();
            if (db == null)
                return result;

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

                    var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(plClone);
                    if (ring == null || ring.Count < 3)
                        continue;
                    if (!RoomParentZoneResolver.TryGetMajorityParentZoneForRoomOutline(db, plClone, out _, out string phex, out _))
                        continue;
                    if (string.IsNullOrWhiteSpace(phex))
                        continue;

                    double area = double.PositiveInfinity;
                    try { area = Math.Abs(plClone.Area); } catch { /* ignore */ }
                    result.Add(new FloorRoomOwnership
                    {
                        Ring = ring,
                        ParentZoneHex = phex.Trim(),
                        Area = area
                    });
                }
                finally
                {
                    try { plClone?.Dispose(); } catch { /* ignore */ }
                }
            }

            return RemoveOuterFloorParcelFromRoomOwnerships(result);
        }

        /// <summary>
        /// The building parcel on <see cref="SprinklerLayers.McdFloorBoundaryLayer"/> must not participate in
        /// "room owns this zone" logic: sprinklers in corridors (inside the parcel only, not in an inner room loop)
        /// would otherwise all inherit one majority zone for the whole footprint, and attach-branches / redesign
        /// would route branches for heads across the entire floor when working on that zone.
        /// </summary>
        private static List<FloorRoomOwnership> RemoveOuterFloorParcelFromRoomOwnerships(List<FloorRoomOwnership> rooms)
        {
            if (rooms == null || rooms.Count == 0)
                return new List<FloorRoomOwnership>();
            if (rooms.Count == 1)
                return rooms;

            int outerIdx = -1;
            double bestArea = -1;
            for (int i = 0; i < rooms.Count; i++)
            {
                var cand = rooms[i];
                if (cand.Ring == null || cand.Ring.Count < 3)
                    continue;

                double a = cand.Area;
                if (!(a > 0) || double.IsInfinity(a) || double.IsNaN(a))
                    a = 0;

                bool containsEveryOther = true;
                for (int j = 0; j < rooms.Count; j++)
                {
                    if (i == j) continue;
                    var inner = rooms[j];
                    if (inner.Ring == null || inner.Ring.Count < 3)
                    {
                        containsEveryOther = false;
                        break;
                    }
                    var testPt = inner.Ring[0];
                    if (!FindShaftsInsideBoundary.IsPointInPolygonRing(cand.Ring, testPt))
                    {
                        containsEveryOther = false;
                        break;
                    }
                }

                if (containsEveryOther && a > bestArea)
                {
                    bestArea = a;
                    outerIdx = i;
                }
            }

            if (outerIdx < 0)
                return rooms;

            var filtered = new List<FloorRoomOwnership>(rooms.Count - 1);
            for (int i = 0; i < rooms.Count; i++)
            {
                if (i != outerIdx)
                    filtered.Add(rooms[i]);
            }

            return filtered;
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

    }
}
