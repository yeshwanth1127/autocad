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
    public class RebuildFromTrunkCommand
    {
        [CommandMethod("SPRINKLERREBUILDFROMTRUNK", CommandFlags.Modal)]
        public void RebuildFromTrunk()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            if (!SelectPolygonBoundary.TrySelect(ed, out var zone, out ObjectId boundaryEntityId))
                return;

            string boundaryHandleHex;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                SprinklerXData.EnsureRegApp(tr0, db);
                if (!DbObjectSafeAccess.TryGetObject(tr0, boundaryEntityId, OpenMode.ForRead, out Entity boundaryEnt))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not read selected zone boundary. Select it again and retry.", MessageBoxIcon.Warning);
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

                if (!SprinklerTrunkLocator.TryFindTaggedTrunkInZone(db, zoneRing, out ObjectId trunkId, out string trunkErr))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, trunkErr ?? "Could not find main trunk.", MessageBoxIcon.Warning);
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
                        regenerateSprinklerGrid: true,
                        selectedShaftPoint: null,
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
    }
}
