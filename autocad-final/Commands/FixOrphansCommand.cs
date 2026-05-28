using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;
using autocad_final.Licensing;
using autocad_final.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.Commands
{
    /// <summary>
    /// Finds interior sprinklers in a zone with only one branch segment and links them to the next
    /// adjacent head on the same row/column. Edge (terminal) sprinklers are skipped.
    /// </summary>
    public class FixOrphansCommand
    {
        [CommandMethod("SPRINKLERFIXORPHANS", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;

            var db = doc.Database;

            try
            {
                if (!AttachBranchesCommand.TrySelectShaftBlock(ed, db, out Point3d shaftPoint, out ObjectId shaftEntityId, out string shaftErr))
                {
                    if (!string.IsNullOrWhiteSpace(shaftErr))
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, shaftErr, MessageBoxIcon.Warning);
                    return;
                }

                if (!RouteMainPipeCommand.TryFindAssignedZoneForShaft(db, shaftEntityId, out ObjectId boundaryEntityId, out Polyline zone, out _)
                    && !AttachBranchesCommand.TryFindZoneOutlineContainingPoint(
                        db, new Point2d(shaftPoint.X, shaftPoint.Y), out boundaryEntityId, out zone, out string zoneErr))
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

                var zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zone);
                if (zoneRing == null || zoneRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Invalid zone boundary.", MessageBoxIcon.Warning);
                    return;
                }

                using (doc.LockDocument())
                {
                    if (!ConnectBranchesManuallyCommand.TryFixOrphansForZone(
                            doc, db, boundaryHandleHex, zoneRing,
                            out string resultMessage, out int segmentsDrawn))
                    {
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            resultMessage ?? "Fix orphans failed.",
                            MessageBoxIcon.Warning);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(resultMessage))
                        ed.WriteMessage("\n" + resultMessage + "\n");

                    if (segmentsDrawn == 0)
                        ed.WriteMessage("\nNo orphan sprinklers needed new branch segments.\n");
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                PaletteCommandErrorUi.ShowDialogThenCommandLine(
                    ed,
                    "Fix orphans failed: " + ex.ErrorStatus + " / " + ex.Message,
                    MessageBoxIcon.Error);
            }
            catch (System.Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }
    }
}
