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

                if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out string trunkErr))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed,
                        trunkErr ?? "No main pipe found in zone. Run 'Route main pipe' first.",
                        MessageBoxIcon.Warning);
                    return;
                }

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
                var ent = tr.GetObject(per.ObjectId, OpenMode.ForRead, false) as Entity;
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
