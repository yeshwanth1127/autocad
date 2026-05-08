using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using autocad_final.Licensing;
using autocad_final.AreaWorkflow;
using autocad_final.UI;
using System.Windows.Forms;

namespace autocad_final.Commands
{
    public class CheckSprinklersAndFixCommand
    {
        [CommandMethod("CHECKSPRINKLERSFIX", CommandFlags.Modal)]
        [CommandMethod("SPRINKLERCHECKSPRINKLERSFIX", CommandFlags.Modal)]
        public void CheckSprinklersAndFix()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            if (!TrialGuard.EnsureActive(ed)) return;

            if (!SelectPolygonBoundary.TrySelect(ed, out var zone, out ObjectId boundaryEntityId))
            {
                ed.WriteMessage(
                    "\nCheck sprinklers cancelled, or pick a closed polyline on layer \"" +
                    SprinklerLayers.McdZoneBoundaryLayer + "\" (or legacy \"" +
                    SprinklerLayers.ZoneGlobalBoundaryLayer + "\").\n");
                return;
            }

            try
            {
                if (!CheckSprinklersAndFixWorkflow.TryRun(doc, zone, boundaryEntityId, out string msg))
                {
                    PaletteCommandErrorUi.ShowDialogThenCommandLine(ed, msg ?? "Check sprinklers failed.", MessageBoxIcon.Warning);
                    return;
                }

                ed.WriteMessage("\n" + msg + "\n");
                try { ed.Regen(); } catch { /* ignore */ }
            }
            finally
            {
                try { zone.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
