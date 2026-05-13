using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Agent;
using autocad_final.AreaWorkflow;
using autocad_final.Licensing;
using autocad_final.UI;
using autocad_final.Workflows.Placement;

namespace autocad_final.Commands
{
    public class PlaceRoomSprinklersCommand
    {
        [CommandMethod("PLACESPRINKLERSROOM", CommandFlags.Modal)]
        [CommandMethod("SPRINKLERPLACESPRINKLERSROOM", CommandFlags.Modal)]
        public void PlaceRoomSprinklers()
        {
            AgentLog.Write("PlaceRoomSprinklers", "command start");
            var ctx = CadContext.TryFromActiveDocument();
            if (ctx == null) return;

            var ed = ctx.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;
            var doc = ctx.Document;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                SprinklerLayers.EnsureMcdFloorBoundaryLayer(tr, db);
                tr.Commit();
            }

            string prompt =
                "\nSelect room boundary (closed polyline on layer \"" + SprinklerLayers.McdFloorBoundaryLayer +
                "\", with DBText/MText label inside — inner loop on the floor plan): ";

            if (!SelectPolygonBoundary.TrySelectOnNamedLayer(
                    ed,
                    SprinklerLayers.McdFloorBoundaryLayer,
                    prompt,
                    out var room,
                    out var boundaryEntityId))
            {
                ed.WriteMessage(
                    "\nPlace room sprinklers cancelled, or pick a closed polyline on layer \"" +
                    SprinklerLayers.McdFloorBoundaryLayer + "\".\n");
                return;
            }

            try
            {
                if (!PlaceRoomSprinklersWorkflow.TryRun(doc, room, boundaryEntityId, out string workflowMsg))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(
                        ed,
                        workflowMsg ?? "Place room sprinklers failed.",
                        MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\n" + workflowMsg + "\n");
                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { room.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
