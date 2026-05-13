using System.Collections.Generic;
using System.Windows.Forms;
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
    /// Routes an orthogonal feeder from a picked zone main into a room (sub-main on main layer), then
    /// orthogonal branches from that feeder to room heads tagged to the parent zone.
    /// </summary>
    public class RouteRoomSubMainCommand
    {
        [CommandMethod("SPRINKLERROUTEROOMSUBMAIN", CommandFlags.Modal)]
        [CommandMethod("ROUTEROOMSUBMAIN", CommandFlags.Modal)]
        public void RouteRoomSubMain()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var db = doc.Database;

            ed.WriteMessage(
                "\nRoom sub-main: pick the room outline, then the zone main polyline to tap. " +
                "Creates an L-shaped feeder on the main layer plus orthogonal branches to room heads.\n");

            if (!SelectPolygonBoundary.TrySelectOnNamedLayer(
                    ed,
                    SprinklerLayers.McdFloorBoundaryLayer,
                    "\nSelect ROOM closed polyline (layer \"" + SprinklerLayers.McdFloorBoundaryLayer + "\"): ",
                    out var room,
                    out ObjectId roomId))
            {
                ed.WriteMessage("\nCancelled.\n");
                return;
            }

            var peoMain = new PromptEntityOptions("\nSelect MAIN pipe polyline to tap (zone main on main-pipe layer): ");
            peoMain.SetRejectMessage("\nSelect a polyline on a main pipe layer.\n");
            peoMain.AddAllowedClass(typeof(Polyline), exactMatch: true);
            var perMain = ed.GetEntity(peoMain);
            if (perMain.Status != PromptStatus.OK)
            {
                try { room.Dispose(); } catch { /* ignore */ }
                ed.WriteMessage("\nCancelled.\n");
                return;
            }

            try
            {
                if (!RoomParentZoneResolver.TryResolveParentZoneForRoom(db, room, out ObjectId zoneId, out string zoneHex, out string zoneErr))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, zoneErr ?? "Could not resolve parent zone.", MessageBoxIcon.Warning);
                    return;
                }

                Polyline zonePl = null;
                List<Point2d> zoneRing = null;
                List<Point2d> roomRing = null;

                using (doc.LockDocument())
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    zonePl = tr0.GetObject(zoneId, OpenMode.ForRead, false) as Polyline;
                    if (zonePl == null || !zonePl.Closed)
                    {
                        tr0.Commit();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Parent zone boundary is invalid.", MessageBoxIcon.Warning);
                        return;
                    }
                    zoneRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(zonePl);
                    roomRing = PolylineClosedBoundaryRingSampler2d.ConvertPolylineToRingPoints(room);
                    tr0.Commit();
                }

                if (zoneRing == null || zoneRing.Count < 3 || roomRing == null || roomRing.Count < 3)
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Could not sample room or zone boundary.", MessageBoxIcon.Warning);
                    return;
                }

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var mainPl = tr.GetObject(perMain.ObjectId, OpenMode.ForRead, false) as Polyline;
                    if (mainPl == null || mainPl.IsErased)
                    {
                        tr.Commit();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, "Main pipe selection is invalid.", MessageBoxIcon.Warning);
                        return;
                    }
                    if (!RoomSubMainBranchRouting.IsEligibleTapMain(mainPl))
                    {
                        tr.Commit();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "Selected polyline is not a valid main pipe (wrong layer, or is a trunk cap).",
                            MessageBoxIcon.Warning);
                        return;
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    if (!RoomSubMainBranchRouting.TryRouteFeederAndBranches(
                            tr,
                            db,
                            ms,
                            room,
                            mainPl,
                            roomRing,
                            zoneRing,
                            zoneHex,
                            onlyTheseHeadIds: null,
                            out int feederVerts,
                            out int branchPls,
                            out _,
                            out string routeErr))
                    {
                        tr.Abort();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, routeErr ?? "Room sub-main routing failed.", MessageBoxIcon.Warning);
                        return;
                    }

                    tr.Commit();
                    ed.WriteMessage(
                        "\nRoom sub-main complete. Feeder vertices=" + feederVerts.ToString() +
                        ", branch polylines=" + branchPls.ToString() + ".\n");
                }

                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { room.Dispose(); } catch { /* ignore */ }
            }
        }

    }
}
