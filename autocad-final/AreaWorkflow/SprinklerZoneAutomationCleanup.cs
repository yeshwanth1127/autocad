using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Geometry;

namespace autocad_final.AreaWorkflow
{
    /// <summary>
    /// Removes prior automated sprinkler content for a zone so a full pipeline (route → sprinklers → branches) can re-run cleanly.
    /// </summary>
    public static class SprinklerZoneAutomationCleanup
    {
        /// <summary>
        /// Erases zone-tagged entities (including trunk) and untagged sprinkler/branch markers inside the zone polygon.
        /// Never erases <paramref name="floorBoundaryEntityId"/> (the selected floor boundary polyline).
        /// </summary>
        public static int ClearPriorAutomatedContent(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            string boundaryHandleHex,
            ObjectId floorBoundaryEntityId)
        {
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            int erased = 0;
            erased += EraseAllZoneTaggedExceptBoundaryObject(tr, ms, boundaryHandleHex, floorBoundaryEntityId);
            erased += EraseUntaggedSprinklersAndBranchesInZone(tr, ms, zoneRing);
            return erased;
        }

        /// <summary>
        /// Erases every prior zone-outline polyline (and its tagged automated content) whose first
        /// vertex lies inside <paramref name="floorRing"/>, plus any orphan MText labels on
        /// <see cref="SprinklerLayers.ZoneLabelLayer"/> inside that ring. Used by
        /// <c>create_sprinkler_zones</c> so a re-run is idempotent and does not stack new outlines
        /// on top of old ones. Never erases <paramref name="floorBoundaryEntityId"/>.
        /// </summary>
        public static int ClearPriorZoneOutlinesInsideFloor(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> floorRing,
            ObjectId floorBoundaryEntityId)
        {
            if (tr == null || ms == null || floorRing == null || floorRing.Count < 3)
                return 0;

            var outlineIds = new List<ObjectId>();
            var outlineHandles = new List<string>();

            foreach (ObjectId id in ms)
            {
                if (id == floorBoundaryEntityId) continue;
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null || ent.IsErased) continue;
                if (!(ent is Polyline pl) || !pl.Closed) continue;
                if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer) &&
                    !SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)) continue;
                // Zone-outline polylines are tagged with their OWN handle. Skip content tagged to another zone.
                if (!string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                Point2d sample;
                try { var v = pl.GetPoint3dAt(0); sample = new Point2d(v.X, v.Y); }
                catch { continue; }
                if (!PointInPolygon(floorRing, sample)) continue;

                outlineIds.Add(id);
                outlineHandles.Add(h);
            }

            int erased = 0;
            for (int i = 0; i < outlineIds.Count; i++)
            {
                Polyline pl;
                try { pl = tr.GetObject(outlineIds[i], OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null || pl.IsErased) continue;

                var zoneRing = new List<Point2d>();
                try
                {
                    int nv = pl.NumberOfVertices;
                    for (int k = 0; k < nv; k++)
                    {
                        var v = pl.GetPoint3dAt(k);
                        zoneRing.Add(new Point2d(v.X, v.Y));
                    }
                }
                catch { zoneRing = null; }
                if (zoneRing == null || zoneRing.Count < 3) continue;

                erased += ClearPriorAutomatedContent(tr, ms, zoneRing, outlineHandles[i], floorBoundaryEntityId);
            }

            foreach (ObjectId id in ms)
            {
                if (id == floorBoundaryEntityId) continue;
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (ent == null || ent.IsErased) continue;
                if (!string.Equals(ent.Layer ?? string.Empty, SprinklerLayers.ZoneLabelLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(ent is MText mt)) continue;
                var loc = mt.Location;
                if (!PointInPolygon(floorRing, new Point2d(loc.X, loc.Y))) continue;
                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseAllZoneTaggedExceptBoundaryObject(
            Transaction tr,
            BlockTableRecord ms,
            string boundaryHandleHex,
            ObjectId floorBoundaryEntityId)
        {
            int erased = 0;
            foreach (ObjectId id in ms)
            {
                if (id == floorBoundaryEntityId)
                    continue;
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseUntaggedSprinklersAndBranchesInZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing)
        {
            int erased = 0;
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;

                string layer = ent.Layer ?? string.Empty;
                bool isSprinkler = SprinklerLayers.IsSprinklerHeadEntity(tr, ent);
                bool isBranch = SprinklerLayers.IsBranchPipeGeometryLayerName(layer);
                bool isMainPipePoly = SprinklerLayers.IsMainPipeLayerName(layer)
                    && (ent is Polyline || ent is Line);
                // Unified design layer: piping and heads share the zone layer; do not erase closed zone-outline polylines here.
                bool isUnifiedAutomation =
                    SprinklerLayers.IsUnifiedZoneDesignLayerName(layer) &&
                    !(ent is Polyline outlinePl && outlinePl.Closed);
                if (!isSprinkler && !isBranch && !isUnifiedAutomation && !isMainPipePoly)
                    continue;

                bool inside = false;
                if (ent is Circle c)
                    inside = PointInPolygon(zoneRing, new Point2d(c.Center.X, c.Center.Y));
                else if (ent is BlockReference br)
                    inside = PointInPolygon(zoneRing, new Point2d(br.Position.X, br.Position.Y));
                else if (ent is Polyline pline)
                    inside = PolylineHasSampleInsideZone(pline, zoneRing);
                else if (ent is Line ln)
                {
                    var a = ln.StartPoint;
                    var b = ln.EndPoint;
                    inside =
                        PointInPolygon(zoneRing, new Point2d(a.X, a.Y)) ||
                        PointInPolygon(zoneRing, new Point2d(b.X, b.Y));
                }
                else if (ent is MText mt)
                {
                    var loc = mt.Location;
                    inside = PointInPolygon(zoneRing, new Point2d(loc.X, loc.Y));
                }

                if (!inside)
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
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
                    if (i + 1 < n)
                    {
                        var v2 = pl.GetPoint3dAt(i + 1);
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
