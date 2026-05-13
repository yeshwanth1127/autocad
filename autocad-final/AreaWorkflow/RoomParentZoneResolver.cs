using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Resolves which MCD / legacy zone "owns" a room outline for tagging and shaft inheritance.
    /// Picks the zone where the <strong>majority of the room interior</strong> lies (grid sampling in the room bbox),
    /// so a room that straddles two zone boundaries still gets a single parent zone.
    /// </summary>
    public static class RoomParentZoneResolver
    {
        /// <summary>
        /// Majority parent zone for a room outline (same rule as <see cref="TryResolveParentZoneForRoom"/>)
        /// without requiring a resolvable shaft. Used to extend branch routing to heads that sit outside the
        /// zone polygon but inside a room that still parents to this zone.
        /// </summary>
        public static bool TryGetMajorityParentZoneForRoomOutline(
            Database db,
            Polyline roomOutline,
            out ObjectId zoneBoundaryId,
            out string zoneBoundaryHandleHex,
            out string errorMessage)
        {
            zoneBoundaryId = ObjectId.Null;
            zoneBoundaryHandleHex = null;
            errorMessage = null;

            if (db == null || roomOutline == null || !roomOutline.Closed || roomOutline.NumberOfVertices < 3)
            {
                errorMessage = "Room outline must be a closed polyline.";
                return false;
            }

            var roomRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(roomOutline);
            if (roomRing == null || roomRing.Count < 3)
            {
                errorMessage = "Could not sample room boundary.";
                return false;
            }

            PolygonUtils.GetBoundingBox(roomRing, out double rminX, out double rminY, out double rmaxX, out double rmaxY);
            double rdx = rmaxX - rminX;
            double rdy = rmaxY - rminY;
            if (!(rdx > 1e-12) && !(rdy > 1e-12))
            {
                errorMessage = "Room outline has negligible extent.";
                return false;
            }

            if (!TryBuildMajorityParentZonePick(
                    db,
                    roomRing,
                    rminX,
                    rminY,
                    rmaxX,
                    rmaxY,
                    out zoneBoundaryId,
                    out zoneBoundaryHandleHex,
                    out errorMessage))
                return false;

            return true;
        }

        /// <summary>
        /// Chooses the zone polygon that covers the largest share of the room interior (sampled on a grid),
        /// then verifies a shaft can be resolved for that zone. Ties favor the smallest zone polygon area.
        /// </summary>
        public static bool TryResolveParentZoneForRoom(
            Database db,
            Polyline roomOutline,
            out ObjectId zoneBoundaryId,
            out string zoneBoundaryHandleHex,
            out string errorMessage)
        {
            zoneBoundaryId = ObjectId.Null;
            zoneBoundaryHandleHex = null;
            errorMessage = null;

            if (!TryGetMajorityParentZoneForRoomOutline(db, roomOutline, out zoneBoundaryId, out zoneBoundaryHandleHex, out errorMessage))
                return false;

            Polyline zoneForShaft = null;
            using (var tr2 = db.TransactionManager.StartTransaction())
            {
                var pl = tr2.GetObject(zoneBoundaryId, OpenMode.ForRead, false) as Polyline;
                if (pl == null)
                {
                    errorMessage = "Parent zone polyline was erased.";
                    tr2.Commit();
                    return false;
                }

                try { zoneForShaft = (Polyline)pl.Clone(); }
                catch
                {
                    errorMessage = "Failed to clone parent zone boundary.";
                    tr2.Commit();
                    return false;
                }

                tr2.Commit();
            }

            try
            {
                if (!FindShaftsInsideBoundary.TryGetShaftForZone(db, zoneBoundaryHandleHex, zoneForShaft, out _, out string shaftErr))
                {
                    errorMessage =
                        "Parent zone has no resolvable shaft (" + (shaftErr ?? "unknown") + "). " +
                        "Assign a shaft to the zone or place a shaft block, then retry.";
                    return false;
                }
            }
            finally
            {
                try { zoneForShaft?.Dispose(); } catch { /* ignore */ }
            }

            return true;
        }

        private static bool TryBuildMajorityParentZonePick(
            Database db,
            List<Point2d> roomRing,
            double rminX,
            double rminY,
            double rmaxX,
            double rmaxY,
            out ObjectId zoneBoundaryId,
            out string zoneBoundaryHandleHex,
            out string errorMessage)
        {
            zoneBoundaryId = ObjectId.Null;
            zoneBoundaryHandleHex = null;
            errorMessage = null;

            var candidates = new List<(ObjectId id, List<Point2d> ring, double zoneArea)>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null || !pl.Closed || pl.NumberOfVertices < 3) continue;

                    string layer = pl.Layer ?? string.Empty;
                    if (string.Equals(layer.Trim(), SprinklerLayers.ZoneGlobalBoundaryLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!SprinklerLayers.IsMcdZoneOutlineLayerName(layer) && !SprinklerLayers.IsUnifiedZoneDesignLayerName(layer))
                        continue;

                    var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                    if (zoneRing == null || zoneRing.Count < 3)
                        continue;

                    PolygonUtils.GetBoundingBox(zoneRing, out double zminX, out double zminY, out double zmaxX, out double zmaxY);
                    if (zmaxX < rminX || zminX > rmaxX || zmaxY < rminY || zminY > rmaxY)
                        continue;

                    double area = double.PositiveInfinity;
                    try { area = Math.Abs(pl.Area); } catch { area = double.PositiveInfinity; }
                    if (!(area > 0) || double.IsInfinity(area))
                        continue;

                    candidates.Add((id, zoneRing, area));
                }

                tr.Commit();
            }

            if (candidates.Count == 0)
            {
                errorMessage =
                    "No zone boundary overlaps this room. Draw zone boundaries on \"" + SprinklerLayers.McdZoneBoundaryLayer +
                    "\" (or legacy \"" + SprinklerLayers.ZoneLayer + "\") so at least one zone intersects the room outline.";
                return false;
            }

            if (!TryPickZoneWithMajorityRoomOverlap(
                    roomRing,
                    rminX,
                    rminY,
                    rmaxX,
                    rmaxY,
                    candidates,
                    out ObjectId bestId,
                    out errorMessage))
                return false;

            using (var tr2 = db.TransactionManager.StartTransaction())
            {
                var pl = tr2.GetObject(bestId, OpenMode.ForRead, false) as Polyline;
                if (pl == null)
                {
                    errorMessage = "Parent zone polyline was erased.";
                    tr2.Commit();
                    return false;
                }

                try { zoneBoundaryHandleHex = pl.Handle.ToString(); }
                catch
                {
                    errorMessage = "Could not read parent zone handle.";
                    tr2.Commit();
                    return false;
                }

                tr2.Commit();
            }

            zoneBoundaryId = bestId;
            return true;
        }

        /// <summary>
        /// Grid samples the room bbox; each sample inside the room polygon votes for every zone that also contains that point.
        /// The zone with the most votes wins; ties break to the smallest <paramref name="zoneArea"/>.
        /// </summary>
        private static bool TryPickZoneWithMajorityRoomOverlap(
            List<Point2d> roomRing,
            double rminX,
            double rminY,
            double rmaxX,
            double rmaxY,
            List<(ObjectId id, List<Point2d> ring, double zoneArea)> candidates,
            out ObjectId bestId,
            out string errorMessage)
        {
            bestId = ObjectId.Null;
            errorMessage = null;

            double rdx = rmaxX - rminX;
            double rdy = rmaxY - rminY;
            double extent = Math.Max(rdx, rdy);
            double cell = extent / 32.0;
            if (cell < extent / 200.0)
                cell = extent / 200.0;
            if (!(cell > 1e-12))
                cell = 1.0;

            int n = candidates.Count;
            var counts = new int[n];

            for (double x = rminX; x <= rmaxX + 1e-9; x += cell)
            {
                for (double y = rminY; y <= rmaxY + 1e-9; y += cell)
                {
                    var p = new Point2d(x, y);
                    if (!FindShaftsInsideBoundary.IsPointInPolygonRing(roomRing, p))
                        continue;

                    for (int i = 0; i < n; i++)
                    {
                        if (FindShaftsInsideBoundary.IsPointInPolygonRing(candidates[i].ring, p))
                            counts[i]++;
                    }
                }
            }

            bool anySample = false;
            for (int i = 0; i < n; i++)
            {
                if (counts[i] > 0)
                {
                    anySample = true;
                    break;
                }
            }

            if (!anySample)
            {
                Point2d c = RingVertexAverage(roomRing);
                if (FindShaftsInsideBoundary.IsPointInPolygonRing(roomRing, c))
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (FindShaftsInsideBoundary.IsPointInPolygonRing(candidates[i].ring, c))
                            counts[i]++;
                    }
                }
            }

            int bestIdx = -1;
            int bestCount = -1;
            double bestArea = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                int c = counts[i];
                if (c <= 0) continue;
                double za = candidates[i].zoneArea;
                if (c > bestCount || (c == bestCount && za < bestArea))
                {
                    bestCount = c;
                    bestArea = za;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0 || bestCount <= 0)
            {
                errorMessage =
                    "Could not determine which zone contains the room interior. " +
                    "Ensure zone outlines overlap the room and are closed polylines.";
                return false;
            }

            bestId = candidates[bestIdx].id;
            return true;
        }

        private static Point2d RingVertexAverage(List<Point2d> ring)
        {
            double sx = 0, sy = 0;
            int n = ring?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                sx += ring[i].X;
                sy += ring[i].Y;
            }

            double d = Math.Max(1, n);
            return new Point2d(sx / d, sy / d);
        }
    }
}
