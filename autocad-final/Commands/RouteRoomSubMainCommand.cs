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
    /// Picks a room outline, resolves its majority parent zone, and chains the room's heads along the room grid,
    /// connecting each chain to the nearest branch pipe of that zone (main pipe as fallback).
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
                "\nRoom branches: pick the room outline. Heads are chained along the room grid and connected to the " +
                "nearest branch pipe of the room's zone (main pipe as fallback).\n");

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
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    RoomSubMainBranchRouting.CollectZoneSupply(
                        tr, ms, zoneRing, zoneHex,
                        out var branchSupply, out var mainSupply);
                    if (branchSupply.Count == 0 && mainSupply.Count == 0)
                    {
                        tr.Commit();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(
                            ed,
                            "No branch or main pipe found in the room's zone. Route the main pipe and branches first.",
                            MessageBoxIcon.Warning);
                        return;
                    }

                    if (!RoomSubMainBranchRouting.TryRouteRoomBranches(
                            tr,
                            db,
                            ms,
                            room.Elevation,
                            roomRing,
                            zoneRing,
                            zoneHex,
                            branchSupply,
                            mainSupply,
                            onlyTheseHeadIds: null,
                            out int branchPls,
                            out _,
                            out string routeErr))
                    {
                        tr.Abort();
                        PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, routeErr ?? "Room branch routing failed.", MessageBoxIcon.Warning);
                        return;
                    }

                    tr.Commit();
                    ed.WriteMessage("\nRoom branches complete. Branch polylines=" + branchPls.ToString() + ".\n");
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
