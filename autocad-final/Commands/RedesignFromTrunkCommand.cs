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

            if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out var zone, out string zoneErr))
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
