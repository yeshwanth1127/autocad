using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Commands
{
    /// <summary>
    /// Incremental redesign: keep tagged trunk, refresh caps/connector from live shaft, optionally regenerate sprinkler grid, then attach branches.
    /// Used by <see cref="RebuildFromTrunkCommand"/> (full regen) and <see cref="SprinklerDesignCommand"/> (preserve heads).
    /// </summary>
    public static class SprinklerZoneRedesignFromTrunk
    {
        private const string MainPipeDisconnectProceedMessage =
            "There is a gap or disconnect found in the main pipe network. Do you want to proceed placing branch pipes?";

        /// <summary>
        /// <paramref name="regenerateSprinklerGrid"/>: true = same as legacy rebuild (re-grid from trunk); false = keep existing sprinkler inserts, only redraw branches + connector/caps.
        /// </summary>
        public static bool TryRun(
            Document doc,
            Database db,
            Editor ed,
            Polyline zone,
            List<Point2d> zoneRing,
            string boundaryHandleHex,
            ObjectId trunkId,
            bool regenerateSprinklerGrid,
            Point3d? selectedShaftPoint,
            out string errorMessage)
        {
            errorMessage = null;
            if (doc == null || db == null || ed == null || zone == null || zoneRing == null || zoneRing.Count < 3 ||
                string.IsNullOrEmpty(boundaryHandleHex) || trunkId.IsNull)
            {
                errorMessage = "Invalid inputs.";
                return false;
            }

            var srCfg = RuntimeSettings.Load();

            // Before mutating the zone on redesign/rebuild, verify the current moved trunk can still be
            // served by the assigned shaft. If no valid in-zone shaft->trunk connector path exists,
            // stop early and surface a user-facing error.
            if (!ValidateShaftCanServeTrunk(
                    db,
                    ed,
                    zone,
                    zoneRing,
                    boundaryHandleHex,
                    trunkId,
                    selectedShaftPoint,
                    srCfg.SprinklerSpacingM,
                    out string shaftServeErr))
            {
                errorMessage = shaftServeErr;
                return false;
            }

            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships = null;
            if (!string.IsNullOrWhiteSpace(boundaryHandleHex))
                floorRoomOwnerships = SprinklerHeadReader2d.BuildFloorRoomOwnerships(db);

            bool trunkHorizontal = true;
            double trunkAxis = 0;
            if (regenerateSprinklerGrid)
            {
                // Re-gridding is trunk-axis-aligned today; require a straight H/V trunk for that mode.
                if (!SprinklerTrunkLocator.TryReadStraightAxisAlignedTrunkInfo(db, trunkId, out trunkHorizontal, out trunkAxis, out string infoErr))
                {
                    errorMessage = infoErr ?? "Could not read main trunk.";
                    return false;
                }
            }

            ObjectId pendentSprinklerBlockId = ObjectId.Null;
            if (regenerateSprinklerGrid)
            {
                using (var trBlock = db.TransactionManager.StartTransaction())
                {
                    if (!PendentSprinklerBlockInsert.TryGetBlockDefinitionId(trBlock, db, out pendentSprinklerBlockId, out string blockLookupErr))
                    {
                        errorMessage = blockLookupErr;
                        trBlock.Commit();
                        return false;
                    }

                    trBlock.Commit();
                }
            }

            bool preserveSprinklerHeads = !regenerateSprinklerGrid;
            // In real-world drawings the "connector" is not a separate system from the main; it is
            // effectively part of the main pipe that users may manually move. During redesign (when
            // we preserve head positions) we must not erase/rebuild connector geometry, otherwise
            // manual main-pipe adjustments get partially overwritten.
            bool preserveConnector = !regenerateSprinklerGrid;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int zoneTaggedErased = EraseEntitiesWithZoneBoundaryTag(
                    tr,
                    ms,
                    boundaryHandleHex,
                    preserveSprinklerHeads,
                    preserveConnector,
                    trunkId,
                    zone != null ? zone.ObjectId : ObjectId.Null);
                int capsErased = EraseTrunkCapsInZone(tr, ms, zoneRing);
                int erased = EraseEntitiesInZone(tr, ms, zoneRing, preserveSprinklerHeads, trunkId, boundaryHandleHex);
                int outsideSprinklersErased = EraseSprinklerMarkersOutsideZone(tr, db, ms, zoneRing, boundaryHandleHex, floorRoomOwnerships);
                int outsideBranchErased = EraseBranchPipingMostlyOutsideZone(tr, ms, zoneRing, db, boundaryHandleHex, floorRoomOwnerships);

                TryTrimTrunkIfTouchingBoundary(tr, db, trunkId, zoneRing);

                // Connector handling:
                // - Rebuild-from-trunk: erase + rebuild connector so it tracks the shaft.
                // - Redesign (preserve heads): keep connector as-is so manual main pipe moves aren't overwritten.
                if (!preserveConnector)
                {
                    EraseConnectorInZone(tr, ms, trunkId, zoneRing);
                    Point3d shaft3;
                    bool hasShaft = selectedShaftPoint.HasValue;
                    if (hasShaft)
                        shaft3 = selectedShaftPoint.Value;
                    else
                        hasShaft = TryFindShaftForZone(db, zone, boundaryHandleHex, out shaft3, out _);
                    if (hasShaft)
                    {
                        DbObjectSafeAccess.TryGetObject(tr, trunkId, OpenMode.ForRead, out Polyline trunkPl);
                        if (trunkPl != null)
                        {
                            var trunkStart = trunkPl.StartPoint;
                            var trunkEnd = trunkPl.EndPoint;
                            if (MainPipeRouting2d.TryBuildConnectorInsideZone(
                                    zoneRing,
                                    db,
                                    new Point2d(shaft3.X, shaft3.Y),
                                    new Point2d(trunkStart.X, trunkStart.Y),
                                    new Point2d(trunkEnd.X, trunkEnd.Y),
                                    sprinklerSpacingMeters: srCfg.SprinklerSpacingM,
                                    out var connectorPath,
                                    out _))
                            {
                                DrawConnector(tr, db, ms, connectorPath, zone.Elevation, zone.Normal, trunkPl, boundaryHandleHex);
                            }
                        }
                    }
                }

                TryDrawTrunkEndCaps(tr, db, ms, trunkId, zone.Elevation, zone.Normal, boundaryHandleHex);

                tr.Commit();
                if (zoneTaggedErased > 0 || capsErased > 0 || erased > 0 || outsideSprinklersErased > 0 || outsideBranchErased > 0)
                    ed.WriteMessage(
                        "\nCleared " + zoneTaggedErased + " zone-tagged entities (anywhere), " +
                        erased + " untagged entities in zone (branches" + (preserveSprinklerHeads ? " only" : "/sprinklers") + "), " +
                        outsideSprinklersErased + " sprinkler markers outside zone, " +
                        outsideBranchErased + " branch segments mostly outside zone, " +
                        capsErased + " trunk caps in zone.\n");
            }

            if (regenerateSprinklerGrid)
            {
                if (!SprinklerGridPlacement2d.TryPlaceForZoneRingAnchoredToTrunk(
                        zoneRing,
                        db,
                        spacingMeters: srCfg.SprinklerSpacingM,
                        coverageRadiusMeters: 1.5,
                        maxBoundarySprinklerGapMeters: srCfg.SprinklerToBoundaryDistanceM,
                        trunkHorizontal: trunkHorizontal,
                        trunkAxis: trunkAxis,
                        out var placement,
                        out string err))
                {
                    errorMessage = err;
                    return false;
                }

                placement.Sprinklers = SprinklerShaftFootprintExclusion.RemovePointsInsideShaftFootprints(db, zone, placement.Sprinklers);

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId sprinklerLayerId = SprinklerLayers.EnsureMcdSprinklersLayer(tr, db);

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    SprinklerXData.EnsureRegApp(tr, db);
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        Entity ent = null;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                        if (ent == null) continue;
                        if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                            continue;
                        if (SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) &&
                            string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                        {
                            ent.UpgradeOpen();
                            try { ent.Erase(); } catch { /* ignore */ }
                        }
                    }

                    PendentSprinklerBlockInsert.AppendBlocksAtPoints(
                        tr,
                        db,
                        ms,
                        zone,
                        placement.Sprinklers,
                        pendentSprinklerBlockId,
                        sprinklerLayerId,
                        boundaryHandleHex,
                        rotationRadians: 0.0);

                    tr.Commit();
                }

                ed.WriteMessage(
                    "\nRebuild from trunk: sprinklers applied: " + placement.Sprinklers.Count.ToString() + ". " +
                    (placement.Summary ?? string.Empty) + "\n");
            }
            else
            {
                int movedExisting = 0;
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    movedExisting = SnapExistingSprinklerHeadsInsideZone(tr, db, ms, zone, zoneRing, boundaryHandleHex, floorRoomOwnerships, srCfg.SprinklerSpacingM);
                    tr.Commit();
                }

                ed.WriteMessage("\nRedesign: keeping existing sprinkler head positions; updating main pipe connector/caps and branch piping.\n");
                if (movedExisting > 0)
                    ed.WriteMessage("\nBoundary snap adjusted " + movedExisting.ToString() + " existing sprinkler heads inward.\n");
            }

            if (!TryCombRouteBranchesForZone(db, zone, zoneRing, boundaryHandleHex, trunkId, floorRoomOwnerships, out string branchMsg))
            {
                errorMessage = branchMsg ?? "Branch routing failed.";
                return false;
            }

            ed.WriteMessage("\n" + branchMsg + "\n");
            try { ed.Regen(); } catch { /* ignore */ }
            return true;
        }

        /// <summary>
        /// Shows a Yes/No dialog when a connectivity problem is detected, letting the user
        /// choose to proceed anyway (returns true) or cancel (returns false; caller should leave out error text so no second alert).
        /// </summary>
        private static bool AskUserProceedDespiteDisconnection(Editor ed, string message, string caption = "Main pipe network")
        {
            var result = MessageBox.Show(
                message,
                caption,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);   // default = No (safer)

            if (result == DialogResult.Yes)
            {
                ed?.WriteMessage("\n[Warning] Proceeding despite main pipe connectivity warning as requested.\n");
                return true;
            }
            return false;
        }

        private static bool ValidateShaftCanServeTrunk(
            Database db,
            Editor ed,
            Polyline zone,
            List<Point2d> zoneRing,
            string boundaryHandleHex,
            ObjectId trunkId,
            Point3d? selectedShaftPoint,
            double sprinklerSpacingMeters,
            out string errorMessage)
        {
            errorMessage = null;
            if (db == null || zone == null || zoneRing == null || zoneRing.Count < 3 || trunkId.IsNull)
            {
                errorMessage = "Invalid inputs for shaft/main connectivity validation.";
                return false;
            }

            string shaftErr = null;
            Point3d shaft3;
            bool hasShaft = selectedShaftPoint.HasValue;
            if (hasShaft)
                shaft3 = selectedShaftPoint.Value;
            else
                hasShaft = TryFindShaftForZone(db, zone, boundaryHandleHex, out shaft3, out shaftErr);

            if (!hasShaft)
            {
                // No shaft at all — hard stop (can't route without knowing shaft location).
                errorMessage = shaftErr ?? "No shaft is assigned to this zone.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
                if (!(tol > 0)) tol = 1e-6;

                double shaftTol = tol * 60.0;
                try
                {
                    if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, Math.Max(0.12, sprinklerSpacingMeters * 0.20), out double du) && du > shaftTol)
                        shaftTol = du;
                }
                catch { /* ignore */ }

                // Gather the existing "main network" in-zone: trunk + connector + any other non-cap
                // main polylines on the main layer.
                var mains = new List<(ObjectId id, List<Point2d> pts)>();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (ent == null) continue;
                    if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                        continue;
                    if (!(ent is Polyline pl))
                        continue;
                    if (SprinklerXData.IsTaggedTrunkCap(pl))
                        continue;
                    if (!string.IsNullOrWhiteSpace(boundaryHandleHex) &&
                        SprinklerXData.TryGetZoneBoundaryHandle(pl, out string mainZh) &&
                        !string.IsNullOrWhiteSpace(mainZh) &&
                        !string.Equals(mainZh.Trim(), boundaryHandleHex.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!PolylineHasSampleInsideZone(pl, zoneRing))
                        continue;

                    var pts = PolylineToPoint2dList(pl);
                    if (pts.Count >= 2)
                        mains.Add((id, pts));
                }

                if (mains.Count == 0)
                {
                    errorMessage = "No in-zone main pipe geometry found for shaft connectivity check.";
                    return false;
                }

                int trunkIdx = -1;
                for (int i = 0; i < mains.Count; i++)
                {
                    if (mains[i].id == trunkId)
                    {
                        trunkIdx = i;
                        break;
                    }
                }
                if (trunkIdx < 0)
                {
                    errorMessage = "Moved main trunk could not be found in the in-zone main network.";
                    return false;
                }

                var shaft2 = new Point2d(shaft3.X, shaft3.Y);
                var visited = new bool[mains.Count];
                var queue = new Queue<int>();

                for (int i = 0; i < mains.Count; i++)
                {
                    if (DistancePointToPolyline(shaft2, mains[i].pts) <= shaftTol)
                    {
                        visited[i] = true;
                        queue.Enqueue(i);
                    }
                }

                if (queue.Count == 0)
                {
                    tr.Commit();
                    if (AskUserProceedDespiteDisconnection(ed, MainPipeDisconnectProceedMessage))
                        return true;
                    errorMessage = null;
                    return false;
                }

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    if (cur == trunkIdx)
                    {
                        tr.Commit();
                        return true;
                    }

                    for (int j = 0; j < mains.Count; j++)
                    {
                        if (visited[j] || j == cur) continue;
                        if (!PolylinesTouchOrIntersect(mains[cur].pts, mains[j].pts, 1e-9))
                            continue;
                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }
                tr.Commit();
            }

            if (AskUserProceedDespiteDisconnection(ed, MainPipeDisconnectProceedMessage))
                return true;
            errorMessage = null;
            return false;
        }

        private static List<Point2d> PolylineToPoint2dList(Polyline pl)
        {
            var pts = new List<Point2d>();
            if (pl == null) return pts;
            int n = 0;
            try { n = pl.NumberOfVertices; } catch { return pts; }
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint3dAt(i);
                pts.Add(new Point2d(p.X, p.Y));
            }
            return pts;
        }

        private static bool PolylinesTouchOrIntersect(List<Point2d> a, List<Point2d> b, double tol)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2)
                return false;

            // Endpoint-to-polyline proximity.
            if (DistancePointToPolyline(a[0], b) <= tol) return true;
            if (DistancePointToPolyline(a[a.Count - 1], b) <= tol) return true;
            if (DistancePointToPolyline(b[0], a) <= tol) return true;
            if (DistancePointToPolyline(b[b.Count - 1], a) <= tol) return true;

            // Interior segment-to-segment proximity (catches mid-segment crossings and T-junctions).
            double tol2 = tol * tol;
            for (int i = 0; i + 1 < a.Count; i++)
            {
                for (int j = 0; j + 1 < b.Count; j++)
                {
                    if (SegmentsProximity2(a[i], a[i + 1], b[j], b[j + 1]) <= tol2)
                        return true;
                }
            }

            return false;
        }

        private static double SegmentsProximity2(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
        {
            // Min squared distance between two line segments via parametric approach.
            double best = Math.Min(
                Math.Min(DistSquaredPointToSegment(a0, b0, b1), DistSquaredPointToSegment(a1, b0, b1)),
                Math.Min(DistSquaredPointToSegment(b0, a0, a1), DistSquaredPointToSegment(b1, a0, a1)));
            // Also sample midpoints in case the closest approach is interior-to-interior.
            var am = new Point2d((a0.X + a1.X) * 0.5, (a0.Y + a1.Y) * 0.5);
            var bm = new Point2d((b0.X + b1.X) * 0.5, (b0.Y + b1.Y) * 0.5);
            double dx = am.X - bm.X, dy = am.Y - bm.Y;
            double d2m = dx * dx + dy * dy;
            if (d2m < best) best = d2m;
            return best;
        }

        private static double DistancePointToPolyline(Point2d p, List<Point2d> poly)
        {
            if (poly == null || poly.Count < 2)
                return double.MaxValue;

            double best2 = double.MaxValue;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                double d2 = DistSquaredPointToSegment(p, poly[i], poly[i + 1]);
                if (d2 < best2) best2 = d2;
            }

            return Math.Sqrt(best2);
        }

        private static int EraseEntitiesWithZoneBoundaryTag(
            Transaction tr,
            BlockTableRecord ms,
            string boundaryHandleHex,
            bool preserveSprinklerMarkers,
            bool preserveConnector,
            ObjectId preserveTrunkId,
            ObjectId preserveZoneBoundaryId)
        {
            int erased = 0;
            if (tr == null || ms == null || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                if (!preserveTrunkId.IsNull && id == preserveTrunkId)
                    continue;
                if (!preserveZoneBoundaryId.IsNull && id == preserveZoneBoundaryId)
                    continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (IsZoneBoundaryEntity(ent))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SprinklerXData.IsTaggedTrunk(ent))
                    continue;
                if (preserveConnector && SprinklerXData.IsTaggedConnector(ent))
                    continue;
                if (preserveSprinklerMarkers && SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static bool IsZoneBoundaryEntity(Entity ent)
        {
            var pl = ent as Polyline;
            if (pl == null || !pl.Closed)
                return false;

            string layer = ent.Layer ?? string.Empty;
            return
                string.Equals(layer, SprinklerLayers.McdZoneBoundaryLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, SprinklerLayers.ZoneLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, SprinklerLayers.ZoneLayerAlternateName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, SprinklerLayers.ZoneGlobalBoundaryLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, SprinklerLayers.BoundaryLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static int SnapExistingSprinklerHeadsInsideZone(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            Polyline zone,
            List<Point2d> zoneRing,
            string boundaryHandleHex,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            double sprinklerSpacingMeters)
        {
            if (tr == null || db == null || ms == null || zone == null || zoneRing == null || zoneRing.Count < 3)
                return 0;

            var entities = new List<Entity>();
            var points = new List<Point2d>();

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;

                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string headZh) ||
                    string.IsNullOrWhiteSpace(headZh) ||
                    !string.Equals(headZh.Trim(), boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                Point2d p;
                if (ent is BlockReference br)
                {
                    p = new Point2d(br.Position.X, br.Position.Y);
                }
                else if (ent is Circle c)
                {
                    p = new Point2d(c.Center.X, c.Center.Y);
                }
                else
                    continue;

                bool inThisZone = SprinklerHeadReader2d.IsPointOwnedByZoneForRouting(
                    db, zoneRing, boundaryHandleHex, p, ent, floorRoomOwnerships);

                if (!inThisZone)
                    continue;

                entities.Add(ent);
                points.Add(p);
            }

            if (points.Count == 0)
                return 0;

            if (!SprinklerGridPlacement2d.TrySnapExistingSprinklersInsideBoundary(
                    zoneRing,
                    db,
                    points,
                    spacingMeters: sprinklerSpacingMeters,
                    out var snapped,
                    out int moved,
                    out _))
                return 0;

            if (moved <= 0 || snapped == null || snapped.Count != points.Count)
                return 0;

            int applied = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                var oldP = points[i];
                var newP = snapped[i];
                if (Math.Abs(oldP.X - newP.X) <= 1e-12 && Math.Abs(oldP.Y - newP.Y) <= 1e-12)
                    continue;

                entities[i].UpgradeOpen();
                if (entities[i] is BlockReference br)
                {
                    var pos = br.Position;
                    br.Position = new Point3d(newP.X, newP.Y, pos.Z);
                    applied++;
                }
                else if (entities[i] is Circle c)
                {
                    var ctr = c.Center;
                    c.Center = new Point3d(newP.X, newP.Y, ctr.Z);
                    applied++;
                }
            }

            return applied;
        }

        private static int EraseEntitiesInZone(
            Transaction tr,
            BlockTableRecord ms,
            List<Point2d> zoneRing,
            bool preserveSprinklerHeads,
            ObjectId preserveTrunkId,
            string zoneBoundaryHandleHex)
        {
            int erased = 0;
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3)
                return 0;

            bool scopeTags = !string.IsNullOrWhiteSpace(zoneBoundaryHandleHex);
            string zoneHex = scopeTags ? zoneBoundaryHandleHex.Trim() : null;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                if (!preserveTrunkId.IsNull && id == preserveTrunkId)
                    continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (scopeTags &&
                    SprinklerXData.TryGetZoneBoundaryHandle(ent, out string entZh) &&
                    !string.IsNullOrWhiteSpace(entZh) &&
                    !string.Equals(entZh.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                string layer = ent.Layer ?? string.Empty;
                bool isSprinkler = SprinklerLayers.IsSprinklerHeadEntity(tr, ent);
                bool isBranch =
                    string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.BranchMarkerLayer, StringComparison.OrdinalIgnoreCase) ||
                    (SprinklerLayers.IsUnifiedZoneDesignLayerName(layer) &&
                     (ent is Line || (ent is Polyline plb && !plb.Closed)) &&
                     !SprinklerXData.IsTaggedTrunk(ent));
                if (preserveSprinklerHeads && isSprinkler)
                    continue;
                if (!isSprinkler && !isBranch)
                    continue;

                bool inside = false;
                if (ent is Circle c)
                {
                    inside = PointInPolygon(zoneRing, new Point2d(c.Center.X, c.Center.Y));
                }
                else if (ent is BlockReference br)
                {
                    var pos = br.Position;
                    inside = PointInPolygon(zoneRing, new Point2d(pos.X, pos.Y));
                }
                else if (ent is Polyline pl)
                {
                    inside = PolylineHasSampleInsideZone(pl, zoneRing);
                }
                else if (ent is Line ln)
                {
                    var a = ln.StartPoint;
                    var b = ln.EndPoint;
                    inside =
                        PointInPolygon(zoneRing, new Point2d(a.X, a.Y)) ||
                        PointInPolygon(zoneRing, new Point2d(b.X, b.Y));
                }

                if (!inside)
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseSprinklerMarkersOutsideZone(Transaction tr, Database db, BlockTableRecord ms, List<Point2d> zoneRing, string boundaryHandleHex, List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships)
        {
            int erased = 0;
            if (tr == null || db == null || ms == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;

                Point2d p2;
                if (ent is Circle c)
                {
                    p2 = new Point2d(c.Center.X, c.Center.Y);
                }
                else if (ent is BlockReference br)
                {
                    if (!string.Equals(
                            GetBlockName(br, tr),
                            SprinklerLayers.GetConfiguredSprinklerBlockName(),
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var pos = br.Position;
                    p2 = new Point2d(pos.X, pos.Y);
                }
                else
                    continue;

                if (PointInPolygon(zoneRing, p2))
                    continue;

                // Match TryReadSprinklerHeadPointsForZoneRouting: keep heads in floor rooms parented to this zone
                // even when outside the zone polygon (straddling rooms).
                if (SprinklerHeadReader2d.IsPointInsideFloorRoomParentedToZone(db, zoneRing, boundaryHandleHex, p2, floorRoomOwnerships))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseBranchPipingMostlyOutsideZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing, Database db, string boundaryHandleHex, List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships)
        {
            int erased = 0;
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3 || db == null || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            double sampleStepDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.12, out double s) && s > 0)
                    sampleStepDu = s;
            }
            catch { /* ignore */ }

            const double minInsideFractionToKeep = 0.55;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out string h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                string layer = ent.Layer ?? string.Empty;
                bool onBranchLayer =
                    string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, SprinklerLayers.BranchMarkerLayer, StringComparison.OrdinalIgnoreCase);
                if (!onBranchLayer)
                    continue;

                bool wipe = false;
                if (ent is Polyline pl)
                    wipe = BranchPolylineMostlyOutsideZone(pl, zoneRing, db, boundaryHandleHex, sampleStepDu, minInsideFractionToKeep, floorRoomOwnerships);
                else if (ent is Line ln)
                    wipe = BranchLineMostlyOutsideZone(ln, zoneRing, db, boundaryHandleHex, minInsideFractionToKeep, floorRoomOwnerships);
                else
                    continue;

                if (!wipe)
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static bool BranchPolylineMostlyOutsideZone(Polyline pl, List<Point2d> zoneRing, Database db, string boundaryHandleHex, double sampleStepDu, double minInsideFractionToKeep, List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships)
        {
            if (pl == null || zoneRing == null || zoneRing.Count < 3)
                return false;

            try
            {
                double len = pl.Length;
                if (!(len > 1e-12))
                {
                    if (pl.NumberOfVertices < 1)
                        return false;
                    var v = pl.GetPoint3dAt(0);
                    var p = new Point2d(v.X, v.Y);
                    return !SprinklerHeadReader2d.IsPointInZoneOrParentedFloorRoomForRouting(db, zoneRing, boundaryHandleHex, p, floorRoomOwnerships);
                }

                int n = Math.Max(8, (int)Math.Ceiling(len / Math.Max(sampleStepDu, 1e-9)));
                int inside = 0;
                for (int i = 0; i <= n; i++)
                {
                    double d = len * i / n;
                    var p3 = pl.GetPointAtDist(d);
                    if (SprinklerHeadReader2d.IsPointInZoneOrParentedFloorRoomForRouting(db, zoneRing, boundaryHandleHex, new Point2d(p3.X, p3.Y), floorRoomOwnerships))
                        inside++;
                }

                double frac = inside / (double)(n + 1);
                return frac < minInsideFractionToKeep;
            }
            catch
            {
                return false;
            }
        }

        private static bool BranchLineMostlyOutsideZone(Line ln, List<Point2d> zoneRing, Database db, string boundaryHandleHex, double minInsideFractionToKeep, List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships)
        {
            if (ln == null || zoneRing == null || zoneRing.Count < 3)
                return false;

            try
            {
                var a = ln.StartPoint;
                var b = ln.EndPoint;
                int inside = 0;
                const int steps = 12;
                for (int i = 0; i <= steps; i++)
                {
                    double t = i / (double)steps;
                    double x = a.X + t * (b.X - a.X);
                    double y = a.Y + t * (b.Y - a.Y);
                    if (SprinklerHeadReader2d.IsPointInZoneOrParentedFloorRoomForRouting(db, zoneRing, boundaryHandleHex, new Point2d(x, y), floorRoomOwnerships))
                        inside++;
                }

                return inside / (double)(steps + 1) < minInsideFractionToKeep;
            }
            catch
            {
                return false;
            }
        }

        private static int EraseTrunkCapsInZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing)
        {
            int erased = 0;
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3)
                return 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                    continue;
                if (!(ent is Polyline pl))
                    continue;
                if (!SprinklerXData.IsTaggedTrunkCap(pl))
                    continue;
                if (!PolylineHasSampleInsideZone(pl, zoneRing))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseConnectorInZone(Transaction tr, BlockTableRecord ms, ObjectId trunkId, List<Point2d> zoneRing)
        {
            int erased = 0;
            if (tr == null || ms == null || trunkId.IsNull || zoneRing == null || zoneRing.Count < 3)
                return 0;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                if (id == trunkId) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;
                if (!SprinklerLayers.IsMainPipeLayerName(ent.Layer))
                    continue;
                if (!(ent is Polyline pl))
                    continue;
                if (SprinklerXData.IsTaggedTrunk(pl) || SprinklerXData.IsTaggedTrunkCap(pl))
                    continue;
                if (!PolylineHasSampleInsideZone(pl, zoneRing))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static void DrawConnector(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            List<Point2d> connectorPath,
            double elevation,
            Vector3d normal,
            Polyline trunkPl,
            string zoneBoundaryHandleHex)
        {
            if (tr == null || db == null || ms == null || connectorPath == null || connectorPath.Count < 2)
                return;

            double w = 0;
            try { w = trunkPl?.ConstantWidth ?? 0; } catch { w = 0; }
            if (!(w > 1e-12))
                w = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            if (!(w > 0)) w = 1.0;

            SprinklerXData.EnsureRegApp(tr, db);
            ObjectId mainPipeLayerId = SprinklerLayers.EnsureMainPipeLayer(tr, db);

            var conn = new Polyline();
            conn.SetDatabaseDefaults(db);
            conn.LayerId = mainPipeLayerId;
            conn.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            conn.ConstantWidth = w;
            conn.Elevation = elevation;
            conn.Normal = normal;
            for (int i = 0; i < connectorPath.Count; i++)
                conn.AddVertexAt(i, connectorPath[i], 0, 0, 0);
            conn.Closed = false;
            SprinklerXData.TagAsConnector(conn);
            if (!string.IsNullOrEmpty(zoneBoundaryHandleHex))
                SprinklerXData.ApplyZoneBoundaryTag(conn, zoneBoundaryHandleHex);
            ms.AppendEntity(conn);
            tr.AddNewlyCreatedDBObject(conn, true);
        }

        private static bool TryFindShaftForZone(Database db, Polyline zone, string boundaryHandleHex, out Point3d shaft, out string errorMessage)
        {
            shaft = default;
            errorMessage = null;
            if (db == null || zone == null)
            {
                errorMessage = "Invalid inputs.";
                return false;
            }

            // Prefer explicit zone->shaft assignment over spatial heuristics. This keeps redesign stable
            // even when the trunk has been moved and is no longer near the shaft.
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    Entity zoneEnt = null;
                    if (!string.IsNullOrEmpty(boundaryHandleHex))
                    {
                        try
                        {
                            var zh = new Handle(Convert.ToInt64(boundaryHandleHex, 16));
                            ObjectId zid = ObjectId.Null;
                            try { zid = db.GetObjectId(false, zh, 0); } catch { zid = ObjectId.Null; }
                            if (!zid.IsNull)
                            {
                                try { zoneEnt = tr.GetObject(zid, OpenMode.ForRead, false) as Entity; }
                                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { zoneEnt = null; }
                            }
                        }
                        catch { /* ignore */ }
                    }
                    if (zoneEnt == null && !zone.ObjectId.IsNull)
                    {
                        try { zoneEnt = tr.GetObject(zone.ObjectId, OpenMode.ForRead, false) as Entity; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { zoneEnt = null; }
                    }

                    if (zoneEnt != null &&
                        SprinklerXData.TryGetShaftAssignmentHandle(zoneEnt, out string shaftHandleHex) &&
                        !string.IsNullOrWhiteSpace(shaftHandleHex))
                    {
                        Handle sh;
                        try { sh = new Handle(Convert.ToInt64(shaftHandleHex, 16)); }
                        catch { sh = new Handle(0); }

                        if (sh.Value != 0)
                        {
                            ObjectId shaftId = ObjectId.Null;
                            try { shaftId = db.GetObjectId(false, sh, 0); } catch { shaftId = ObjectId.Null; }
                            if (!shaftId.IsNull)
                            {
                                BlockReference shaftBr = null;
                                try { shaftBr = tr.GetObject(shaftId, OpenMode.ForRead, false) as BlockReference; }
                                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { shaftBr = null; }
                                if (shaftBr != null && !shaftBr.IsErased)
                                {
                                    shaft = shaftBr.Position;
                                    tr.Commit();
                                    return true;
                                }
                            }
                        }
                    }

                    tr.Commit();
                }
            }
            catch { /* fallback to spatial scan */ }

            try
            {
                var inside = FindShaftsInsideBoundary.GetShaftPositionsInsideBoundary(db, zone);
                if (inside != null && inside.Count > 0)
                {
                    shaft = inside[0];
                    return true;
                }
            }
            catch { /* ignore */ }

            Point3d c3;
            try
            {
                var ext = zone.GeometricExtents;
                c3 = new Point3d(
                    0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                    0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y),
                    ext.MinPoint.Z);
            }
            catch
            {
                c3 = new Point3d(zone.GetPoint2dAt(0).X, zone.GetPoint2dAt(0).Y, zone.Elevation);
            }

            bool found = false;
            double best = double.MaxValue;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (!(ent is BlockReference br)) continue;
                    string blockName = GetBlockName(br, tr);
                    bool isShaftByName = !string.IsNullOrWhiteSpace(blockName) &&
                        blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isShaftByLayer = !string.IsNullOrWhiteSpace(br.Layer) &&
                        br.Layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isShaftByName && !isShaftByLayer)
                        continue;
                    double d = br.Position.DistanceTo(c3);
                    if (d < best)
                    {
                        best = d;
                        shaft = br.Position;
                        found = true;
                    }
                }
                tr.Commit();
            }

            if (!found)
            {
                errorMessage = "No shaft blocks found (block name or layer must contain \"shaft\").";
                return false;
            }
            return true;
        }

        private static string GetBlockName(BlockReference br, Transaction tr)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!br.IsDynamicBlock)
                return btr.Name;
            if (!br.DynamicBlockTableRecord.IsNull)
            {
                var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                return dyn.Name;
            }
            return btr.Name;
        }

        private static void TryDrawTrunkEndCaps(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            ObjectId trunkId,
            double elevation,
            Vector3d normal,
            string zoneBoundaryHandleHex)
        {
            if (tr == null || db == null || ms == null || trunkId.IsNull)
                return;

            if (!DbObjectSafeAccess.TryGetObject(tr, trunkId, OpenMode.ForRead, out Polyline trunk))
                return;
            if (trunk == null)
                return;

            int nv = trunk.NumberOfVertices;
            if (nv < 2)
                return;

            var trunkPath = new List<Point2d>(nv);
            for (int i = 0; i < nv; i++)
            {
                var p = trunk.GetPoint3dAt(i);
                trunkPath.Add(new Point2d(p.X, p.Y));
            }

            double w = 0;
            try { w = trunk.ConstantWidth; } catch { w = 0; }
            if (!(w > 1e-12))
                w = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
            if (!(w > 0)) w = 1.0;

            double capLen = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.30, out double du) && du > 0)
                    capLen = du;
            }
            catch { /* ignore */ }
            double h = 0.5 * capLen;

            SprinklerXData.EnsureRegApp(tr, db);
            ObjectId mainPipeLayerId = SprinklerLayers.EnsureMainPipeLayer(tr, db);

            int last = trunkPath.Count - 1;
            var a = trunkPath[0];
            var b = trunkPath[last];
            var aNext = trunkPath[1];
            var bPrev = trunkPath[last - 1];

            DrawCapAt(a, new Vector2d(aNext.X - a.X, aNext.Y - a.Y));
            DrawCapAt(b, new Vector2d(b.X - bPrev.X, b.Y - bPrev.Y));

            void DrawCapAt(Point2d p, Vector2d along)
            {
                double aLen = Math.Sqrt(along.X * along.X + along.Y * along.Y);
                double nx, ny;
                if (aLen < 1e-9)
                {
                    nx = 0;
                    ny = 1;
                }
                else
                {
                    nx = -along.Y / aLen;
                    ny =  along.X / aLen;
                }

                var cap = new Polyline();
                cap.SetDatabaseDefaults(db);
                cap.LayerId = mainPipeLayerId;
                cap.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                cap.ConstantWidth = w;
                cap.Elevation = elevation;
                cap.Normal = normal;
                cap.AddVertexAt(0, new Point2d(p.X - nx * h, p.Y - ny * h), 0, 0, 0);
                cap.AddVertexAt(1, new Point2d(p.X + nx * h, p.Y + ny * h), 0, 0, 0);
                cap.Closed = false;
                SprinklerXData.TagAsTrunkCap(cap);
                if (!string.IsNullOrEmpty(zoneBoundaryHandleHex))
                    SprinklerXData.ApplyZoneBoundaryTag(cap, zoneBoundaryHandleHex);
                ms.AppendEntity(cap);
                tr.AddNewlyCreatedDBObject(cap, true);
            }
        }

        private static void TryTrimTrunkIfTouchingBoundary(Transaction tr, Database db, ObjectId trunkId, List<Point2d> zoneRing)
        {
            if (tr == null || db == null || trunkId.IsNull || zoneRing == null || zoneRing.Count < 3)
                return;

            if (!DbObjectSafeAccess.TryGetObject(tr, trunkId, OpenMode.ForWrite, out Polyline trunk))
                return;
            if (trunk == null || trunk.NumberOfVertices < 2)
                return;

            double trimDu = 0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.30, out double du) && du > 0)
                    trimDu = du;
            }
            catch { /* ignore */ }
            if (!(trimDu > 0))
                return;

            double extent = 1.0;
            try
            {
                var ext = trunk.GeometricExtents;
                extent = Math.Max(Math.Abs(ext.MaxPoint.X - ext.MinPoint.X), Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y));
            }
            catch { /* ignore */ }
            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double eps = Math.Max(tol, 1e-9 * Math.Max(extent, 1.0));

            var a3 = trunk.StartPoint;
            var b3 = trunk.EndPoint;
            var a = new Point2d(a3.X, a3.Y);
            var b = new Point2d(b3.X, b3.Y);

            double da = Math.Sqrt(MinDistSquaredToRingEdges(zoneRing, a));
            double dbd = Math.Sqrt(MinDistSquaredToRingEdges(zoneRing, b));
            bool touchA = da <= trimDu * 0.75 + eps;
            bool touchB = dbd <= trimDu * 0.75 + eps;
            if (!touchA && !touchB)
                return;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            int nv = trunk.NumberOfVertices;
            for (int i = 0; i < nv; i++)
            {
                var p = trunk.GetPoint3dAt(i);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            bool trunkHorizontal = (maxY - minY) <= tol * 10.0 && (maxX - minX) > tol * 10.0;

            double len;
            try { len = trunk.Length; }
            catch { len = trunkHorizontal ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y); }
            if (len <= trimDu * 2.2)
                return;

            if (trunkHorizontal)
            {
                double dir = b.X >= a.X ? 1.0 : -1.0;
                if (touchA) trunk.SetPointAt(0, new Point2d(a.X + dir * trimDu, a.Y));
                if (touchB) trunk.SetPointAt(nv - 1, new Point2d(b.X - dir * trimDu, b.Y));
            }
            else
            {
                double dir = b.Y >= a.Y ? 1.0 : -1.0;
                if (touchA) trunk.SetPointAt(0, new Point2d(a.X, a.Y + dir * trimDu));
                if (touchB) trunk.SetPointAt(nv - 1, new Point2d(b.X, b.Y - dir * trimDu));
            }
        }

        private static double MinDistSquaredToRingEdges(IList<Point2d> ring, Point2d p)
        {
            double best = double.PositiveInfinity;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                double d2 = DistSquaredPointToSegment(p, a, b);
                if (d2 < best) best = d2;
            }
            return best;
        }

        private static double DistSquaredPointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);

            double c2 = vx * vx + vy * vy;
            if (c2 <= 0) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);

            double t = c1 / c2;
            if (t >= 1) return (p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y);

            double px = a.X + t * vx;
            double py = a.Y + t * vy;
            double dx = p.X - px;
            double dy = p.Y - py;
            return dx * dx + dy * dy;
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

        private const double MainLineIntersectEps = 1e-9;
        private const double BranchAxisSnapEps = 1e-4;
        /// <summary>Legacy tolerance for auxiliary column ordering (not merged geometry).</summary>
        private const double AuxColumnGroupingEps = 1e-2;
        /// <summary>Fraction of trunk length from start/end; heads whose closest foot lies in these bands route only via end auxiliary (not the diagonal).</summary>
        private const double TrunkAuxEndZoneArcFrac = 0.15;

        /// <summary>Wider-than-tall main bbox → trunk runs broadly along +X → use vertical branch drops (+Y / −Y). Otherwise use horizontal feeders.</summary>
        private static bool MainRunsPrimarilyAlongX(List<Point2d> pts)
        {
            if (pts == null || pts.Count < 2) return true;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return (maxX - minX) >= (maxY - minY);
        }

        private static List<Point2d> DedupeNearbyIntersections(List<Point2d> input, double tol)
        {
            var res = new List<Point2d>();
            foreach (var p in input)
            {
                bool dup = false;
                for (int i = 0; i < res.Count; i++)
                {
                    if (res[i].GetDistanceTo(p) <= tol) { dup = true; break; }
                }
                if (!dup) res.Add(p);
            }
            return res;
        }

        private sealed class BranchRouteContext
        {
            public Database Db;
            public List<Point2d> ZoneRing;
            public string BoundaryHex;
            public List<SprinklerHeadReader2d.FloorRoomOwnership> FloorRooms;
        }

        private sealed class SprinklerColumnGroup
        {
            public List<int> Indices = new List<int>();
            public double ColumnCoord;
        }

        private sealed class BranchSegmentRef
        {
            public Point2d A;
            public Point2d B;
        }

        private static double ColumnClusterToleranceM(double sprinklerSpacingM)
        {
            double spacing = sprinklerSpacingM > 1e-9 ? sprinklerSpacingM : 1.0;
            return Math.Max(AuxColumnGroupingEps, 0.35 * spacing);
        }

        private static List<SprinklerColumnGroup> BuildSprinklerColumnGroups(
            List<Point2d> sprinklers,
            bool clusterByX,
            double clusterTol)
        {
            var groups = new List<SprinklerColumnGroup>();
            if (sprinklers == null || sprinklers.Count == 0)
                return groups;

            var order = new List<int>(sprinklers.Count);
            for (int i = 0; i < sprinklers.Count; i++)
                order.Add(i);
            order.Sort((a, b) =>
            {
                double ka = clusterByX ? sprinklers[a].X : sprinklers[a].Y;
                double kb = clusterByX ? sprinklers[b].X : sprinklers[b].Y;
                return ka.CompareTo(kb);
            });

            SprinklerColumnGroup cur = null;
            for (int i = 0; i < order.Count; i++)
            {
                int idx = order[i];
                double coord = clusterByX ? sprinklers[idx].X : sprinklers[idx].Y;
                if (cur == null)
                {
                    cur = new SprinklerColumnGroup { ColumnCoord = coord };
                    cur.Indices.Add(idx);
                    groups.Add(cur);
                    continue;
                }
                if (coord - cur.ColumnCoord > clusterTol)
                {
                    cur = new SprinklerColumnGroup { ColumnCoord = coord };
                    cur.Indices.Add(idx);
                    groups.Add(cur);
                }
                else
                    cur.Indices.Add(idx);
            }
            return groups;
        }

        private static void CollectVerticalCutsWithMain(List<Point2d> mainPts, double xLine, List<Point2d> hitsOut)
        {
            for (int i = 0; i + 1 < mainPts.Count; i++)
            {
                Point2d a = mainPts[i];
                Point2d b = mainPts[i + 1];
                double dx = b.X - a.X;
                if (Math.Abs(dx) < MainLineIntersectEps)
                {
                    double xSeg = 0.5 * (a.X + b.X);
                    if (Math.Abs(xLine - xSeg) <= BranchAxisSnapEps)
                    {
                        Point2d close = ClosestPointOnSegment(a, b, new Point2d(xLine, 0.5 * (a.Y + b.Y)));
                        hitsOut.Add(new Point2d(xLine, close.Y));
                    }
                    continue;
                }
                double t = (xLine - a.X) / dx;
                if (t < -MainLineIntersectEps || t > 1.0 + MainLineIntersectEps) continue;
                t = Math.Max(0, Math.Min(1, t));
                hitsOut.Add(new Point2d(xLine, a.Y + t * (b.Y - a.Y)));
            }
        }

        private static void CollectHorizontalCutsWithMain(List<Point2d> mainPts, double yLine, List<Point2d> hitsOut)
        {
            for (int i = 0; i + 1 < mainPts.Count; i++)
            {
                Point2d a = mainPts[i];
                Point2d b = mainPts[i + 1];
                double dy = b.Y - a.Y;
                if (Math.Abs(dy) < MainLineIntersectEps)
                {
                    double ySeg = 0.5 * (a.Y + b.Y);
                    if (Math.Abs(yLine - ySeg) <= BranchAxisSnapEps)
                    {
                        Point2d close = ClosestPointOnSegment(a, b, new Point2d(0.5 * (a.X + b.X), yLine));
                        hitsOut.Add(new Point2d(close.X, yLine));
                    }
                    continue;
                }
                double t = (yLine - a.Y) / dy;
                if (t < -MainLineIntersectEps || t > 1.0 + MainLineIntersectEps) continue;
                t = Math.Max(0, Math.Min(1, t));
                hitsOut.Add(new Point2d(a.X + t * (b.X - a.X), yLine));
            }
        }

        private static bool TryPickPerpendicularTapOnMain(List<Point2d> mainPts, Point2d sprinkler, bool useVerticalBranches, out Point2d tapOnMain)
        {
            tapOnMain = default;
            var hits = new List<Point2d>();
            if (useVerticalBranches)
                CollectVerticalCutsWithMain(mainPts, sprinkler.X, hits);
            else
                CollectHorizontalCutsWithMain(mainPts, sprinkler.Y, hits);
            hits = DedupeNearbyIntersections(hits, BranchAxisSnapEps);
            if (hits.Count == 0) return false;

            tapOnMain = hits[0];
            double bd = sprinkler.GetDistanceTo(tapOnMain);
            for (int i = 1; i < hits.Count; i++)
            {
                double d = sprinkler.GetDistanceTo(hits[i]);
                if (d < bd)
                {
                    bd = d;
                    tapOnMain = hits[i];
                }
            }
            return true;
        }

        private static bool TryPickColumnTapOnMain(
            List<Point2d> mainPts,
            double columnCoord,
            bool useVerticalBranches,
            List<Point2d> sprinklers,
            IReadOnlyList<int> headIndices,
            out Point2d tapOnMain)
        {
            tapOnMain = default;
            if (headIndices == null || headIndices.Count == 0)
                return false;

            double targetAlong = useVerticalBranches
                ? MedianCoordInBucket(sprinklers, new List<int>(headIndices), useX: false)
                : MedianCoordInBucket(sprinklers, new List<int>(headIndices), useX: true);

            var hits = CollectMainCutsAtCoord(mainPts, columnCoord, useVerticalBranches);
            if (hits.Count == 0)
            {
                for (int k = 0; k < headIndices.Count; k++)
                {
                    double headCoord = useVerticalBranches
                        ? sprinklers[headIndices[k]].X
                        : sprinklers[headIndices[k]].Y;
                    hits.AddRange(CollectMainCutsAtCoord(mainPts, headCoord, useVerticalBranches));
                }
                hits = DedupeNearbyIntersections(hits, BranchAxisSnapEps);
            }

            if (hits.Count == 0)
                return false;

            tapOnMain = hits[0];
            double best = useVerticalBranches
                ? Math.Abs(tapOnMain.Y - targetAlong)
                : Math.Abs(tapOnMain.X - targetAlong);
            for (int i = 1; i < hits.Count; i++)
            {
                double d = useVerticalBranches
                    ? Math.Abs(hits[i].Y - targetAlong)
                    : Math.Abs(hits[i].X - targetAlong);
                if (d < best)
                {
                    best = d;
                    tapOnMain = hits[i];
                }
            }
            return true;
        }

        private static List<Point2d> CollectMainCutsAtCoord(List<Point2d> mainPts, double coord, bool useVerticalBranches)
        {
            var hits = new List<Point2d>();
            if (useVerticalBranches)
                CollectVerticalCutsWithMain(mainPts, coord, hits);
            else
                CollectHorizontalCutsWithMain(mainPts, coord, hits);
            return DedupeNearbyIntersections(hits, BranchAxisSnapEps);
        }

        private static int ClassifyColumnAuxPile(
            List<Point2d> mainPipePts,
            List<Point2d> sprinklers,
            SprinklerColumnGroup group,
            bool useVerticalBranches)
        {
            if (group?.Indices == null || group.Indices.Count == 0)
                return -1;
            int rep = group.Indices[group.Indices.Count / 2];
            int pile = ClassifyAuxPile(mainPipePts, sprinklers[rep], useVerticalBranches, out _);
            if (pile != 0 && pile != 1)
                return -1;

            Point2d endVertex = pile == 0
                ? mainPipePts[0]
                : mainPipePts[mainPipePts.Count - 1];
            double outwardSign = TrunkEndOutwardSign(mainPipePts, endVertex, useVerticalBranches);
            double columnCoord = useVerticalBranches
                ? MedianCoordInBucket(sprinklers, group.Indices, useX: true)
                : MedianCoordInBucket(sprinklers, group.Indices, useX: false);
            double anchor = useVerticalBranches ? endVertex.X : endVertex.Y;
            if (!IsOutwardAlongAxis(columnCoord, anchor, outwardSign))
                return -1;
            return pile;
        }

        private static bool BranchPathFullyInZone(List<Point2d> zoneRing, List<Point2d> verts)
        {
            if (zoneRing == null || verts == null || verts.Count < 2) return false;
            foreach (var v in verts)
                if (!PointInPolygon(zoneRing, v))
                    return false;
            for (int i = 1; i < verts.Count; i++)
                if (!SegmentInsideRing(zoneRing, verts[i - 1], verts[i]))
                    return false;
            return true;
        }

        private static bool BranchPathValidForRouting(BranchRouteContext ctx, List<Point2d> verts)
        {
            if (ctx?.ZoneRing == null || verts == null || verts.Count < 2)
                return false;
            string hex = ctx.BoundaryHex?.Trim() ?? "";
            foreach (var v in verts)
            {
                if (!SprinklerHeadReader2d.IsPointInZoneOrParentedFloorRoomForRouting(
                    ctx.Db, ctx.ZoneRing, hex, v, ctx.FloorRooms))
                    return false;
            }
            for (int i = 1; i < verts.Count; i++)
            {
                if (!BranchSegmentValidForRouting(ctx, verts[i - 1], verts[i]))
                    return false;
            }
            return true;
        }

        private static bool BranchSegmentValidForRouting(BranchRouteContext ctx, Point2d p0, Point2d p1)
        {
            const int samples = 14;
            string hex = ctx.BoundaryHex?.Trim() ?? "";
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                if (!SprinklerHeadReader2d.IsPointInZoneOrParentedFloorRoomForRouting(
                    ctx.Db, ctx.ZoneRing, hex, p, ctx.FloorRooms))
                    return false;
            }
            return true;
        }

        /// <summary>Active pile indices sorted by coordinate; split when gap &gt; tol along that axis.</summary>
        private static List<List<int>> ClusterPileIndicesByAxis(
            List<Point2d> sprinklers,
            List<int> pileIndices,
            HashSet<int> servedIndices,
            bool sortByX)
        {
            var active = new List<int>();
            for (int i = 0; i < pileIndices.Count; i++)
            {
                int si = pileIndices[i];
                if (!servedIndices.Contains(si))
                    active.Add(si);
            }
            if (active.Count == 0)
                return new List<List<int>>();

            active.Sort((a, b) =>
            {
                double ka = sortByX ? sprinklers[a].X : sprinklers[a].Y;
                double kb = sortByX ? sprinklers[b].X : sprinklers[b].Y;
                return ka.CompareTo(kb);
            });

            var buckets = new List<List<int>>();
            for (int i = 0; i < active.Count; i++)
            {
                int idx = active[i];
                double coord = sortByX ? sprinklers[idx].X : sprinklers[idx].Y;
                if (buckets.Count == 0)
                {
                    buckets.Add(new List<int> { idx });
                    continue;
                }
                var cur = buckets[buckets.Count - 1];
                int lastIdx = cur[cur.Count - 1];
                double lastCoord = sortByX ? sprinklers[lastIdx].X : sprinklers[lastIdx].Y;
                if (coord - lastCoord > AuxColumnGroupingEps)
                    buckets.Add(new List<int> { idx });
                else
                    cur.Add(idx);
            }
            return buckets;
        }

        private static double MedianCoordInBucket(List<Point2d> sprinklers, List<int> bucket, bool useX)
        {
            if (bucket == null || bucket.Count == 0) return 0;
            var vals = new List<double>(bucket.Count);
            for (int i = 0; i < bucket.Count; i++)
                vals.Add(useX ? sprinklers[bucket[i]].X : sprinklers[bucket[i]].Y);
            vals.Sort();
            int n = vals.Count;
            if (n % 2 == 1)
                return vals[n / 2];
            return 0.5 * (vals[n / 2 - 1] + vals[n / 2]);
        }

        /// <summary>+1 / −1 along the auxiliary run axis pointing from the trunk end vertex away from the pipe interior.</summary>
        private static double TrunkEndOutwardSign(List<Point2d> mainPipePts, Point2d endVertex, bool axisIsX)
        {
            if (mainPipePts == null || mainPipePts.Count < 2)
                return 1;

            bool atStart = endVertex.GetDistanceTo(mainPipePts[0])
                <= endVertex.GetDistanceTo(mainPipePts[mainPipePts.Count - 1]);
            Point2d towardCenter = atStart ? mainPipePts[1] : mainPipePts[mainPipePts.Count - 2];

            double sign = axisIsX
                ? Math.Sign(endVertex.X - towardCenter.X)
                : Math.Sign(endVertex.Y - towardCenter.Y);
            if (Math.Abs(sign) < 1e-9)
                sign = atStart ? -1 : 1;
            return sign;
        }

        private static bool IsOutwardAlongAxis(double coord, double anchor, double outwardSign)
        {
            if (outwardSign > 0)
                return coord >= anchor - BranchAxisSnapEps;
            return coord <= anchor + BranchAxisSnapEps;
        }

        private static bool TryDrawTaggedBranchSegment(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            List<Point2d> path,
            ref int drawnCount)
        {
            path = CollapseCollinear(path ?? new List<Point2d>());
            if (path.Count < 2 || !BranchPathValidForRouting(ctx, path))
                return false;
            var pl = CreateBranchPolyline(ctx.Db, path, elevation, branchLayerId, branchW);
            if (pl == null) return false;
            SprinklerXData.ApplyZoneBoundaryTag(pl, ctx.BoundaryHex?.Trim() ?? "");
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            drawnCount++;
            return true;
        }

        private static Point2d AlignColumnTapToHead(Point2d columnTap, Point2d spr, bool useVerticalBranches)
        {
            return useVerticalBranches
                ? new Point2d(spr.X, columnTap.Y)
                : new Point2d(columnTap.X, spr.Y);
        }

        private static bool TryDrawPerHeadBranch(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            Point2d tap,
            Point2d spr,
            ref int drawnCount)
        {
            if (!TryBuildStraightBranchPath(tap, spr, out List<Point2d> path))
                return false;
            if (path.Count != 2)
                return false;
            if (!BranchPathValidForRouting(ctx, path))
                return false;
            var pl = CreateHeadBranchPolyline(ctx.Db, path[0], path[1], elevation, branchLayerId, branchW);
            if (pl == null)
                return false;
            SprinklerXData.ApplyZoneBoundaryTag(pl, ctx.BoundaryHex?.Trim() ?? "");
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            drawnCount++;
            return true;
        }

        /// <summary>Labelling requires exactly one segment (two vertices): supply tap and one sprinkler.</summary>
        private static Polyline CreateHeadBranchPolyline(
            Database db,
            Point2d tap,
            Point2d head,
            double elevation,
            ObjectId layerId,
            double width)
        {
            if (tap.GetDistanceTo(head) < 1e-9)
                return null;
            return CreateBranchPolyline(db, new List<Point2d> { tap, head }, elevation, layerId, width);
        }

        /// <summary>
        /// Stub-end heads: outward auxiliary bar from trunk end vertex, then one straight two-point branch per head at spr.X / spr.Y.
        /// </summary>
        private static void DrawAuxiliaryFromEndVertexAndPerpendicularFeeds(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            List<Point2d> mainPipePts,
            Point2d endVertex,
            List<Point2d> sprinklers,
            List<int> pileIndices,
            bool useVerticalBranchDropsFromZoneMain,
            ref int drawnCount,
            HashSet<int> servedIndices)
        {
            if (pileIndices == null || pileIndices.Count == 0)
                return;

            if (useVerticalBranchDropsFromZoneMain)
            {
                double yBar = endVertex.Y;
                double outwardX = TrunkEndOutwardSign(mainPipePts, endVertex, axisIsX: true);
                double barEnd = endVertex.X;
                bool hasOutward = false;
                for (int p = 0; p < pileIndices.Count; p++)
                {
                    int si = pileIndices[p];
                    if (!IsOutwardAlongAxis(sprinklers[si].X, endVertex.X, outwardX))
                        continue;
                    hasOutward = true;
                    barEnd = outwardX > 0
                        ? Math.Max(barEnd, sprinklers[si].X)
                        : Math.Min(barEnd, sprinklers[si].X);
                }
                if (hasOutward && Math.Abs(barEnd - endVertex.X) > BranchAxisSnapEps)
                {
                    TryDrawTaggedBranchSegment(tr, ms, ctx, branchLayerId, branchW, elevation,
                        new List<Point2d> { new Point2d(endVertex.X, yBar), new Point2d(barEnd, yBar) }, ref drawnCount);
                }

                for (int p = 0; p < pileIndices.Count; p++)
                {
                    int si = pileIndices[p];
                    if (servedIndices.Contains(si))
                        continue;
                    Point2d spr = sprinklers[si];
                    if (!IsOutwardAlongAxis(spr.X, endVertex.X, outwardX))
                        continue;
                    Point2d tapAux = new Point2d(spr.X, yBar);
                    if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tapAux, spr, ref drawnCount))
                        servedIndices.Add(si);
                }
            }
            else
            {
                double xBar = endVertex.X;
                double outwardY = TrunkEndOutwardSign(mainPipePts, endVertex, axisIsX: false);
                double barEndY = endVertex.Y;
                bool hasOutward = false;
                for (int p = 0; p < pileIndices.Count; p++)
                {
                    int si = pileIndices[p];
                    if (!IsOutwardAlongAxis(sprinklers[si].Y, endVertex.Y, outwardY))
                        continue;
                    hasOutward = true;
                    barEndY = outwardY > 0
                        ? Math.Max(barEndY, sprinklers[si].Y)
                        : Math.Min(barEndY, sprinklers[si].Y);
                }
                if (hasOutward && Math.Abs(barEndY - endVertex.Y) > BranchAxisSnapEps)
                {
                    TryDrawTaggedBranchSegment(tr, ms, ctx, branchLayerId, branchW, elevation,
                        new List<Point2d> { new Point2d(xBar, endVertex.Y), new Point2d(xBar, barEndY) }, ref drawnCount);
                }

                for (int p = 0; p < pileIndices.Count; p++)
                {
                    int si = pileIndices[p];
                    if (servedIndices.Contains(si))
                        continue;
                    Point2d spr = sprinklers[si];
                    if (!IsOutwardAlongAxis(spr.Y, endVertex.Y, outwardY))
                        continue;
                    Point2d tapAux = new Point2d(xBar, spr.Y);
                    if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tapAux, spr, ref drawnCount))
                        servedIndices.Add(si);
                }
            }
        }

        private static double GetBranchSnapToleranceDu(Database db)
        {
            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double snapTol) && snapTol > 0)
                return snapTol;
            return 50.0;
        }

        private static double GetMaxSubBranchAttachDistanceDu(Database db, double sprinklerSpacingM)
        {
            double spacingM = sprinklerSpacingM > 1e-9 ? sprinklerSpacingM : 1.0;
            double meters = Math.Max(0.35 * spacingM, 1.5);
            if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, meters, out double du) && du > 0)
                return du;
            return Math.Max(0.35 * spacingM, 1.5);
        }

        private static bool IsBranchPipeLayerName(string layer)
        {
            return string.Equals(layer, SprinklerLayers.BranchPipeLayer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, SprinklerLayers.McdBranchPipeLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static List<BranchSegmentRef> CollectZoneBranchSegments(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx)
        {
            var segments = new List<BranchSegmentRef>();
            if (tr == null || ms == null || ctx == null)
                return segments;

            string zoneHex = ctx.BoundaryHex?.Trim() ?? "";
            bool scopeTags = !string.IsNullOrEmpty(zoneHex);

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Polyline pl = null;
                try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (pl == null || pl.NumberOfVertices < 2) continue;
                if (!IsBranchPipeLayerName(pl.Layer ?? "")) continue;
                if (SprinklerXData.IsTaggedTrunk(pl)) continue;
                if (scopeTags)
                {
                    if (!SprinklerXData.TryGetZoneBoundaryHandle(pl, out string entHex) ||
                        string.IsNullOrWhiteSpace(entHex) ||
                        !string.Equals(entHex.Trim(), zoneHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var verts = PolylineToPoint2dList(pl);
                for (int i = 0; i + 1 < verts.Count; i++)
                {
                    Point2d a = verts[i];
                    Point2d b = verts[i + 1];
                    if (a.GetDistanceTo(b) < 1e-9)
                        continue;
                    segments.Add(new BranchSegmentRef { A = a, B = b });
                }
            }
            return segments;
        }

        private static bool IsSprinklerServedByGeometry(
            Point2d spr,
            List<BranchSegmentRef> segments,
            double snapTol)
        {
            if (segments == null || segments.Count == 0)
                return false;
            for (int i = 0; i < segments.Count; i++)
            {
                Point2d a = segments[i].A;
                Point2d b = segments[i].B;
                if (spr.GetDistanceTo(a) <= snapTol || spr.GetDistanceTo(b) <= snapTol)
                    return true;
                Point2d close = ClosestPointOnSegment(a, b, spr);
                if (close.GetDistanceTo(spr) <= snapTol)
                    return true;
            }
            return false;
        }

        private static List<int> BuildUnservedIndicesForSubBranch(
            List<Point2d> sprinklers,
            HashSet<int> served,
            List<BranchSegmentRef> segments,
            double snapTol)
        {
            var unserved = new List<int>();
            for (int i = 0; i < sprinklers.Count; i++)
            {
                if (served.Contains(i))
                    continue;
                if (IsSprinklerServedByGeometry(sprinklers[i], segments, snapTol))
                {
                    served.Add(i);
                    continue;
                }
                unserved.Add(i);
            }
            return unserved;
        }

        private static bool TryFindClosestPointOnBranchSegments(
            Point2d spr,
            List<BranchSegmentRef> segments,
            double maxAttachDist,
            out Point2d attachPoint,
            out double distance)
        {
            attachPoint = default;
            distance = double.MaxValue;
            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                Point2d close = ClosestPointOnSegment(segments[i].A, segments[i].B, spr);
                double d = close.GetDistanceTo(spr);
                if (d < distance)
                {
                    distance = d;
                    attachPoint = close;
                }
            }

            if (distance > maxAttachDist)
                return false;
            if (attachPoint.GetDistanceTo(spr) < BranchAxisSnapEps)
                return false;
            return true;
        }

        private static bool TryBuildOrthogonalPathForRouting(
            BranchRouteContext ctx,
            Point2d from,
            Point2d to,
            bool verticalFirst,
            out List<Point2d> path)
        {
            path = null;
            if (from.GetDistanceTo(to) < 1e-7)
            {
                path = new List<Point2d> { from, to };
                return BranchPathValidForRouting(ctx, path);
            }

            var c1 = verticalFirst ? new Point2d(from.X, to.Y) : new Point2d(to.X, from.Y);
            if (BranchSegmentValidForRouting(ctx, from, c1) && BranchSegmentValidForRouting(ctx, c1, to))
            {
                path = CollapseCollinear(new List<Point2d> { from, c1, to });
                return path != null && path.Count >= 2;
            }

            var c2 = verticalFirst ? new Point2d(to.X, from.Y) : new Point2d(from.X, to.Y);
            if (BranchSegmentValidForRouting(ctx, from, c2) && BranchSegmentValidForRouting(ctx, c2, to))
            {
                path = CollapseCollinear(new List<Point2d> { from, c2, to });
                return path != null && path.Count >= 2;
            }

            return false;
        }

        private static bool TryDrawOrthogonalSubBranchToHead(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            Point2d attach,
            Point2d spr,
            bool verticalFirst,
            ref int drawnCount)
        {
            if (TryBuildStraightBranchPath(attach, spr, out List<Point2d> straight) &&
                BranchPathValidForRouting(ctx, straight))
            {
                return TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, attach, spr, ref drawnCount);
            }

            if (!TryBuildOrthogonalPathForRouting(ctx, attach, spr, verticalFirst, out List<Point2d> path) &&
                !TryBuildOrthogonalPathForRouting(ctx, attach, spr, !verticalFirst, out path))
                return false;

            if (path == null || path.Count < 2)
                return false;

            if (path.Count == 2)
                return TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, path[0], path[1], ref drawnCount);

            for (int i = 0; i + 1 < path.Count - 1; i++)
            {
                Point2d legA = path[i];
                Point2d legB = path[i + 1];
                if (legA.GetDistanceTo(legB) < BranchAxisSnapEps)
                    continue;
                if (!TryBuildStraightBranchPath(legA, legB, out List<Point2d> feeder) ||
                    !BranchPathValidForRouting(ctx, feeder))
                    return false;
                if (!TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, legA, legB, ref drawnCount))
                    return false;
            }

            Point2d headLegStart = path[path.Count - 2];
            Point2d head = path[path.Count - 1];
            return TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, headLegStart, head, ref drawnCount);
        }

        /// <summary>
        /// After main/aux routing: connect remaining heads via orthogonal sub-branches from nearest existing branch pipe only.
        /// </summary>
        private static int RouteSubBranchesFromNearestBranchPipe(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            List<Point2d> sprinklers,
            HashSet<int> served,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            bool mainRunsAlongX,
            double sprinklerSpacingM,
            ref int drawnCount)
        {
            int subDrawn = 0;
            if (sprinklers == null || sprinklers.Count == 0 || ctx == null)
                return 0;

            double snapTol = GetBranchSnapToleranceDu(ctx.Db);
            double maxAttachDist = GetMaxSubBranchAttachDistanceDu(ctx.Db, sprinklerSpacingM);
            bool verticalFirst = mainRunsAlongX;

            for (int pass = 0; pass < 2; pass++)
            {
                var segments = CollectZoneBranchSegments(tr, ms, ctx);
                if (segments.Count == 0)
                    break;

                var unserved = BuildUnservedIndicesForSubBranch(sprinklers, served, segments, snapTol);
                if (unserved.Count == 0)
                    break;

                var order = new List<(int index, double dist)>(unserved.Count);
                for (int u = 0; u < unserved.Count; u++)
                {
                    int si = unserved[u];
                    if (TryFindClosestPointOnBranchSegments(sprinklers[si], segments, maxAttachDist, out _, out double d))
                        order.Add((si, d));
                    else
                        order.Add((si, double.MaxValue));
                }
                order.Sort((a, b) => a.dist.CompareTo(b.dist));

                int servedThisPass = 0;
                for (int u = 0; u < order.Count; u++)
                {
                    int si = order[u].index;
                    if (served.Contains(si))
                        continue;
                    if (IsSprinklerServedByGeometry(sprinklers[si], segments, snapTol))
                    {
                        served.Add(si);
                        servedThisPass++;
                        continue;
                    }

                    if (!TryFindClosestPointOnBranchSegments(sprinklers[si], segments, maxAttachDist, out Point2d attach, out _))
                        continue;

                    int before = drawnCount;
                    if (TryDrawOrthogonalSubBranchToHead(
                        tr, ms, ctx, branchLayerId, branchW, elevation, attach, sprinklers[si], verticalFirst, ref drawnCount))
                    {
                        served.Add(si);
                        servedThisPass++;
                        subDrawn += drawnCount - before;
                    }
                }

                if (servedThisPass == 0)
                    break;
            }

            return subDrawn;
        }

        /// <summary>Single straight segment (horizontal or vertical only) from tap on trunk to head — no L-shaped paths off the main.</summary>
        private static bool TryBuildStraightBranchPath(Point2d tap, Point2d spr, out List<Point2d> path)
        {
            path = CollapseCollinear(new List<Point2d> { tap, spr });
            if (path.Count < 2)
                return false;
            return Math.Abs(tap.X - spr.X) < BranchAxisSnapEps || Math.Abs(tap.Y - spr.Y) < BranchAxisSnapEps;
        }

        /// <summary>One straight two-point branch per sprinkler; column groups pick shared taps only.</summary>
        private static int DrawSimplePerpendicularBranchesFromMain(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            List<Point2d> mainPipePts,
            List<Point2d> sprinklers,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            bool mainRunsAlongX,
            double sprinklerSpacingM,
            out int servedHeadCount,
            out int subBranchDrawn)
        {
            int drawnCount = 0;
            servedHeadCount = 0;
            subBranchDrawn = 0;
            bool useVerticalBranches = mainRunsAlongX;
            var served = new HashSet<int>();
            double clusterTol = ColumnClusterToleranceM(sprinklerSpacingM);
            var columnGroups = BuildSprinklerColumnGroups(sprinklers, clusterByX: useVerticalBranches, clusterTol);
            var headToGroup = new int[sprinklers.Count];
            for (int g = 0; g < headToGroup.Length; g++)
                headToGroup[g] = -1;
            for (int g = 0; g < columnGroups.Count; g++)
            {
                for (int k = 0; k < columnGroups[g].Indices.Count; k++)
                    headToGroup[columnGroups[g].Indices[k]] = g;
            }

            var columnAux = new int[columnGroups.Count];
            var columnMainTap = new Point2d?[columnGroups.Count];
            for (int g = 0; g < columnGroups.Count; g++)
            {
                columnAux[g] = ClassifyColumnAuxPile(mainPipePts, sprinklers, columnGroups[g], useVerticalBranches);
                if (columnAux[g] == 0 || columnAux[g] == 1)
                    continue;
                double columnX = useVerticalBranches
                    ? MedianCoordInBucket(sprinklers, columnGroups[g].Indices, useX: true)
                    : MedianCoordInBucket(sprinklers, columnGroups[g].Indices, useX: false);
                if (TryPickColumnTapOnMain(mainPipePts, columnX, useVerticalBranches, sprinklers, columnGroups[g].Indices, out Point2d tap))
                    columnMainTap[g] = tap;
            }

            for (int g = 0; g < columnGroups.Count; g++)
            {
                if (columnAux[g] == 0 || columnAux[g] == 1)
                    continue;

                if (columnMainTap[g].HasValue)
                {
                    Point2d columnTap = columnMainTap[g].Value;
                    for (int k = 0; k < columnGroups[g].Indices.Count; k++)
                    {
                        int si = columnGroups[g].Indices[k];
                        if (served.Contains(si))
                            continue;
                        Point2d tap = AlignColumnTapToHead(columnTap, sprinklers[si], useVerticalBranches);
                        if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tap, sprinklers[si], ref drawnCount))
                            served.Add(si);
                    }
                    continue;
                }

                for (int k = 0; k < columnGroups[g].Indices.Count; k++)
                {
                    int si = columnGroups[g].Indices[k];
                    if (served.Contains(si))
                        continue;
                    if (!TryPickPerpendicularTapOnMain(mainPipePts, sprinklers[si], useVerticalBranches, out Point2d tapPerc))
                        continue;
                    Point2d tap = AlignColumnTapToHead(tapPerc, sprinklers[si], useVerticalBranches);
                    if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tap, sprinklers[si], ref drawnCount))
                        served.Add(si);
                }
            }

            var pileNearStart = new List<int>();
            var pileNearEnd = new List<int>();
            for (int g = 0; g < columnGroups.Count; g++)
            {
                if (columnAux[g] == 0)
                    pileNearStart.AddRange(columnGroups[g].Indices);
                else if (columnAux[g] == 1)
                    pileNearEnd.AddRange(columnGroups[g].Indices);
            }

            DrawAuxiliaryFromEndVertexAndPerpendicularFeeds(
                tr, ms, ctx, branchLayerId, branchW, elevation,
                mainPipePts, mainPipePts[0], sprinklers, pileNearStart, useVerticalBranches, ref drawnCount, served);
            DrawAuxiliaryFromEndVertexAndPerpendicularFeeds(
                tr, ms, ctx, branchLayerId, branchW, elevation,
                mainPipePts, mainPipePts[mainPipePts.Count - 1], sprinklers, pileNearEnd, useVerticalBranches, ref drawnCount, served);

            for (int i = 0; i < sprinklers.Count; i++)
            {
                if (served.Contains(i))
                    continue;

                int g = headToGroup[i];
                if (g >= 0 && columnAux[g] != 0 && columnAux[g] != 1 && columnMainTap[g].HasValue)
                {
                    Point2d tap = AlignColumnTapToHead(columnMainTap[g].Value, sprinklers[i], useVerticalBranches);
                    if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tap, sprinklers[i], ref drawnCount))
                    {
                        served.Add(i);
                        continue;
                    }
                }

                if (g >= 0 && (columnAux[g] == 0 || columnAux[g] == 1))
                {
                    Point2d endVertex = columnAux[g] == 0 ? mainPipePts[0] : mainPipePts[mainPipePts.Count - 1];
                    Point2d spr = sprinklers[i];
                    if (useVerticalBranches)
                    {
                        double outwardX = TrunkEndOutwardSign(mainPipePts, endVertex, axisIsX: true);
                        if (IsOutwardAlongAxis(spr.X, endVertex.X, outwardX))
                        {
                            Point2d tapAux = new Point2d(spr.X, endVertex.Y);
                            if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tapAux, spr, ref drawnCount))
                            {
                                served.Add(i);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        double outwardY = TrunkEndOutwardSign(mainPipePts, endVertex, axisIsX: false);
                        if (IsOutwardAlongAxis(spr.Y, endVertex.Y, outwardY))
                        {
                            Point2d tapAux = new Point2d(endVertex.X, spr.Y);
                            if (TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tapAux, spr, ref drawnCount))
                            {
                                served.Add(i);
                                continue;
                            }
                        }
                    }
                }

                if (TryDrawStraightBranchFromMainForHead(
                    tr, ms, ctx, mainPipePts, sprinklers[i], branchLayerId, branchW, elevation,
                    useVerticalBranches, ref drawnCount))
                    served.Add(i);
            }

            subBranchDrawn = RouteSubBranchesFromNearestBranchPipe(
                tr, ms, ctx, sprinklers, served, branchLayerId, branchW, elevation, mainRunsAlongX, sprinklerSpacingM, ref drawnCount);

            servedHeadCount = served.Count;
            return drawnCount;
        }

        private static bool TryCombRouteBranchesForZone(
            Database db,
            Polyline zone,
            List<Point2d> zoneRing,
            string zoneBoundaryHex,
            ObjectId trunkId,
            List<SprinklerHeadReader2d.FloorRoomOwnership> floorRoomOwnerships,
            out string errorMessage)
        {
            errorMessage = null;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.GetDocument(db);
            if (doc == null)
            {
                errorMessage = "Cannot get document from database.";
                return false;
            }

            using (var docLock = doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Polyline trunk = null;
                    try { trunk = tr.GetObject(trunkId, OpenMode.ForRead, false) as Polyline; }
                    catch { }
                    if (trunk == null)
                    {
                        errorMessage = "Trunk polyline not found.";
                        return false;
                    }

                    var mainPipePts = new List<Point2d>();
                    for (int i = 0; i < trunk.NumberOfVertices; i++)
                    {
                        var pt3 = trunk.GetPoint3dAt(i);
                        mainPipePts.Add(new Point2d(pt3.X, pt3.Y));
                    }

                    var sprinklers = new List<Point2d>();
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        Entity ent = null;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        if (ent == null) continue;

                        if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent)) continue;

                        Point2d p = default;
                        if (ent is Circle c)
                            p = new Point2d(c.Center.X, c.Center.Y);
                        else if (ent is BlockReference br)
                            p = new Point2d(br.Position.X, br.Position.Y);
                        else
                            continue;

                        if (!PointInPolygon(zoneRing, p)) continue;
                        sprinklers.Add(p);
                    }

                    if (sprinklers.Count == 0)
                    {
                        errorMessage = "No sprinklers found in zone.";
                        return false;
                    }

                    bool mainAlongX = MainRunsPrimarilyAlongX(mainPipePts);
                    double spacingM = RuntimeSettings.Load().SprinklerSpacingM;
                    var routeCtx = new BranchRouteContext
                    {
                        Db = db,
                        ZoneRing = zoneRing,
                        BoundaryHex = zoneBoundaryHex,
                        FloorRooms = floorRoomOwnerships
                    };

                    ObjectId branchLayerId = SprinklerLayers.EnsureMcdBranchPipeLayer(tr, db);
                    double mainW = EstimatePipeWidth(db);
                    double branchW = Math.Max(mainW * 0.66, 1.0);
                    double elevation = zone.Elevation;

                    int drawnCount = DrawSimplePerpendicularBranchesFromMain(
                        tr, ms, routeCtx, mainPipePts, sprinklers, branchLayerId, branchW, elevation, mainAlongX, spacingM,
                        out int servedHeads, out int subBranchDrawn);

                    tr.Commit();
                    int total = sprinklers.Count;
                    int unserved = total - servedHeads;
                    string dropKind = mainAlongX ? "vertical drops" : "horizontal drops";
                    errorMessage = servedHeads > 0
                        ? $"Branches ({dropKind}): {servedHeads}/{total} heads served, {drawnCount} segment(s). Sub-branches: {subBranchDrawn}. Unserved: {unserved}."
                        : "No perpendicular branch paths could be validated inside the zone.";
                    return servedHeads > 0;
                }
                catch (Exception ex)
                {
                    errorMessage = "Branch routing failed: " + ex.Message;
                    return false;
                }
            }
        }

        private static Point2d ClosestPointOnPolylineWithArcFraction(List<Point2d> pts, Point2d p, out double arcFrac01)
        {
            arcFrac01 = 0;
            if (pts == null || pts.Count == 0)
                return p;
            if (pts.Count == 1)
            {
                arcFrac01 = 0;
                return pts[0];
            }

            double totalLen = 0;
            var segLens = new double[pts.Count - 1];
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double len = pts[i].GetDistanceTo(pts[i + 1]);
                segLens[i] = len;
                totalLen += len;
            }

            double minDist = double.MaxValue;
            Point2d best = p;
            double bestArcDist = 0;

            double cum = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Point2d a = pts[i];
                Point2d b = pts[i + 1];
                double segLen = segLens[i];
                Point2d close = ClosestPointOnSegment(a, b, p);
                double d = close.GetDistanceTo(p);
                if (d < minDist)
                {
                    minDist = d;
                    best = close;
                    double segLenSq = segLen * segLen;
                    double tParam = segLenSq < 1e-24
                        ? 0
                        : ((close.X - a.X) * (b.X - a.X) + (close.Y - a.Y) * (b.Y - a.Y)) / segLenSq;
                    tParam = Math.Max(0, Math.Min(1, tParam));
                    bestArcDist = cum + tParam * segLen;
                }
                cum += segLen;
            }

            if (totalLen < MainLineIntersectEps)
            {
                arcFrac01 = 0;
                return best;
            }
            arcFrac01 = Math.Max(0, Math.Min(1, bestArcDist / totalLen));
            return best;
        }

        /// <returns>-1 = may use trunk; 0 = start auxiliary only; 1 = end auxiliary only.</returns>
        private static int ClassifyAuxOnlyPile(List<Point2d> mainPipePts, Point2d spr, out double arcT01)
        {
            ClosestPointOnPolylineWithArcFraction(mainPipePts, spr, out arcT01);
            double z = TrunkAuxEndZoneArcFrac;
            if (z * 2 >= 1.0 - 1e-9)
                return arcT01 <= 0.5 ? 0 : 1;

            bool startBand = arcT01 <= z;
            bool endBand = arcT01 >= 1.0 - z;
            if (startBand && !endBand) return 0;
            if (endBand && !startBand) return 1;
            if (startBand && endBand) return arcT01 <= 0.5 ? 0 : 1;
            return -1;
        }

        /// <summary>Candidate aux pile is kept only when the sprinkler lies outward from that trunk end (not in the inward transition strip).</summary>
        private static int FilterAuxPileToOutwardSprinklerOnly(
            List<Point2d> mainPipePts,
            Point2d spr,
            int candidatePile,
            bool useVerticalBranches)
        {
            if (candidatePile != 0 && candidatePile != 1)
                return -1;

            Point2d endVertex = candidatePile == 0
                ? mainPipePts[0]
                : mainPipePts[mainPipePts.Count - 1];
            double outwardSign = TrunkEndOutwardSign(mainPipePts, endVertex, useVerticalBranches);
            double coord = useVerticalBranches ? spr.X : spr.Y;
            double anchor = useVerticalBranches ? endVertex.X : endVertex.Y;
            return IsOutwardAlongAxis(coord, anchor, outwardSign) ? candidatePile : -1;
        }

        /// <summary>
        /// End-zone heads outward from a trunk end only; transition strip toward center uses the main (returns -1).
        /// </summary>
        private static int ClassifyAuxPile(
            List<Point2d> mainPipePts,
            Point2d spr,
            bool useVerticalBranches,
            out double arcT01)
        {
            int candidate = -1;

            int arcPile = ClassifyAuxOnlyPile(mainPipePts, spr, out arcT01);
            if (arcPile == 0 || arcPile == 1)
                candidate = arcPile;

            if (candidate < 0)
            {
                Point2d v0 = mainPipePts[0];
                Point2d v1 = mainPipePts[mainPipePts.Count - 1];
                double pad = Math.Max(AuxColumnGroupingEps, 0.02 * v0.GetDistanceTo(v1));

                if (useVerticalBranches)
                {
                    double minX = Math.Min(v0.X, v1.X);
                    double maxX = Math.Max(v0.X, v1.X);
                    if (spr.X < minX - pad)
                        candidate = 0;
                    else if (spr.X > maxX + pad)
                        candidate = 1;
                }
                else
                {
                    double minY = Math.Min(v0.Y, v1.Y);
                    double maxY = Math.Max(v0.Y, v1.Y);
                    if (spr.Y < minY - pad)
                        candidate = 0;
                    else if (spr.Y > maxY + pad)
                        candidate = 1;
                }
            }

            if (candidate < 0 && TryPickPerpendicularTapOnMain(mainPipePts, spr, useVerticalBranches, out Point2d tapPerc))
            {
                ClosestPointOnPolylineWithArcFraction(mainPipePts, tapPerc, out double tapArc);
                double z = TrunkAuxEndZoneArcFrac;
                if (tapArc <= z)
                    candidate = 0;
                else if (tapArc >= 1.0 - z)
                    candidate = 1;
            }

            return FilterAuxPileToOutwardSprinklerOnly(mainPipePts, spr, candidate, useVerticalBranches);
        }

        private static bool TryDrawStraightBranchFromMainForHead(
            Transaction tr,
            BlockTableRecord ms,
            BranchRouteContext ctx,
            List<Point2d> mainPipePts,
            Point2d spr,
            ObjectId branchLayerId,
            double branchW,
            double elevation,
            bool useVerticalBranches,
            ref int drawnCount)
        {
            if (!TryPickPerpendicularTapOnMain(mainPipePts, spr, useVerticalBranches, out Point2d tapPerc))
                return false;
            return TryDrawPerHeadBranch(tr, ms, ctx, branchLayerId, branchW, elevation, tapPerc, spr, ref drawnCount);
        }

        private static Point2d ClosestPointOnPolyline(List<Point2d> pts, Point2d p)
        {
            return ClosestPointOnPolylineWithArcFraction(pts, p, out _);
        }



        private static Point2d ClosestPointOnSegment(Point2d a, Point2d b, Point2d p)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return a;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point2d(a.X + t * dx, a.Y + t * dy);
        }

        private static bool BuildOrthogonalPath(Point2d from, Point2d to, List<Point2d> ring, bool verticalFirst, out List<Point2d> path)
        {
            path = null;
            if (from.GetDistanceTo(to) < 1e-7)
            {
                path = new List<Point2d> { from, to };
                return true;
            }

            var c1 = verticalFirst ? new Point2d(from.X, to.Y) : new Point2d(to.X, from.Y);
            if (SegmentInsideRing(ring, from, c1) && SegmentInsideRing(ring, c1, to))
            {
                path = CollapseCollinear(new List<Point2d> { from, c1, to });
                return path.Count >= 2;
            }

            var c2 = verticalFirst ? new Point2d(to.X, from.Y) : new Point2d(from.X, to.Y);
            if (SegmentInsideRing(ring, from, c2) && SegmentInsideRing(ring, c2, to))
            {
                path = CollapseCollinear(new List<Point2d> { from, c2, to });
                return path.Count >= 2;
            }

            path = null;
            return false;
        }

        private static bool SegmentInsideRing(List<Point2d> ring, Point2d p0, Point2d p1)
        {
            const int samples = 14;
            for (int i = 0; i <= samples; i++)
            {
                double t = i / (double)samples;
                var p = new Point2d(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
                if (!PointInPolygon(ring, p))
                    return false;
            }
            return true;
        }

        private static List<Point2d> CollapseCollinear(List<Point2d> verts)
        {
            if (verts == null || verts.Count < 2)
                return verts ?? new List<Point2d>();
            var result = new List<Point2d> { verts[0] };
            for (int i = 1; i < verts.Count; i++)
                if (result[result.Count - 1].GetDistanceTo(verts[i]) > 1e-9)
                    result.Add(verts[i]);
            return result;
        }

        private static Polyline CreateBranchPolyline(Database db, List<Point2d> verts, double elevation, ObjectId layerId, double width)
        {
            if (verts == null || verts.Count < 2)
                return null;
            var pl = new Polyline();
            pl.SetDatabaseDefaults(db);
            pl.LayerId = layerId;
            pl.ConstantWidth = width;
            pl.Elevation = elevation;
            pl.Closed = false;
            for (int i = 0; i < verts.Count; i++)
                pl.AddVertexAt(i, verts[i], 0, 0, 0);
            return pl;
        }

        private static double EstimatePipeWidth(Database db)
        {
            return NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
        }
    }
}
