using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autocad_final.AreaWorkflow;
using autocad_final.Commands;
using autocad_final.Geometry;

namespace autocad_final.Workflows.Zoning
{
    /// <summary>
    /// Fix zones: post-processes Voronoi (or similar) zone polylines into strictly orthogonal (axis-aligned)
    /// closed rings — dominant-direction edge snap with midpoint supporting lines, vertex merge, clip-to-floor,
    /// then redraws zone polylines and rebuilds the global boundary.
    /// </summary>
    public static class FixZonesWorkflow
    {
        /// <summary>
        /// Auto-discovers zone-outline polylines inside the floor (legacy behaviour).
        /// </summary>
        public static bool TryRun(Document doc, Polyline floorBoundary, ObjectId floorBoundaryEntityId, out string message)
            => TryRun(doc, floorBoundary, floorBoundaryEntityId, explicitZonePolylineIds: null, out message);

        /// <summary>
        /// When explicit IDs are provided, only those closed polylines are orthogonalized; otherwise zone outlines
        /// are auto-discovered on the unified zone layer inside the floor.
        /// </summary>
        /// <param name="explicitZonePolylineIds">When non-null and non-empty, seeds the fix from these entities only.</param>
        public static bool TryRun(
            Document doc,
            Polyline floorBoundary,
            ObjectId floorBoundaryEntityId,
            IReadOnlyList<ObjectId> explicitZonePolylineIds,
            out string message)
        {
            message = null;
            if (doc == null) { message = "No document."; return false; }
            if (floorBoundary == null) { message = "No boundary selected."; return false; }

            var db = doc.Database;
            var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(floorBoundary);
            if (ring == null || ring.Count < 3) { message = "Selected boundary is invalid."; return false; }

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;

            var inputRings = CollectInputZoneRings(doc, ring, explicitZonePolylineIds);
            if (inputRings.Count == 0)
            {
                message = explicitZonePolylineIds != null && explicitZonePolylineIds.Count > 0
                    ? "None of the selected polylines are valid zone rings inside the floor boundary."
                    : "No zone-outline polylines found inside the floor boundary.";
                return false;
            }

            var processedRings = new List<List<Point2d>>();
            int failedCount = 0;
            string lastFail = null;
            foreach (var rin in inputRings)
            {
                if (OrthogonalVoronoiPostProcessor2d.TryOrthogonalizeClipAndValidate(rin, floorBoundary, tol, out var rout, out string oneErr))
                    processedRings.Add(rout);
                else
                {
                    failedCount++;
                    if (!string.IsNullOrWhiteSpace(oneErr))
                        lastFail = oneErr;
                }
            }

            if (processedRings.Count == 0)
            {
                message = "Fix zones failed: orthogonal post-process produced no valid zones." +
                          (failedCount > 0 && !string.IsNullOrWhiteSpace(lastFail)
                              ? (" Last error: " + lastFail)
                              : string.Empty);
                return false;
            }

            string overlapWarning = string.Empty;
            if (OrthogonalVoronoiPostProcessor2d.TryDetectPossibleOverlaps(processedRings, out var overlapPairs) &&
                overlapPairs.Count > 0)
            {
                overlapWarning = " Possible overlaps: " + overlapPairs.Count.ToString(CultureInfo.InvariantCulture) +
                                 " centroid-in-other-zone cases (review if unexpected).";
            }

            FindShaftsInsideBoundary.GetShaftHandlesAndPositionsInsideBoundary(db, floorBoundary, out var shafts3, out var shaftHandlesRaw);
            ShaftVoronoiZonesOnFloorPolyline.DedupeShaftSitesWithHandles(
                shafts3, shaftHandlesRaw, tol,
                out var shaftSites,
                out var shaftHandlesDeduped);

            var ringsWithOwner = new List<(List<Point2d> Ring, int Owner)>();
            for (int i = 0; i < processedRings.Count; i++)
            {
                var r = processedRings[i];
                int owner = NearestShaftIndex(PolygonUtils.ApproxCentroidAreaWeighted(r), shaftSites);
                if (owner < 0)
                    continue;
                ringsWithOwner.Add((r, owner));
            }

            if (ringsWithOwner.Count == 0)
            {
                message = "Fix zones: no fixed regions contained any shafts.";
                return false;
            }

            // 4) Remove previous zone outlines + labels in this floor so the output is clean.
            EraseOldZoneOutlinesAndLabels(doc, ring);

            // 5) Draw straight zone outlines on the standard zone layer and rebuild the global boundary from them.
            var zoneRings = new List<List<Point2d>>(ringsWithOwner.Count);
            var zoneTable = new List<ZoneTableEntry>(ringsWithOwner.Count);
            for (int i = 0; i < ringsWithOwner.Count; i++)
            {
                var rr = ringsWithOwner[i].Ring;
                int owner = ringsWithOwner[i].Owner;
                double aDu = PolygonVerticalHalfPlaneClip2d.AbsArea(rr);
                double? aM2 = DrawingUnitsHelper.TryGetAreaSquareMeters(db, aDu, out _);
                zoneRings.Add(rr);
                zoneTable.Add(new ZoneTableEntry
                {
                    Name = "Zone " + (owner + 1).ToString(CultureInfo.InvariantCulture),
                    AreaDrawingUnits = aDu,
                    AreaM2 = aM2,
                    ZoneOwnerIndex = owner
                });
            }

            var createdZoneHandles = new List<string>();
            ShaftVoronoiZonesOnFloorPolyline.AppendZoneOutlinePolylines(
                doc,
                zoneRings,
                floorBoundary,
                zoneTable,
                zoneOutlinesOnFloorBoundaryLayer: false,
                createdZonePolylineHandles: createdZoneHandles);

            var ownersList = new List<int>(ringsWithOwner.Count);
            for (int i = 0; i < ringsWithOwner.Count; i++)
                ownersList.Add(ringsWithOwner[i].Owner);
            AssignShaftToZoneCommand.ApplyDefaultShaftAssignmentsForCreatedZones(
                db, createdZoneHandles, ownersList, shaftHandlesDeduped, floorBoundary);

            // Palette refreshes zone→shaft table via CommandEnded + Idle (see SprinklerPaletteExtensionApplication).

            // Remove legacy ManhattanFixed temporary output (lines + region polylines tagged with their own handle),
            // and clear any prior global-boundary separator lines so the output is the closed zone polylines only
            // (what the user expects to see/select — consistent with ZoneCreation1).
            CleanupLegacyGlobalLayerEntities(doc, ring);

            try
            {
                ZoneGlobalBoundaryBuilder.TryClearForFloorBoundary(doc, floorBoundaryEntityId, out _);
            }
            catch { /* ignore */ }

            message = "Fix zones complete. Orthogonal zone polylines=" +
                      createdZoneHandles.Count.ToString(CultureInfo.InvariantCulture) + "." +
                      (failedCount > 0
                          ? (" Skipped " + failedCount.ToString(CultureInfo.InvariantCulture) + " input ring(s).")
                          : string.Empty) +
                      overlapWarning;
            return true;
        }

        private static List<List<Point2d>> CollectInputZoneRings(
            Document doc,
            List<Point2d> floorRing,
            IReadOnlyList<ObjectId> explicitZonePolylineIds)
        {
            var rings = new List<List<Point2d>>();
            if (doc == null || floorRing == null || floorRing.Count < 3)
                return rings;

            var db = doc.Database;
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;
            double edgeTol = Math.Max(tol * 4.0, 1e-6);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (explicitZonePolylineIds != null && explicitZonePolylineIds.Count > 0)
                {
                    foreach (ObjectId oid in explicitZonePolylineIds)
                    {
                        if (oid.IsNull) continue;
                        Polyline pl;
                        try { pl = tr.GetObject(oid, OpenMode.ForRead, false) as Polyline; }
                        catch { continue; }
                        if (pl == null || pl.IsErased || !pl.Closed) continue;

                        var c = ApproxCentroidFromPolyline(pl);
                        if (!PolygonUtils.PointInPolygon(floorRing, c) && !PolygonUtils.IsPointOnRingEdge(floorRing, c, edgeTol))
                            continue;

                        var pts = new List<Point2d>(pl.NumberOfVertices);
                        for (int k = 0; k < pl.NumberOfVertices; k++)
                            pts.Add(pl.GetPoint2dAt(k));
                        if (pts.Count >= 3)
                            rings.Add(pts);
                    }
                }
                else
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        Polyline pl;
                        try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { continue; }
                        if (pl == null || pl.IsErased || !pl.Closed) continue;
                        if (!SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer)) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)) continue;
                        if (!string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase)) continue;

                        var c = ApproxCentroidFromPolyline(pl);
                        if (!PolygonUtils.PointInPolygon(floorRing, c) && !PolygonUtils.IsPointOnRingEdge(floorRing, c, 1e-6))
                            continue;

                        var pts = new List<Point2d>(pl.NumberOfVertices);
                        for (int k = 0; k < pl.NumberOfVertices; k++)
                            pts.Add(pl.GetPoint2dAt(k));
                        if (pts.Count >= 3)
                            rings.Add(pts);
                    }
                }

                tr.Commit();
            }

            return rings;
        }

        private static void EraseOldZoneOutlinesAndLabels(Document doc, List<Point2d> floorRing)
        {
            if (doc == null || floorRing == null || floorRing.Count < 3) return;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (ObjectId id in ms)
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (ent == null || ent.IsErased) continue;

                    // Remove prior zone outlines (closed polylines tagged with their own handle).
                    if (ent is Polyline pl && pl.Closed && SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer))
                    {
                        if (SprinklerXData.TryGetZoneBoundaryHandle(pl, out string h)
                            && string.Equals(h, pl.Handle.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            var c = ApproxCentroidFromPolyline(pl);

                            if (PolygonUtils.PointInPolygon(floorRing, c))
                            {
                                try { pl.UpgradeOpen(); pl.Erase(); } catch { /* ignore */ }
                            }
                        }
                    }

                    // Remove zone labels (MText) inside the floor boundary.
                    if (ent is MText mt && string.Equals(mt.Layer, SprinklerLayers.ZoneLabelLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        Point2d p;
                        try { p = new Point2d(mt.Location.X, mt.Location.Y); }
                        catch { continue; }
                        if (!PolygonUtils.PointInPolygon(floorRing, p)) continue;

                        string t = null;
                        try { t = mt.Contents ?? string.Empty; } catch { t = string.Empty; }
                        if (t.StartsWith("Zone", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Uncovered", StringComparison.OrdinalIgnoreCase))
                        {
                            try { mt.UpgradeOpen(); mt.Erase(); } catch { /* ignore */ }
                        }
                    }
                }

                tr.Commit();
            }
        }

        private static void CleanupLegacyGlobalLayerEntities(Document doc, List<Point2d> floorRing)
        {
            if (doc == null || floorRing == null || floorRing.Count < 3) return;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (ObjectId id in ms)
                {
                    Entity ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (ent == null || ent.IsErased) continue;
                    if (!SprinklerLayers.IsZoneGlobalBoundaryOrMcdZoneOutlineLayerName(ent.Layer))
                        continue;

                    if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h)) continue;
                    // Legacy ManhattanFixed: tag equals entity handle.
                    if (!string.Equals(h, ent.Handle.ToString(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    Point2d c;
                    if (ent is Line ln)
                    {
                        var sp = ln.StartPoint;
                        var ep = ln.EndPoint;
                        c = new Point2d((sp.X + ep.X) * 0.5, (sp.Y + ep.Y) * 0.5);
                    }
                    else if (ent is Polyline pl)
                    {
                        c = ApproxCentroidFromPolyline(pl);
                    }
                    else
                    {
                        continue;
                    }

                    if (!PolygonUtils.PointInPolygon(floorRing, c) && !PolygonUtils.IsPointOnRingEdge(floorRing, c, 1e-6))
                        continue;

                    try { ent.UpgradeOpen(); ent.Erase(); } catch { /* ignore */ }
                }

                tr.Commit();
            }
        }

        private static Point2d ApproxCentroidFromPolyline(Polyline pl)
        {
            try
            {
                var ext = pl.GeometricExtents;
                double x = 0.5 * (ext.MinPoint.X + ext.MaxPoint.X);
                double y = 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y);
                return new Point2d(x, y);
            }
            catch
            {
                return new Point2d(0, 0);
            }
        }

        private static int NearestShaftIndex(Point2d p, List<Point2d> shafts)
        {
            if (shafts == null || shafts.Count == 0) return -1;
            int best = 0;
            double bestD2 = double.MaxValue;
            for (int i = 0; i < shafts.Count; i++)
            {
                double dx = p.X - shafts[i].X;
                double dy = p.Y - shafts[i].Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return best;
        }
    }
}

