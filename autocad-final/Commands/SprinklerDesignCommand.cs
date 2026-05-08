using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    /// <summary>
    /// Full automated workflow for one floor zone, or incremental redesign when a tagged trunk already exists.
    /// User selects the closed floor boundary on layer <see cref="SprinklerLayers.WorkLayer"/>.
    /// </summary>
    public class SprinklerDesignCommand
    {
        /// <summary>Palette and command line entry (short alias — reliable from modeless UI).</summary>
        [CommandMethod("SPRINKDESIGN", CommandFlags.Modal)]
        [CommandMethod("SPRINKLERSPRINKLERDESIGN", CommandFlags.Modal)]
        public void SprinklerDesign()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelectOnWorkLayer(ed, out var zone, out ObjectId boundaryEntityId))
            {
                ed.WriteMessage(
                    "\nDesign/Re-design cancelled, or pick a closed polyline on layer \"" +
                    SprinklerLayers.WorkLayer + "\" (floor boundary).\n");
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
                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }

                if (SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out _))
                {
                    ed.WriteMessage("\nExisting main pipe found — redesign keeps trunk and sprinkler head positions; updating connector, caps, and branch piping.\n");
                    if (!SprinklerZoneRedesignFromTrunk.TryRun(
                            doc,
                            db,
                            ed,
                            zone,
                            zoneRing,
                            boundaryHandleHex,
                            trunkId,
                            regenerateSprinklerGrid: false,
                            selectedShaftPoint: null,
                            out string err))
                    {
                        if (!string.IsNullOrEmpty(err))
                            PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, err, MessageBoxIcon.Warning);
                        return;
                    }

                    ed.WriteMessage("\nDesign/Re-design completed (incremental).\n");
                    return;
                }

                ed.WriteMessage("\nNew design: routing main pipe and placing sprinklers from grid.\n");

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    int cleared = SprinklerZoneAutomationCleanup.ClearPriorAutomatedContent(
                        tr,
                        ms,
                        zoneRing,
                        boundaryHandleHex,
                        boundaryEntityId);
                    tr.Commit();
                    if (cleared > 0)
                        ed.WriteMessage("\nCleared " + cleared.ToString() + " prior sprinkler entities for this zone.\n");
                }

                if (!RouteMainPipeCommand.TryRouteMainPipeForZone(
                        doc,
                        zone,
                        boundaryHandleHex,
                        out string routeErr,
                        out string routeSummary))
                {
                    if (!string.IsNullOrEmpty(routeErr))
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Main pipe: " + routeErr, MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\nMain pipe routed. " + (routeSummary ?? string.Empty) + "\n");

                if (!ApplySprinklersCommand.TryApplySprinklersForZone(
                        doc,
                        zone,
                        boundaryHandleHex,
                        out string applyMsg,
                        useTrunkAnchoredGrid: true))
                {
                    if (!string.IsNullOrEmpty(applyMsg))
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, applyMsg, MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\n" + applyMsg + "\n");

                if (!AttachBranchesCommand.TryAttachBranchesForZone(doc, db, zone, zoneRing, boundaryHandleHex, out string branchMsg))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Branch pipes: " + (branchMsg ?? "failed."), MessageBoxIcon.Warning);
                    return;
                }
                if (!AttachBranchesCommand.TryPlaceReducersForZone(doc, db, zone, zoneRing, boundaryHandleHex, routeBranchPipesFromConnectorFirst: false, ObjectId.Null, out string redMsg))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Reducers: " + (redMsg ?? "failed."), MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\n" + branchMsg + "\n");
                ed.WriteMessage("\n" + redMsg + "\n");
                ed.WriteMessage("\nDesign/Re-design completed (new route).\n");

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
