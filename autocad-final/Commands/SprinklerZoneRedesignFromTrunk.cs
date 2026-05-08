using System;
using System.Collections.Generic;
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

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int zoneTaggedErased = EraseEntitiesWithZoneBoundaryTag(tr, ms, boundaryHandleHex, preserveSprinklerHeads, trunkId);
                int capsErased = EraseTrunkCapsInZone(tr, ms, zoneRing);
                int erased = EraseEntitiesInZone(tr, ms, zoneRing, preserveSprinklerHeads, trunkId);
                int outsideSprinklersErased = EraseSprinklerMarkersOutsideZone(tr, ms, zoneRing, boundaryHandleHex);
                int outsideBranchErased = EraseBranchPipingMostlyOutsideZone(tr, ms, zoneRing, db, boundaryHandleHex);

                TryTrimTrunkIfTouchingBoundary(tr, db, trunkId, zoneRing);

                EraseConnectorInZone(tr, ms, trunkId, zoneRing);
                Point3d shaft3;
                bool hasShaft = selectedShaftPoint.HasValue;
                if (hasShaft)
                    shaft3 = selectedShaftPoint.Value;
                else
                    hasShaft = TryFindShaftForZone(db, zone, boundaryHandleHex, out shaft3, out _);
                if (hasShaft)
                {
                    var trunkPl = tr.GetObject(trunkId, OpenMode.ForRead, false) as Polyline;
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
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                            continue;
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
                    movedExisting = SnapExistingSprinklerHeadsInsideZone(tr, db, ms, zone, zoneRing, boundaryHandleHex);
                    tr.Commit();
                }

                ed.WriteMessage("\nRedesign: keeping existing sprinkler head positions; updating main pipe connector/caps and branch piping.\n");
                if (movedExisting > 0)
                    ed.WriteMessage("\nBoundary snap adjusted " + movedExisting.ToString() + " existing sprinkler heads inward.\n");
            }

            if (!AttachBranchesCommand.TryAttachBranchesForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string branchMsg))
            {
                errorMessage = branchMsg ?? "Branch attach failed.";
                return false;
            }
            if (!AttachBranchesCommand.TryPlaceReducersForZone(doc, db, zone, zoneRing, boundaryHandleHex, routeBranchPipesFromConnectorFirst: false, ObjectId.Null, out string redMsg))
            {
                errorMessage = redMsg ?? "Place reducers failed.";
                return false;
            }

            ed.WriteMessage("\n" + branchMsg + "\n");
            ed.WriteMessage("\n" + redMsg + "\n");
            try { ed.Regen(); } catch { /* ignore */ }
            return true;
        }

        private static int EraseEntitiesWithZoneBoundaryTag(Transaction tr, BlockTableRecord ms, string boundaryHandleHex, bool preserveSprinklerMarkers, ObjectId preserveTrunkId)
        {
            int erased = 0;
            if (tr == null || ms == null || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            foreach (ObjectId id in ms)
            {
                if (!preserveTrunkId.IsNull && id == preserveTrunkId)
                    continue;
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;
                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SprinklerXData.IsTaggedTrunk(ent))
                    continue;
                if (preserveSprinklerMarkers && SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int SnapExistingSprinklerHeadsInsideZone(
            Transaction tr,
            Database db,
            BlockTableRecord ms,
            Polyline zone,
            List<Point2d> zoneRing,
            string boundaryHandleHex)
        {
            if (tr == null || db == null || ms == null || zone == null || zoneRing == null || zoneRing.Count < 3)
                return 0;

            var entities = new List<Entity>();
            var points = new List<Point2d>();

            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;
                if (!SprinklerLayers.IsSprinklerHeadEntity(tr, ent))
                    continue;

                bool inThisZone =
                    (SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) &&
                     string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase));

                Point2d p;
                if (ent is BlockReference br)
                {
                    p = new Point2d(br.Position.X, br.Position.Y);
                    if (!inThisZone)
                        inThisZone = PointInPolygon(zoneRing, p);
                }
                else if (ent is Circle c)
                {
                    p = new Point2d(c.Center.X, c.Center.Y);
                    if (!inThisZone)
                        inThisZone = PointInPolygon(zoneRing, p);
                }
                else
                    continue;

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
                    spacingMeters: RuntimeSettings.Load().SprinklerSpacingM,
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

        private static int EraseEntitiesInZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing, bool preserveSprinklerHeads, ObjectId preserveTrunkId)
        {
            int erased = 0;
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3)
                return 0;

            foreach (ObjectId id in ms)
            {
                if (!preserveTrunkId.IsNull && id == preserveTrunkId)
                    continue;
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
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

        private static int EraseSprinklerMarkersOutsideZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing, string boundaryHandleHex)
        {
            int erased = 0;
            if (tr == null || ms == null || zoneRing == null || zoneRing.Count < 3 || string.IsNullOrEmpty(boundaryHandleHex))
                return 0;

            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;

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

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static int EraseBranchPipingMostlyOutsideZone(Transaction tr, BlockTableRecord ms, List<Point2d> zoneRing, Database db, string boundaryHandleHex)
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
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;

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
                    wipe = BranchPolylineMostlyOutsideZone(pl, zoneRing, sampleStepDu, minInsideFractionToKeep);
                else if (ent is Line ln)
                    wipe = BranchLineMostlyOutsideZone(ln, zoneRing, minInsideFractionToKeep);
                else
                    continue;

                if (!wipe)
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); erased++; } catch { /* ignore */ }
            }

            return erased;
        }

        private static bool BranchPolylineMostlyOutsideZone(Polyline pl, List<Point2d> zoneRing, double sampleStepDu, double minInsideFractionToKeep)
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
                    return !PointInPolygon(zoneRing, new Point2d(v.X, v.Y));
                }

                int n = Math.Max(8, (int)Math.Ceiling(len / Math.Max(sampleStepDu, 1e-9)));
                int inside = 0;
                for (int i = 0; i <= n; i++)
                {
                    double d = len * i / n;
                    var p3 = pl.GetPointAtDist(d);
                    if (PointInPolygon(zoneRing, new Point2d(p3.X, p3.Y)))
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

        private static bool BranchLineMostlyOutsideZone(Line ln, List<Point2d> zoneRing, double minInsideFractionToKeep)
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
                    if (PointInPolygon(zoneRing, new Point2d(x, y)))
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
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;
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
                if (id == trunkId) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    continue;
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
                                zoneEnt = tr.GetObject(zid, OpenMode.ForRead, false) as Entity;
                        }
                        catch { /* ignore */ }
                    }
                    if (zoneEnt == null && !zone.ObjectId.IsNull)
                        zoneEnt = tr.GetObject(zone.ObjectId, OpenMode.ForRead, false) as Entity;

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
                                var shaftBr = tr.GetObject(shaftId, OpenMode.ForRead, false) as BlockReference;
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
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (!(ent is BlockReference br)) continue;
                    string blockName = GetBlockName(br, tr);
                    if (!string.Equals(blockName, "shaft", StringComparison.OrdinalIgnoreCase))
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
                errorMessage = "No shaft blocks found (block name \"shaft\").";
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

            var trunk = tr.GetObject(trunkId, OpenMode.ForRead, false) as Polyline;
            if (trunk == null)
                return;

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            if (tol <= 0) tol = 1e-6;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            int nv = trunk.NumberOfVertices;
            for (int i = 0; i < nv; i++)
            {
                var p = trunk.GetPoint3dAt(i);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }

            bool trunkHorizontal = (maxY - minY) <= tol * 10.0 && (maxX - minX) > tol * 10.0;

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

            var a = trunk.StartPoint;
            var b = trunk.EndPoint;

            SprinklerXData.EnsureRegApp(tr, db);
            ObjectId mainPipeLayerId = SprinklerLayers.EnsureMainPipeLayer(tr, db);
            DrawCapAt(new Point2d(a.X, a.Y));
            DrawCapAt(new Point2d(b.X, b.Y));

            void DrawCapAt(Point2d p)
            {
                var cap = new Polyline();
                cap.SetDatabaseDefaults(db);
                cap.LayerId = mainPipeLayerId;
                cap.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                cap.ConstantWidth = w;
                cap.Elevation = elevation;
                cap.Normal = normal;

                if (trunkHorizontal)
                {
                    cap.AddVertexAt(0, new Point2d(p.X, p.Y - h), 0, 0, 0);
                    cap.AddVertexAt(1, new Point2d(p.X, p.Y + h), 0, 0, 0);
                }
                else
                {
                    cap.AddVertexAt(0, new Point2d(p.X - h, p.Y), 0, 0, 0);
                    cap.AddVertexAt(1, new Point2d(p.X + h, p.Y), 0, 0, 0);
                }

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

            var trunk = tr.GetObject(trunkId, OpenMode.ForWrite, false) as Polyline;
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

            double len = trunkHorizontal ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y);
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
    }
}
