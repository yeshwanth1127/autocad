using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class RedesignFromTrunkCommand
    {
        [CommandMethod("SPRINKLERREDESIGN", CommandFlags.Modal)]
        public void RedesignFromTrunk()
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

            // Resolve zone the same way as "Route main pipe": explicit shaft→zone assignment first, then fall
            // back to spatial containment of the shaft point. Without the spatial fallback a missing/stale
            // assignment forced a hard stop even when the shaft clearly sits inside a zone.
            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out _)
                && !RouteMainPipeCommand.TryFindZoneOutlineContainingPoint(db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                    zoneErr ?? "No zone is assigned to this shaft.\nUse \"Assign shaft to zone\" to link this shaft to a zone first.",
                    MessageBoxIcon.Warning);
                return;
            }

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                if (!DbObjectSafeAccess.TryGetObject(tr0, boundaryEntityId, OpenMode.ForRead, out Entity boundaryEnt))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not read assigned zone boundary. Reassign shaft to zone and retry.", MessageBoxIcon.Warning);
                    return;
                }
                boundaryHandleHex = boundaryEnt.Handle.ToString();
                tr0.Commit();
            }

            try
            {
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }

                if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out string trunkErr, boundaryHandleHex))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                        trunkErr ?? "No main pipe found in zone. Run 'Route main pipe' first.",
                        MessageBoxIcon.Warning);
                    return;
                }

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (!SprinklerZoneRedesignFromTrunk.TryRun(
                                doc,
                                db,
                                ed,
                                zone,
                                zoneRing,
                                boundaryHandleHex,
                                trunkId,
                                regenerateSprinklerGrid: false,
                                selectedShaftPoint: shaftPoint,
                                out string err))
                        {
                            if (!string.IsNullOrEmpty(err))
                                PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err, MessageBoxIcon.Warning);
                            return;
                        }

                        return;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (
                        ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased && attempt == 0)
                    {
                        // Redesign touches many entities; after manual edits some stale IDs can appear transiently.
                        // Retry once against the refreshed database state.
                        try { ed.Regen(); } catch { /* ignore */ }
                        continue;
                    }
                }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }

        [CommandMethod("SPRINKLERREDESIGNALL", CommandFlags.Modal)]
        public void RedesignFromTrunkAllForFloor()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            // Same floor-boundary picker as "Create zones" / "Route main pipes (all zones)".
            if (!SelectPolygonBoundary.TrySelectOnNamedLayer(
                    ed,
                    SprinklerLayers.McdFloorBoundaryLayer,
                    SelectPolygonBoundary.FloorBoundaryPickPromptForLayer(SprinklerLayers.McdFloorBoundaryLayer),
                    out var floorBoundary,
                    out var floorBoundaryEntityId))
                return;

            try
            {
                string floorHandleHex;
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    SprinklerXData.EnsureRegApp(tr0, db);
                    floorHandleHex = tr0.GetObject(floorBoundaryEntityId, OpenMode.ForRead).Handle.ToString();
                    tr0.Commit();
                }

                var zones = RouteMainPipeCommand.CollectZoneOutlinesForFloor(db, floorBoundary, floorHandleHex);
                if (zones.Count == 0)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                        "No zones found for this floor boundary.\nRun \"Create zones\" first, then route branches.",
                        MessageBoxIcon.Warning);
                    return;
                }

                int routed = 0;
                var failures = new System.Collections.Generic.List<string>();
                try
                {
                    foreach (var z in zones)
                    {
                        if (TryRedesignZone(doc, db, ed, z, out string err))
                            routed++;
                        else
                            failures.Add("Zone [" + z.HandleHex + "]: " + (err ?? "branch routing failed."));
                    }
                }
                finally
                {
                    foreach (var z in zones)
                        try { z.Zone?.Dispose(); } catch { /* ignore */ }
                }

                try { ed.Regen(); } catch { /* ignore */ }

                string summary = "Routed branches for " + routed.ToString() + " of " +
                                 zones.Count.ToString() + " zone(s) on this floor.";
                if (failures.Count > 0)
                    summary += "\nSkipped " + failures.Count.ToString() + ":\n - " +
                               string.Join("\n - ", failures);

                if (routed == 0)
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, summary, MessageBoxIcon.Warning);
                else
                    ed.WriteMessage("\n" + summary + "\n");
            }
            finally
            {
                try { floorBoundary.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Routes branches for a single zone: locates the tagged trunk and its assigned shaft, then runs the
        /// redesign-from-trunk pass. Mirrors the single-zone command (incl. one retry on a transient erased id).
        /// </summary>
        private static bool TryRedesignZone(Document doc, Database db, Editor ed, RouteMainPipeCommand.ZoneOutlineRef z, out string errorMessage)
        {
            errorMessage = null;
            var zone = z.Zone;
            string boundaryHandleHex = z.HandleHex;
            if (zone == null || string.IsNullOrEmpty(boundaryHandleHex))
            {
                errorMessage = "Invalid zone.";
                return false;
            }

            var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
            if (zoneRing == null || zoneRing.Count < 3)
            {
                errorMessage = "Invalid zone boundary.";
                return false;
            }

            if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out string trunkErr, boundaryHandleHex))
            {
                errorMessage = trunkErr ?? "No main pipe found in zone. Run 'Route main pipes' first.";
                return false;
            }

            // Resolve the shaft assigned to this zone (xdata first, then spatial) so the redesign can validate
            // and route the shaft→trunk connector. Null is tolerated by the redesign pass.
            Point3d? shaftPoint = null;
            if (FindShaftsInsideBoundary.TryGetShaftForZone(db, boundaryHandleHex, zone, out var shaftInfo, out _))
                shaftPoint = shaftInfo.Position;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return SprinklerZoneRedesignFromTrunk.TryRun(
                        doc, db, ed, zone, zoneRing, boundaryHandleHex, trunkId,
                        regenerateSprinklerGrid: false,
                        selectedShaftPoint: shaftPoint,
                        out errorMessage);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) when (
                    ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.WasErased && attempt == 0)
                {
                    try { ed.Regen(); } catch { /* ignore */ }
                    continue;
                }
            }
            return false;
        }

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
                if (!DbObjectSafeAccess.TryGetObject(tr, per.ObjectId, OpenMode.ForRead, out Entity ent))
                {
                    errorMessage = "Selected shaft was erased by Undo. Select it again and retry.";
                    return false;
                }
                if (!(ent is BlockReference br))
                {
                    errorMessage = "Selected entity is not a block reference.";
                    return false;
                }

                string blockName = GetBlockName(br, tr);
                bool isShaft =
                    (!string.IsNullOrWhiteSpace(blockName) && blockName.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(br.Layer) && br.Layer.IndexOf("shaft", StringComparison.OrdinalIgnoreCase) >= 0);
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
