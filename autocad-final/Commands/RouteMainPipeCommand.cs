using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class RouteMainPipeCommand
    {
        [CommandMethod("SPRINKLERROUTEMAINPIPE", CommandFlags.Modal)]
        public void RouteMainPipe()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
            {
                if (!string.IsNullOrWhiteSpace(shaftErr))
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, MessageBoxIcon.Warning);
                return;
            }

            // Priority: explicit ASSIGNSHAFTOZONE xdata on the shaft → then spatial containment.
            if (!TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "Could not find zone boundary for the selected shaft.",
                    MessageBoxIcon.Warning);
                return;
            }

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                boundaryHandleHex = tr0.GetObject(boundaryEntityId, OpenMode.ForRead).Handle.ToString();
                tr0.Commit();
            }

            try
            {
                if (!TryRouteMainPipeForZone(doc, zone, boundaryHandleHex, shaftPoint, out string routeErr, out string routeSummary))
                {
                    if (!string.IsNullOrEmpty(routeErr))
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, routeErr, MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\nMain pipe routed. " + (routeSummary ?? string.Empty) + "\n");
                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Detects shaft, computes route, and draws tagged trunk + connector + caps for the zone.
        /// </summary>
        internal static bool TryRouteMainPipeForZone(
            Document doc,
            Polyline zone,
            string boundaryHandleHex,
            out string errorMessage,
            out string routeSummary,
            double? sprinklerSpacingMeters = null,
            double? sprinklerCoverageRadiusMeters = null,
            string preferredOrientation = null,
            string strategy = null,
            double? skeletonCellSizeMeters = null,
            double? skeletonMinClearanceMeters = null,
            double? skeletonPruneBranchLengthMeters = null,
            double? mainPipeLengthPenalty = null)
        {
            errorMessage = null;
            routeSummary = null;
            if (doc == null || zone == null)
            {
                errorMessage = "Invalid inputs.";
                return false;
            }

            var db = doc.Database;
            AgentLog.Write("TryRouteMainPipeForZone", "resolving shaft via priority lookup");

            Point3d shaft;
            bool usedExplicitAssignment = false;

            if (FindShaftsInsideBoundary.TryGetShaftForZone(db, boundaryHandleHex, zone, out var shaftInfo, out string shaftLookupErr))
            {
                shaft = shaftInfo.Position;
                AgentLog.Write("TryRouteMainPipeForZone",
                    "shaft resolved at " + shaft.X.ToString("F1") + "," + shaft.Y.ToString("F1"));

                // Detect if this came from xdata assignment (shaft may be outside zone).
                using (var trChk = db.TransactionManager.StartTransaction())
                {
                    Handle zh;
                    try { zh = new Handle(Convert.ToInt64(boundaryHandleHex, 16)); } catch { zh = default; }
                    if (zh.Value != 0)
                    {
                        ObjectId zId = ObjectId.Null;
                        try { zId = db.GetObjectId(false, zh, 0); } catch { }
                        if (!zId.IsNull)
                        {
                            var zEnt = trChk.GetObject(zId, OpenMode.ForRead, false) as Entity;
                            if (zEnt != null && SprinklerXData.TryGetShaftAssignmentHandle(zEnt, out _))
                                usedExplicitAssignment = true;
                        }
                    }
                    trChk.Commit();
                }
            }
            else
            {
                AgentLog.Write("TryRouteMainPipeForZone", "priority lookup failed (" + shaftLookupErr + "), falling back to nearest shaft search");
                if (!TryFindNearestShaftToZone(db, zone, out shaft, out string nearErr))
                {
                    AgentLog.Write("TryRouteMainPipeForZone", "shaft not found: " + nearErr);
                    errorMessage = nearErr;
                    return false;
                }
                AgentLog.Write("TryRouteMainPipeForZone", "nearest shaft at " + shaft.X.ToString("F1") + "," + shaft.Y.ToString("F1"));
            }

            bool routed = TryRouteMainPipeForZone(
                doc,
                zone,
                boundaryHandleHex,
                shaft,
                out errorMessage,
                out routeSummary,
                sprinklerSpacingMeters,
                sprinklerCoverageRadiusMeters,
                preferredOrientation,
                strategy,
                skeletonCellSizeMeters,
                skeletonMinClearanceMeters,
                skeletonPruneBranchLengthMeters,
                mainPipeLengthPenalty);

            if (routed && usedExplicitAssignment && !string.IsNullOrEmpty(routeSummary))
            {
                routeSummary += "\nNote: Routed from explicitly assigned shaft (may be outside zone boundary).";
            }

            return routed;
        }

        /// <summary>
        /// Computes route from an explicit shaft point and draws tagged trunk + connector + caps for the zone.
        /// </summary>
        internal static bool TryRouteMainPipeForZone(
            Document doc,
            Polyline zone,
            string boundaryHandleHex,
            Point3d shaft,
            out string errorMessage,
            out string routeSummary,
            double? sprinklerSpacingMeters = null,
            double? sprinklerCoverageRadiusMeters = null,
            string preferredOrientation = null,
            string strategy = null,
            double? skeletonCellSizeMeters = null,
            double? skeletonMinClearanceMeters = null,
            double? skeletonPruneBranchLengthMeters = null,
            double? mainPipeLengthPenalty = null)
        {
            errorMessage = null;
            routeSummary = null;
            if (doc == null || zone == null)
            {
                errorMessage = "Invalid inputs.";
                return false;
            }

            var db = doc.Database;
            var cfg = RuntimeSettings.Load();
            double spacingM = sprinklerSpacingMeters ?? cfg.SprinklerSpacingM;
            double radiusM = sprinklerCoverageRadiusMeters ?? 1.5;

            string[] strategies;
            if (!string.IsNullOrWhiteSpace(strategy))
                strategies = new[] { strategy.Trim().ToLowerInvariant() };
            else
                // UI expectation: produce a simple orthogonal “L” toward the zone center.
                // Keep a conservative fallback to sprinkler-driven, but do not auto-pick skeleton here.
                strategies = new[] { "center_l", "sprinkler_driven" };

            // Read existing sprinkler heads in this zone (created by Apply Sprinklers) and route a straight trunk
            // that can serve them via orthogonal branch drops.
            var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            double spacingDu = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, spacingM, out double s) && s > 0)
                    spacingDu = s;
            }
            catch { /* ignore */ }

            double tol = BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db);
            double clusterTol = Math.Max(tol * 10.0, spacingDu * 0.35);

            if (!SprinklerHeadReader2d.TryReadSprinklerHeadPointsForZoneRouting(db, zoneRing, boundaryHandleHex, clusterTol, out var sprinklers, out string sprErr))
            {
                errorMessage = sprErr ?? "Could not read sprinklers inside zone.";
                return false;
            }
            if (sprinklers.Count == 0 && doc != null)
            {
                ApplySprinklersCommand.RunApplySprinklersFallbackSequence(doc, zone, boundaryHandleHex);
                SprinklerHeadReader2d.TryReadSprinklerHeadPointsForZoneRouting(db, zoneRing, boundaryHandleHex, clusterTol, out sprinklers, out _);
            }
            if (sprinklers.Count == 0)
            {
                errorMessage = "No sprinklers detected for this zone after automatically running Apply sprinklers. " +
                    "Check the zone boundary or run Apply sprinklers manually.";
                return false;
            }

            // ── Detect existing trunk at any angle ────────────────────────────────
            // If the user has already drawn (or manually adjusted) a trunk for this zone,
            // preserve it and only update the connector from the shaft to that trunk.
            // This makes routing idempotent with respect to manually rotated/slanted pipes.
            var existingSegments = MainPipeDetector.FindMainPipes(db, boundaryHandleHex);
            if (existingSegments.Count > 0)
            {
                AgentLog.Write("TryRouteMainPipeForZone",
                    "Existing trunk detected (" + existingSegments.Count + " segment(s)). Routing connector only.");

                var shaft2d = new Point2d(shaft.X, shaft.Y);
                double eps = Math.Max(BoundaryEntityToClosedLwPolyline.CoincidentTolerance(db), 1e-9);
                var connPath = AngleTrunkRouting2d.RouteConnector(shaft2d, existingSegments, zoneRing, eps);

                double angleDeg = MainPipeDetector.DominantAngleDeg(existingSegments);
                routeSummary = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Preserved existing trunk ({0} segment(s), angle ≈ {1:F1}°). Connector re-routed from shaft.",
                    existingSegments.Count, double.IsNaN(angleDeg) ? 0 : angleDeg);

                // Only erase+redraw the connector (trunk stays as-is).
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId pipeLayerId = SprinklerLayers.EnsureMainPipeLayer(tr, db);
                    SprinklerXData.EnsureRegApp(tr, db);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // Erase only connector entities (not trunk or caps).
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        Entity ent = null;
                        try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                        if (ent == null) continue;
                        if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                            !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!SprinklerXData.IsTaggedConnector(ent)) continue;
                        ent.UpgradeOpen();
                        try { ent.Erase(); } catch { /* ignore */ }
                    }

                    double w = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                    if (w <= 0) w = 1.0;

                    if (connPath != null && connPath.Count >= 2)
                    {
                        var conn = new Polyline();
                        conn.SetDatabaseDefaults(db);
                        conn.LayerId = pipeLayerId;
                        conn.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256);
                        conn.ConstantWidth = w;
                        conn.Elevation = zone.Elevation;
                        conn.Normal = zone.Normal;
                        for (int i = 0; i < connPath.Count; i++)
                            conn.AddVertexAt(i, connPath[i], 0, 0, 0);
                        conn.Closed = false;
                        SprinklerXData.TagAsConnector(conn);
                        SprinklerXData.ApplyZoneBoundaryTag(conn, boundaryHandleHex);
                        ms.AppendEntity(conn);
                        tr.AddNewlyCreatedDBObject(conn, true);
                    }

                    tr.Commit();
                }

                AgentLog.Write("TryRouteMainPipeForZone", "Connector-only update done: " + routeSummary);
                return true;
            }

            // ── Fresh routing (no existing trunk) ────────────────────────────────
            MainPipeRouting2d.RouteResult route = null;
            string routeErr = null;
            string usedStrategy = null;
            foreach (var s in strategies)
            {
                AgentLog.Write("TryRouteMainPipeForZone", "MainPipeRouting2d.TryRoute attempt strategy=" + s);
                if (MainPipeRouting2d.TryRoute(
                        zone,
                        shaft,
                        db,
                        sprinklerSpacingMeters: spacingM,
                        sprinklerCoverageRadiusMeters: radiusM,
                        out route,
                        out routeErr,
                        preferredOrientation: preferredOrientation,
                        strategy: s,
                        skeletonCellSizeMeters: skeletonCellSizeMeters,
                        skeletonMinClearanceMeters: skeletonMinClearanceMeters,
                        skeletonPruneBranchLengthMeters: skeletonPruneBranchLengthMeters,
                        mainPipeLengthPenalty: mainPipeLengthPenalty,
                        sprinklersOverride: sprinklers))
                {
                    usedStrategy = s;
                    break;
                }
            }

            if (route == null)
            {
                AgentLog.Write("TryRouteMainPipeForZone", "TryRoute failed (all strategies): " + routeErr);
                errorMessage = routeErr;
                return false;
            }

            if (string.IsNullOrWhiteSpace(strategy) && strategies.Length > 1 && !string.IsNullOrWhiteSpace(usedStrategy))
                route.Summary = "Strategy " + usedStrategy + ". " + (route.Summary ?? string.Empty);

            AgentLog.Write("TryRouteMainPipeForZone", "TryRoute succeeded (" + usedStrategy + "), drawing entities");
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId pipeLayerId = SprinklerLayers.EnsureMainPipeLayer(tr, db);
                SprinklerXData.EnsureRegApp(tr, db);

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Re-route safety: erase any existing zone-tagged trunk/connector/caps so we don't stack duplicates.
                EraseExistingMainPipeEntitiesForZone(tr, ms, boundaryHandleHex);

                double w = NfpaBranchPipeSizing.GetMainTrunkPolylineDisplayWidthDu(db);
                if (w <= 0) w = 1.0;

                // Draw trunk (trim ends so it doesn't touch the zone boundary).
                var trunk = new Polyline();
                trunk.SetDatabaseDefaults(db);
                trunk.LayerId = pipeLayerId;
                trunk.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                trunk.ConstantWidth = w;
                trunk.Elevation = zone.Elevation;
                trunk.Normal = zone.Normal;
                var trimmedTrunkPath = TryTrimTrunkPathEnds(db, route.TrunkIsHorizontal, route.TrunkPath);
                for (int i = 0; i < trimmedTrunkPath.Count; i++)
                    trunk.AddVertexAt(i, trimmedTrunkPath[i], 0, 0, 0);
                trunk.Closed = false;
                SprinklerXData.TagAsTrunk(trunk);
                SprinklerXData.ApplyZoneBoundaryTag(trunk, boundaryHandleHex);
                ms.AppendEntity(trunk);
                tr.AddNewlyCreatedDBObject(trunk, true);

                // Draw connector.
                var conn = new Polyline();
                conn.SetDatabaseDefaults(db);
                conn.LayerId = pipeLayerId;
                conn.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                conn.ConstantWidth = w;
                conn.Elevation = zone.Elevation;
                conn.Normal = zone.Normal;
                for (int i = 0; i < route.ConnectorPath.Count; i++)
                    conn.AddVertexAt(i, route.ConnectorPath[i], 0, 0, 0);
                conn.Closed = false;
                SprinklerXData.TagAsConnector(conn);
                SprinklerXData.ApplyZoneBoundaryTag(conn, boundaryHandleHex);
                ms.AppendEntity(conn);
                tr.AddNewlyCreatedDBObject(conn, true);

                TryDrawTrunkEndCaps(
                    tr, db, pipeLayerId, w, zone.Elevation, zone.Normal,
                    route.TrunkIsHorizontal, trimmedTrunkPath, ms, boundaryHandleHex);

                tr.Commit();
            }

            AgentLog.Write("TryRouteMainPipeForZone", "entities committed, done");
            routeSummary = route.Summary;
            return true;
        }

        /// <summary>When automatic routing sees several shafts, pick a stable feed riser instead of relying on iterate order.</summary>
        private static Point3d PickShaftNearestZoneCentroid2d(Polyline zone, List<Point3d> shaftsInside)
        {
            if (shaftsInside == null || shaftsInside.Count == 0)
                return default;
            if (shaftsInside.Count == 1)
                return shaftsInside[0];

            Point2d target;
            try
            {
                var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (ring != null && ring.Count >= 3)
                    target = PolygonUtils.ApproxCentroidAreaWeighted(ring);
                else
                {
                    var ext = zone.GeometricExtents;
                    target = new Point2d(
                        0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                        0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                }
            }
            catch
            {
                var ext = zone.GeometricExtents;
                target = new Point2d(
                    0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                    0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
            }

            Point3d best = shaftsInside[0];
            double bestD2 = double.MaxValue;
            for (int i = 0; i < shaftsInside.Count; i++)
            {
                var s = shaftsInside[i];
                double dx = s.X - target.X;
                double dy = s.Y - target.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = s;
                }
            }
            return best;
        }

        private static void EraseExistingMainPipeEntitiesForZone(Transaction tr, BlockTableRecord ms, string boundaryHandleHex)
        {
            if (tr == null || ms == null || string.IsNullOrWhiteSpace(boundaryHandleHex))
                return;

            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                if (ent == null) continue;

                if (!SprinklerXData.TryGetZoneBoundaryHandle(ent, out var h) ||
                    !string.Equals(h, boundaryHandleHex, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only erase main-pipe entities created by routing (trunk, connector, and end caps).
                if (!SprinklerXData.IsTaggedTrunk(ent) &&
                    !SprinklerXData.IsTaggedConnector(ent) &&
                    !SprinklerXData.IsTaggedTrunkCap(ent))
                    continue;

                ent.UpgradeOpen();
                try { ent.Erase(); } catch { /* ignore */ }
            }
        }

        private static bool TrySelectShaftBlock(Editor ed, Database db, out Point3d shaftPoint, out string errorMessage)
            => TrySelectShaftBlock(ed, db, out shaftPoint, out _, out errorMessage);

        private static bool TrySelectShaftBlock(Editor ed, Database db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string errorMessage)
        {
            shaftPoint = default;
            shaftEntityId = ObjectId.Null;
            errorMessage = null;
            if (ed == null || db == null)
            {
                errorMessage = "No active document.";
                return false;
            }

            var peo = new PromptEntityOptions("\nSelect shaft block: ");
            peo.SetRejectMessage("\nPlease select a block reference.\n");
            peo.AddAllowedClass(typeof(BlockReference), exactMatch: true);
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity;
                if (!(ent is BlockReference br))
                {
                    errorMessage = "Selected entity is not a block reference.";
                    return false;
                }

                string blockName = GetBlockName(br, tr);
                bool isShaft =
                    (!string.IsNullOrWhiteSpace(blockName) && blockName.IndexOf("shaft", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(br.Layer) && br.Layer.IndexOf("shaft", System.StringComparison.OrdinalIgnoreCase) >= 0);
                if (!isShaft)
                {
                    errorMessage = "Selected block does not look like a shaft (name/layer must contain \"shaft\").";
                    return false;
                }

                shaftPoint = br.Position;
                shaftEntityId = per.ObjectId;
                tr.Commit();
                return true;
            }
        }

        /// <summary>
        /// Resolves the zone boundary that was explicitly assigned to this shaft via ASSIGNSHAFTOZONE xdata.
        /// Returns false (no error) when no assignment exists — caller should fall back to spatial lookup.
        /// </summary>
        internal static bool TryFindAssignedZoneForShaft(
            Database db,
            ObjectId shaftEntityId,
            out ObjectId zoneBoundaryEntityId,
            out Polyline zoneClone,
            out string errorMessage)
        {
            zoneBoundaryEntityId = ObjectId.Null;
            zoneClone = null;
            errorMessage = null;

            if (db == null || shaftEntityId.IsNull) return false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var shaftEnt = tr.GetObject(shaftEntityId, OpenMode.ForRead, false) as Entity;
                if (shaftEnt == null) { tr.Commit(); return false; }

                if (!SprinklerXData.TryGetZoneAssignmentHandles(shaftEnt, out var assignedHandles)
                    || assignedHandles == null || assignedHandles.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                // Prefer the most recently linked zone (manual ASSIGNSHAFTOZONE appends; exclusive manual assigns a single entry).
                for (int hi = assignedHandles.Count - 1; hi >= 0; hi--)
                {
                    string zh = assignedHandles[hi];
                    Handle h;
                    try { h = new Handle(Convert.ToInt64(zh, 16)); } catch { continue; }
                    ObjectId zId = ObjectId.Null;
                    try { zId = db.GetObjectId(false, h, 0); } catch { continue; }
                    if (zId.IsNull) continue;

                    Polyline zEnt;
                    try { zEnt = tr.GetObject(zId, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; } // catches eWasErased for old (deleted) zone handles
                    if (zEnt == null || zEnt.IsErased || !zEnt.Closed) continue;

                    // Reject entities that are not actual zone boundaries (e.g. floor boundary accidentally tagged).
                    bool isMcdZoneLayer = SprinklerLayers.IsMcdZoneOutlineLayerName(zEnt.Layer);
                    bool isUnifiedZoneLayer = SprinklerLayers.IsUnifiedZoneDesignLayerName(zEnt.Layer);
                    bool selfTagged =
                        SprinklerXData.TryGetZoneBoundaryHandle(zEnt, out var ztag) &&
                        !string.IsNullOrWhiteSpace(ztag) &&
                        string.Equals(ztag, zEnt.Handle.ToString(), System.StringComparison.OrdinalIgnoreCase);
                    // "MCD-zone boundary" layer is exclusive to zones — accept without self-tag.
                    // "sprinkler - zone" layer is shared — require self-tag to avoid picking up floor boundaries.
                    // For any other layer (e.g. manually placed), require self-tag as the only confirmation.
                    if (isMcdZoneLayer) { /* accept */ }
                    else if (isUnifiedZoneLayer && selfTagged) { /* accept */ }
                    else if (!isMcdZoneLayer && !isUnifiedZoneLayer && selfTagged) { /* accept — self-tag is sufficient */ }
                    else continue;

                    try
                    {
                        zoneClone = (Polyline)zEnt.Clone();
                        zoneBoundaryEntityId = zId;
                        tr.Commit();
                        return true;
                    }
                    catch { continue; }
                }

                tr.Commit();
            }

            return false;
        }

        private static bool TryFindZoneOutlineContainingPoint(
            Database db,
            Point2d point,
            out ObjectId zoneBoundaryEntityId,
            out Polyline zoneClone,
            out string errorMessage)
        {
            zoneBoundaryEntityId = ObjectId.Null;
            zoneClone = null;
            errorMessage = null;

            if (db == null)
            {
                errorMessage = "Database is null.";
                return false;
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                ObjectId bestId = ObjectId.Null;
                Polyline bestPl = null;
                double bestArea = double.PositiveInfinity;
                bool bestTaggedSelf = false;

                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    Polyline pl = null;
                    try { pl = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased) { continue; }
                    if (pl == null) continue;
                    if (!pl.Closed)
                        continue;
                    bool isMcdZone = SprinklerLayers.IsMcdZoneOutlineLayerName(pl.Layer);
                    bool isUnifiedZone = SprinklerLayers.IsUnifiedZoneDesignLayerName(pl.Layer);
                    if (!isMcdZone && !isUnifiedZone)
                        continue;

                    bool taggedSelf =
                        SprinklerXData.TryGetZoneBoundaryHandle(pl, out var h)
                        && !string.IsNullOrWhiteSpace(h)
                        && string.Equals(h, pl.Handle.ToString(), System.StringComparison.OrdinalIgnoreCase);

                    // "sprinkler - zone" is a shared layer — require self-tag to confirm it is a zone boundary.
                    // "MCD-zone boundary" is exclusive to zone boundaries — no self-tag required.
                    if (isUnifiedZone && !taggedSelf)
                        continue;

                    var ring = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(pl);
                    if (ring == null || ring.Count < 3)
                        continue;
                    if (!PointInPolygon(ring, point))
                        continue;

                    double area = double.PositiveInfinity;
                    try { area = System.Math.Abs(pl.Area); } catch { /* ignore */ }
                    if (double.IsNaN(area) || double.IsInfinity(area) || area <= 0)
                        area = double.PositiveInfinity;

                    bool choose =
                        bestId.IsNull
                        || (taggedSelf && !bestTaggedSelf)
                        || (taggedSelf == bestTaggedSelf && area < bestArea);

                    if (!choose)
                        continue;

                    bestId = id;
                    bestPl = pl;
                    bestArea = area;
                    bestTaggedSelf = taggedSelf;
                }

                if (bestId.IsNull || bestPl == null)
                {
                    errorMessage =
                        "No zone is assigned to this shaft.\n" +
                        "Use \"Assign shaft to zone\" to link this shaft to a zone first.";
                    return false;
                }

                // Clone for downstream code that disposes the polyline.
                try { zoneClone = (Polyline)bestPl.Clone(); }
                catch
                {
                    errorMessage = "Failed to clone zone boundary polyline.";
                    return false;
                }

                zoneBoundaryEntityId = bestId;
                tr.Commit();
                return true;
            }
        }

        private static bool PointInPolygon(IList<Point2d> poly, Point2d p)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                bool intersect =
                    ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static void TryDrawTrunkEndCaps(
            Transaction tr,
            Database db,
            ObjectId pipeLayerId,
            double width,
            double elevation,
            Vector3d normal,
            bool trunkHorizontal,
            List<Point2d> trunkPath,
            BlockTableRecord ms,
            string zoneBoundaryHandleHex)
        {
            if (tr == null || db == null || ms == null || trunkPath == null || trunkPath.Count < 2)
                return;

            double capLen = 1.0;
            try
            {
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.30, out double du) && du > 0)
                    capLen = du;
            }
            catch { /* ignore */ }

            double h = 0.5 * capLen;
            int last = trunkPath.Count - 1;
            var a = trunkPath[0];
            var b = trunkPath[last];

            // Direction of the first/last segment — works for both axis-aligned single-segment
            // trunks and multi-vertex skeleton paths.
            var aNext = trunkPath[1];
            var bPrev = trunkPath[last - 1];

            DrawCapAt(a, new Vector2d(aNext.X - a.X, aNext.Y - a.Y));
            DrawCapAt(b, new Vector2d(b.X - bPrev.X, b.Y - bPrev.Y));

            void DrawCapAt(Point2d p, Vector2d along)
            {
                // Perpendicular (rotate 90°): (x, y) → (-y, x).
                double aLen = Math.Sqrt(along.X * along.X + along.Y * along.Y);
                double nx, ny;
                if (aLen < 1e-9)
                {
                    // Fallback to trunkHorizontal flag.
                    if (trunkHorizontal) { nx = 0; ny = 1; }
                    else { nx = 1; ny = 0; }
                }
                else
                {
                    nx = -along.Y / aLen;
                    ny =  along.X / aLen;
                }

                var cap = new Polyline();
                cap.SetDatabaseDefaults(db);
                cap.LayerId = pipeLayerId;
                cap.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                cap.ConstantWidth = width;
                cap.Elevation = elevation;
                cap.Normal = normal;
                cap.AddVertexAt(0, new Point2d(p.X - nx * h, p.Y - ny * h), 0, 0, 0);
                cap.AddVertexAt(1, new Point2d(p.X + nx * h, p.Y + ny * h), 0, 0, 0);
                cap.Closed = false;
                SprinklerXData.TagAsTrunkCap(cap);
                SprinklerXData.ApplyZoneBoundaryTag(cap, zoneBoundaryHandleHex);
                ms.AppendEntity(cap);
                tr.AddNewlyCreatedDBObject(cap, true);
            }
        }

        /// <summary>
        /// Trim each end of the trunk polyline inward along its own first/last segment by
        /// <c>trimDu</c>. Works for single-segment axis-aligned trunks and for multi-vertex
        /// skeleton trunks (L / zig-zag). Bails out if the first or last segment is too short
        /// to absorb the trim.
        /// </summary>
        private static List<Point2d> TryTrimTrunkPathEnds(Database db, bool trunkHorizontal, List<Point2d> trunkPath)
        {
            if (db == null || trunkPath == null || trunkPath.Count < 2)
                return trunkPath ?? new List<Point2d>();

            double trimDu = 0;
            try
            {
                // Keep trunk visually “end-to-end” without touching the boundary.
                // 0.30m trim was too aggressive for many drawings; use a small trim instead.
                if (DrawingUnitsHelper.TryMetersToDrawingLength(db.Insunits, 0.05, out double du) && du > 0)
                    trimDu = du;
            }
            catch { /* ignore */ }

            if (!(trimDu > 0))
                return trunkPath;

            int last = trunkPath.Count - 1;
            var p0 = trunkPath[0];
            var p1 = trunkPath[1];
            var pN = trunkPath[last];
            var pNm1 = trunkPath[last - 1];

            double firstLen = p0.GetDistanceTo(p1);
            double lastLen = pN.GetDistanceTo(pNm1);
            // Require room on both ends for the trim; otherwise leave trunk as-is.
            if (firstLen <= trimDu * 1.1 || lastLen <= trimDu * 1.1)
                return trunkPath;

            var outList = new List<Point2d>(trunkPath);
            // First endpoint → move toward p1.
            double fx = p1.X - p0.X, fy = p1.Y - p0.Y;
            double fl = Math.Sqrt(fx * fx + fy * fy);
            if (fl > 1e-9)
                outList[0] = new Point2d(p0.X + fx / fl * trimDu, p0.Y + fy / fl * trimDu);

            // Last endpoint → move toward pNm1.
            double lx = pNm1.X - pN.X, ly = pNm1.Y - pN.Y;
            double ll = Math.Sqrt(lx * lx + ly * ly);
            if (ll > 1e-9)
                outList[last] = new Point2d(pN.X + lx / ll * trimDu, pN.Y + ly / ll * trimDu);

            return outList;
        }

        private static bool TryFindNearestShaftToZone(Database db, Polyline zone, out Point3d shaft, out string errorMessage)
        {
            shaft = default;
            errorMessage = null;

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
                    if (ent == null) continue;
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
                errorMessage = "No shaft blocks found in the drawing (block name or layer must contain \"shaft\").";
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
    }
}

